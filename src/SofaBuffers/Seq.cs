/*
 * SofaBuffers C# - generated-code support layer: array growth for a decode
 * destination filled element by element.
 *
 * SPDX-License-Identifier: MIT
 */

using System;

namespace sofab;

/// <summary>
/// Array growth for generated decode destinations — the <b>support layer</b>, not
/// part of the codec.
/// </summary>
/// <remarks>
/// Nothing here touches the wire. This is what a generated message class does
/// <em>around</em> an <see cref="IVisitor"/> callback: enlarge the array it is
/// filling as elements actually arrive. Its code has the same shape for every
/// schema — the element count arrives as an argument and the element type as a
/// type parameter — which is why it lives in the corelib rather than being emitted
/// into every generated source tree (generator#345).
/// <para>
/// <b>A count is untrusted.</b> An array's element count is the wire's claim about
/// how many elements follow, and until a schema <c>count</c> or a receiver limit
/// bounds it, nothing else does. So no method here allocates from a count alone:
/// the count is a ceiling on growth, never the first allocation.
/// </para>
/// </remarks>
public static class Seq
{
    /// <summary>
    /// Initial element capacity for an array whose length the schema does not
    /// bound.
    /// </summary>
    /// <remarks>
    /// The first reservation is this, not the announced count: sizing the
    /// destination from an untrusted count lets a three-byte header ask for
    /// gigabytes. Growth starts here and <see cref="EnsureCap{T}"/> doubles it
    /// against elements that have actually arrived.
    /// </remarks>
    public const int ArrayInitCap = 16;

    /// <summary>
    /// Enlarge <paramref name="array"/> so that index <paramref name="index"/> can
    /// be written, doubling its length but never past <paramref name="cap"/>.
    /// </summary>
    /// <remarks>
    /// This is the growth policy for an array being filled element by element, and
    /// its whole point is that it tracks elements that have <b>actually arrived</b>.
    /// Doubling keeps the fill amortized O(n), and the <paramref name="cap"/> clamp
    /// means an honest array of the announced length still ends up exactly
    /// right-sized rather than at the next power of two.
    /// <para>
    /// <paramref name="cap"/> is a ceiling on the <em>result</em>, not a bound the
    /// caller is relieved of checking: it is the announced element count for an
    /// unbounded field and the schema capacity for a bounded one, both already
    /// judged by the caller at <see cref="IVisitor.ArrayBegin"/> (MESSAGE_SPEC
    /// §7.1), and a fill that stays within its own count therefore never sees it
    /// clamp.
    /// </para>
    /// <para>
    /// The arithmetic is done in <c>long</c>, so doubling a large array cannot
    /// overflow into a negative length and hand back an array shorter than the one
    /// it was given. Whenever <paramref name="index"/> already fits, the array is
    /// returned untouched — the call sits on the hot path unguarded.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">element type of the destination array</typeparam>
    /// <param name="array">the array so far</param>
    /// <param name="index">index about to be written</param>
    /// <param name="cap">growth ceiling: the announced or declared element count</param>
    /// <returns><paramref name="array"/>, or a longer copy of it</returns>
    public static T[] EnsureCap<T>(T[] array, int index, int cap)
    {
        if (index < array.Length)
        {
            return array;
        }
        long n = (long)array.Length * 2;
        if (n < (long)index + 1)
        {
            n = (long)index + 1;
        }
        if (n > cap)
        {
            n = cap;
        }
        Array.Resize(ref array, (int)n);
        return array;
    }
}
