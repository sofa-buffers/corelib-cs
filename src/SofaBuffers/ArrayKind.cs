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
/// <remarks>
/// A fixlen array names its element <em>subtype</em> here — <see cref="Fp32"/> or
/// <see cref="Fp64"/>, never a collapsed "fixlen" category. CORELIB_PLAN §4.8
/// requires the subtype to decide whether the field is this array's value at all
/// (MESSAGE_SPEC §7.3) <em>before</em> any schema bound is applied, so the
/// receiver must be able to tell the two apart from the header hook alone.
/// The ordinals are shared across every push-API corelib in the family.
/// </remarks>
public enum ArrayKind
{
    /// <summary>Unsigned-integer elements, delivered through <see cref="IVisitor.Unsigned"/>.</summary>
    Unsigned = 0,

    /// <summary>Signed-integer elements, delivered through <see cref="IVisitor.Signed"/>.</summary>
    Signed = 1,

    /// <summary>32-bit float elements, delivered through <see cref="IVisitor.Fp32"/>.</summary>
    Fp32 = 2,

    /// <summary>64-bit float elements, delivered through <see cref="IVisitor.Fp64"/>.</summary>
    Fp64 = 3,
}
