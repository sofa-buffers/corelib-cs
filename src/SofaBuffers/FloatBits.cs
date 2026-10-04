/*
 * SofaBuffers C# - generated-code support layer: bit-pattern equality of float
 * arrays, the comparison behind "this field equals its default, omit it".
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Runtime.InteropServices;

namespace sofab;

/// <summary>
/// Bit-pattern equality for <c>fp32</c> / <c>fp64</c> arrays — the comparison a
/// generated encoder makes between a field and its declared default.
/// </summary>
/// <remarks>
/// A field is omitted from the wire iff it equals its default (MESSAGE_SPEC §2),
/// and floats round-trip bit for bit (CORELIB_PLAN §4.6). "Equals" is therefore a
/// comparison of IEEE-754 <b>bit patterns</b>, not the IEEE <c>==</c> that
/// <see cref="System.Linq.Enumerable.SequenceEqual{T}(System.Collections.Generic.IEnumerable{T}, System.Collections.Generic.IEnumerable{T})"/>
/// performs: under <c>==</c>, <c>-0.0</c> equals <c>+0.0</c> (so a <c>-0.0</c>
/// element would be dropped as a default) and a NaN equals nothing, itself
/// included. Here <c>+0.0</c> and <c>-0.0</c> differ, and a NaN equals another NaN
/// exactly when every bit, payload included, is the same.
/// <para>
/// The code has the same shape for every schema — the element type is the only
/// thing that varies — so it lives in the corelib rather than being emitted into
/// every generated source tree (generator#636, #587). Both overloads take
/// <see cref="ReadOnlySpan{T}"/>, so a generated call passes the field array and a
/// constant default array alike (arrays convert implicitly). Neither allocates or
/// mutates.
/// </para>
/// </remarks>
public static class FloatBits
{
    /// <summary>
    /// True iff both spans have the same length and every element pair has the same
    /// 32-bit IEEE-754 bit pattern.
    /// </summary>
    /// <param name="a">The first array (the field).</param>
    /// <param name="b">The second array (the default).</param>
    /// <returns>Whether the two arrays are bit-for-bit identical.</returns>
    public static bool BitsEqual(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        // Length first: unequal lengths never touch an element. Equal-length
        // contiguous storage is then one vectorised byte compare.
        return a.Length == b.Length
            && MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));
    }

    /// <summary>
    /// True iff both spans have the same length and every element pair has the same
    /// 64-bit IEEE-754 bit pattern.
    /// </summary>
    /// <param name="a">The first array (the field).</param>
    /// <param name="b">The second array (the default).</param>
    /// <returns>Whether the two arrays are bit-for-bit identical.</returns>
    public static bool BitsEqual(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        return a.Length == b.Length
            && MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));
    }
}
