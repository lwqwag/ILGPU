﻿// ---------------------------------------------------------------------------------------
//                                        ILGPU
//                        Copyright (c) 2016-2020 Marcel Koester
//                                    www.ilgpu.net
//
// File: ViewIntrinsics.cs
//
// This file is part of ILGPU and is distributed under the University of Illinois Open
// Source License. See LICENSE.txt for details
// ---------------------------------------------------------------------------------------

using ILGPU.IR;
using ILGPU.IR.Types;
using ILGPU.IR.Values;
using ILGPU.Resources;
using System;

namespace ILGPU.Frontend.Intrinsic
{
    enum ViewIntrinsicKind
    {
        GetViewLength,
        GetViewLongLength,
        GetViewLengthInBytes,
        GetSubView,
        GetSubViewImplicitLength,
        GetViewElementAddress,
        CastView,

        IsValidView,
        GetViewExtent,
        GetViewLongExtent,
        GetViewElementAddressByIndex,
        GetViewLinearElementAddress,
        AsLinearView,
        AlignTo
    }

    /// <summary>
    /// Marks view methods that are built in.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    sealed class ViewIntrinsicAttribute : IntrinsicAttribute
    {
        public ViewIntrinsicAttribute(ViewIntrinsicKind intrinsicKind)
        {
            IntrinsicKind = intrinsicKind;
        }

        public override IntrinsicType Type => IntrinsicType.View;

        /// <summary>
        /// Returns the assigned intrinsic kind.
        /// </summary>
        public ViewIntrinsicKind IntrinsicKind { get; }
    }

    partial class Intrinsics
    {
        /// <summary>
        /// Handles shared memory operations.
        /// </summary>
        /// <param name="context">The current invocation context.</param>
        /// <param name="attribute">The intrinsic attribute.</param>
        /// <returns>The resulting value.</returns>
        private static ValueReference HandleViewOperation(
            ref InvocationContext context,
            ViewIntrinsicAttribute attribute)
        {
            var builder = context.Builder;
            var location = context.Location;
            // These methods are instance calls -> load instance value
            var paramOffset = 0;
            var instanceValue = builder.CreateLoad(
                location,
                context[paramOffset++]);
            return attribute.IntrinsicKind switch
            {
                ViewIntrinsicKind.GetViewLength => builder.CreateGetViewLength(
                    location,
                    instanceValue),
                ViewIntrinsicKind.GetViewLongLength => builder.CreateGetViewLongLength(
                    location,
                    instanceValue),
                ViewIntrinsicKind.GetViewLengthInBytes => builder.CreateArithmetic(
                    location,
                    builder.CreateGetViewLongLength(location, instanceValue),
                    builder.CreateLongSizeOf(
                        location,
                        (instanceValue.Type as AddressSpaceType).ElementType),
                    BinaryArithmeticKind.Mul),
                ViewIntrinsicKind.GetSubView => builder.CreateSubViewValue(
                    location,
                    instanceValue,
                    context[paramOffset++],
                    context[paramOffset++]),
                ViewIntrinsicKind.GetSubViewImplicitLength =>
                    builder.CreateSubViewValue(
                        location,
                        instanceValue,
                        context[paramOffset],
                        builder.CreateArithmetic(
                            location,
                            builder.CreateGetViewLongLength(location, instanceValue),
                            context[paramOffset],
                            BinaryArithmeticKind.Sub)),
                ViewIntrinsicKind.GetViewElementAddress =>
                    GetViewElementAddress(
                        ref context,
                        instanceValue,
                        context[paramOffset++]),
                ViewIntrinsicKind.CastView => builder.CreateViewCast(
                    location,
                    instanceValue,
                    builder.CreateType(context.GetMethodGenericArguments()[0])),
                ViewIntrinsicKind.IsValidView => builder.CreateCompare(
                    location,
                    builder.CreateGetViewLongLength(location, instanceValue),
                    builder.CreatePrimitiveValue(location, 0L),
                    CompareKind.GreaterThan),
                ViewIntrinsicKind.GetViewExtent => builder.CreateIndex(
                    location,
                    builder.CreateGetViewLength(location, instanceValue)),
                ViewIntrinsicKind.GetViewLongExtent => builder.CreateIndex(
                    location,
                    builder.CreateGetViewLongLength(location, instanceValue)),
                ViewIntrinsicKind.GetViewElementAddressByIndex =>
                    GetViewElementAddress(
                        ref context,
                        instanceValue,
                        builder.CreateGetField(
                            location,
                            context[paramOffset++],
                            new FieldAccess(0))),
                ViewIntrinsicKind.GetViewLinearElementAddress =>
                    RemapToLinearElementAddress(ref context),
                ViewIntrinsicKind.AsLinearView => instanceValue,
                ViewIntrinsicKind.AlignTo =>
                    builder.CreateAlignViewTo(
                        location,
                        instanceValue,
                        context[paramOffset++]),
                _ => throw context.Location.GetNotSupportedException(
                    ErrorMessages.NotSupportedViewIntrinsic,
                    attribute.IntrinsicKind.ToString()),
            };
        }

        /// <summary>
        /// Constructs a new view-element access that is bounds checked in debug mode.
        /// </summary>
        private static ValueReference GetViewElementAddress(
            ref InvocationContext context,
            Value instanceValue,
            Value index)
        {
            // Load the corresponding view length
            var builder = context.Builder;
            var location = context.Location;

            // Build a new assertion
            if (context.Context.HasFlags(ContextFlags.EnableAssertions))
            {
                // Determine base offset and max length
                var baseOffset = builder.CreatePrimitiveValue(
                    location,
                    index.BasicValueType,
                    0L);
                var viewLength = index.BasicValueType == BasicValueType.Int64
                    ? builder.CreateGetViewLongLength(location, instanceValue)
                    : builder.CreateGetViewLength(location, instanceValue);

                // Verify the lower bound, which must be >= 0 in all cases:
                // index >= 0
                var lowerBoundsCheck = builder.CreateCompare(
                    location,
                    index,
                    baseOffset,
                    CompareKind.GreaterEqual);

                // If the length can be determined (>= 0), we have to verify the upper
                // bound too
                // length < 0 || index < length
                var upperBoundsCheck = builder.CreateArithmetic(
                    location,
                    builder.CreateCompare(
                        location,
                        viewLength,
                        builder.CreatePrimitiveValue(location, 0),
                        CompareKind.LessThan),
                    builder.CreateCompare(
                        location,
                        index,
                        viewLength,
                        CompareKind.LessThan),
                    BinaryArithmeticKind.Or);

                // Build the complete range condition check:
                // index >= 0 && (length < 0 || index < length)
                var inRange = builder.CreateArithmetic(
                    location,
                    lowerBoundsCheck,
                    upperBoundsCheck,
                    BinaryArithmeticKind.And);
                builder.CreateDebugAssert(
                    location,
                    inRange,
                    builder.CreatePrimitiveValue(location, "Index out of range"));
            }

            // Load the element index
            return builder.CreateLoadElementAddress(location, instanceValue, index);
        }

        /// <summary>
        /// Remaps intrinsic index-linearization functionality to a specific
        /// linearization function (see also <see cref="IndexTypeExtensions.
        /// GetViewLinearIndexMethod(Type, Type)"/>).
        /// </summary>
        /// <param name="context">The invocation context.</param>
        /// <returns>The remapped call reference.</returns>
        private static ValueReference RemapToLinearElementAddress(
            ref InvocationContext context)
        {
            var builder = context.Builder;

            // Extract the managed index type instance and resolve the index
            // linearization method to compute either an int or a long value.
            var methodGenerics = context.GetMethodGenericArguments();
            var typeGenerics = context.GetTypeGenericArguments();
            var linearMethod = IndexTypeExtensions.GetViewLinearIndexMethod(
                methodGenerics[0],
                typeGenerics[0]);
            var targetMethod = context.DeclareMethod(linearMethod);

            // Build a call to the specific access function
            var viewInstance = builder.CreateLoad(
                context.Location,
                context[0]);
            var callBuilder = context.Builder.CreateCall(
                context.Location,
                targetMethod);
            callBuilder.Add(viewInstance);
            for (int i = 1, e = context.NumArguments; i < e; ++i)
                callBuilder.Add(context[i]);
            return callBuilder.Seal();
        }
    }
}
