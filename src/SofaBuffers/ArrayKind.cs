/*
 * SofaBuffers C# - array element category.
 *
 * SPDX-License-Identifier: MIT
 */

namespace sofab;

/// <summary>
/// Element category of an array field, reported to an <see cref="IVisitor"/> via
/// <see cref="IVisitor.ArrayBegin"/> just before the elements are delivered.
/// </summary>
public enum ArrayKind
{
    /// <summary>Unsigned-integer elements, delivered through <see cref="IVisitor.Unsigned"/>.</summary>
    Unsigned,

    /// <summary>Signed-integer elements, delivered through <see cref="IVisitor.Signed"/>.</summary>
    Signed,

    /// <summary>Floating-point elements, delivered through <c>Fp32</c> / <c>Fp64</c>.</summary>
    Fixlen,
}
