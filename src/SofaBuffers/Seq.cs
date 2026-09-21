/*
 * SofaBuffers C# - generated-code support layer: element placement, row
 * reservation, the element-index bound, and array growth for a decode
 * destination filled element by element.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace sofab;

/// <summary>
/// Element placement, row reservation and array growth for generated decode
/// destinations — the <b>support layer</b>, not part of the codec.
/// </summary>
/// <remarks>
/// Nothing here touches the wire. These are the operations a generated message
/// class performs <em>around</em> an <see cref="IVisitor"/> callback: put an
/// element at the index its id names, reserve the slot a framed element is routed
/// into, reserve a matrix row, enlarge an array as elements actually arrive. Their
/// code has the same shape for every schema — a bound arrives as an argument, an
/// element type as a type parameter and an element default as an argument or a
/// factory — which is why they live in the corelib rather than being emitted,
/// rationale and all, into every generated source tree (generator#345, #587).
/// <para>
/// <b>Three reservations, one shape.</b> Every wrapper array a schema can declare
/// reaches this class through one of three calls, which differ only in what a
/// slot holds: <see cref="PlaceElem{T}"/> puts a decoded <c>string</c> or
/// <c>blob</c> at the index its id names; <see cref="ReserveElem{T}"/> makes the
/// slot a <c>struct</c>, <c>union</c> or nested-array element is routed into;
/// <see cref="ReserveRow{T}"/> reserves a matrix row. <see cref="CheckIndex"/> is
/// the bound on its own, published for the one site that has no reservation to
/// ride. A generated C# row is always a <see cref="List{T}"/>, native or wrapper,
/// so one generic <see cref="ReserveRow{T}"/> serves every matrix; .NET shares one
/// canonical body for every reference-type instantiation, so the generics cost no
/// code per schema.
/// </para>
/// <para>
/// <b>Ids are positions</b> (MESSAGE_SPEC §5.1). An array element's id <em>is</em>
/// its index, an interior element equal to the element default may be omitted,
/// and the highest id present is what gives the decoded array its length — so a
/// missing id fills a gap rather than shifting every later element down by one,
/// a repeated id replaces (or, for a framed element, merges) rather than appends
/// (§7.4), and no trailing fill is ever needed because the last element is never
/// elided. Not one of these rules is visible in the bytes: two implementations can
/// disagree about each and still emit an identical message, which is why they are
/// written here once and tested here directly (CORELIB_PLAN §7.2 item 8).
/// </para>
/// <para>
/// <b>An element index is untrusted, and both of its bounds travel as
/// arguments.</b> Growing a list to <c>id + 1</c> means one over-index element is
/// by itself the allocation an untrusted index buys, so every placement bounds the
/// index <em>before</em> the list grows: a refused id leaves the list exactly as
/// it was, and a lower id delivered afterwards still lands (§7.2 item 8). Each
/// call takes two numbers, and the schema decides which one applies:
/// </para>
/// <list type="bullet">
/// <item><c>cap</c> — the array's schema <c>count</c>, a <b>capacity</b> and not a
/// length (the list starts empty and the wire carries the length). An id at or
/// past it contradicts the schema both peers agreed on: malformed input,
/// <see cref="SofabError.InvalidMessage"/> (MESSAGE_SPEC §7.1). Where the schema
/// declares no count, the caller passes a negative <c>cap</c>.</item>
/// <item><c>rcap</c> — the receiver's <c>max_dyn_array_count</c>, applied
/// <b>only</b> where <c>cap</c> is negative. The bytes are then well formed and
/// the same element decodes under a looser configuration, so the verdict is the
/// <see cref="SofabError.LimitExceeded"/> policy category (CORELIB_PLAN §6.2.1,
/// §6.3). A negative <c>rcap</c> states no cap at all and admits no element,
/// reported as <see cref="SofabError.Argument"/>: the mistake is in the call, not
/// in a receiver policy nobody configured.</item>
/// </list>
/// <para>
/// Exactly one of the two is ever compared — a receiver cap never touches a field
/// the schema already bounds (§6.2.1) — and neither is this class's: both arrive
/// per call, are used for that one comparison and are not retained. Nothing here
/// holds, defaults to or clamps to a limit of its own. Generated code therefore
/// needs no index comparison of its own at all; it passes the schema count (a
/// literal the JIT folds) and its <c>MaxDynArrayCount</c> constant to every call.
/// </para>
/// <para>
/// <b>What these bounds do not cover.</b> A <c>string</c> or <c>blob</c>
/// element's own length (<c>maxlen</c>, or <c>max_dyn_string_len</c> /
/// <c>max_dyn_blob_len</c>) is judged at the <b>length word</b>, so that a message
/// ending right after that word is refused rather than reported
/// <see cref="DecodeStatus.Incomplete"/> (MESSAGE_SPEC §5.2); generated code runs
/// <see cref="CheckIndex"/> there, then the length check, and places the element
/// only once its payload is complete. The receiver half of that length check is
/// <see cref="PayloadAcc"/>'s. Neither is the element <b>count</b> of a native
/// array or of a matrix row bounded here: that count arrives at
/// <see cref="IVisitor.ArrayBegin"/> and no call of this class sees it.
/// </para>
/// <para>
/// <b>A count is untrusted too.</b> An array's element count is the wire's claim
/// about how many elements follow, and until a schema <c>count</c> or a receiver
/// limit bounds it, nothing else does. So no method here allocates from a count
/// alone: the count is a ceiling on growth, never the first allocation.
/// </para>
/// <para>
/// The encode output buffer is not here: it belongs to the generated layer
/// (CORELIB_PLAN §5.1).
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

    // -----------------------------------------------------------------------
    // Element placement
    // -----------------------------------------------------------------------

    /// <summary>
    /// <b>Place a leaf element</b> — a wrapper array's <c>string</c> or
    /// <c>blob</c> — at the index its wire id names, growing the list and filling
    /// the gap omitted interior elements left (MESSAGE_SPEC §5.1).
    /// </summary>
    /// <remarks>
    /// The index is bounded first (<see cref="CheckIndex"/>), so a refused id
    /// leaves <paramref name="list"/> exactly as it was. A gap below
    /// <paramref name="id"/> is filled with <paramref name="empty"/>, and a
    /// repeated id replaces the element already there (§7.4).
    /// <para>
    /// <paramref name="empty"/> is <b>shared, not copied</b>: the gap value of a
    /// string or blob array is <c>""</c> or <see cref="Array.Empty{T}"/> —
    /// immutable, or zero-length and so with no state to share wrongly — so a gap
    /// costs no allocation. An element whose default is a <em>mutable</em> object
    /// belongs in <see cref="ReserveElem{T}"/>, which makes one per slot.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">element type</typeparam>
    /// <param name="list">the destination list, which this grows</param>
    /// <param name="id">the element's wire id, which is its index</param>
    /// <param name="empty">the element default, filling any gap below <paramref name="id"/></param>
    /// <param name="value">the decoded element</param>
    /// <param name="cap">the array's schema <c>count</c>, or negative where the schema declares none</param>
    /// <param name="rcap">the receiver's <c>max_dyn_array_count</c>, applied only where <paramref name="cap"/> is negative</param>
    /// <exception cref="SofabException">see <see cref="CheckIndex"/></exception>
    public static void PlaceElem<T>(List<T> list, int id, T empty, T value, long cap, long rcap)
    {
        CheckIndex(id, cap, rcap);
        while (list.Count <= id)
        {
            list.Add(empty);
        }
        list[id] = value;
    }

    /// <summary>
    /// <b>Reserve a framed element</b> — the slot a wrapper array's
    /// <c>struct</c>, <c>union</c> or nested-array element is routed into: bound
    /// the index, then grow the list to <c>id + 1</c>, giving each new slot its own
    /// element from <paramref name="make"/> (MESSAGE_SPEC §5.1).
    /// </summary>
    /// <remarks>
    /// Same rules and same order as <see cref="PlaceElem{T}"/>, with one deliberate
    /// difference: <b>a slot already present is left alone</b>. A framed element's
    /// fields arrive one at a time and each is routed into the object reserved
    /// here, so a re-opened element id <em>merges</em> into what its earlier fields
    /// built (§7.4); replacing the slot would discard them.
    /// <para>
    /// <b>A factory, not a shared default.</b> A generated element is mutable and
    /// decodes <em>into</em> the object placed here, so one shared instance would
    /// alias every element of the array onto it. A <see cref="Func{TResult}"/> and
    /// not a <c>new()</c> constraint: in a shared generic body <c>new T()</c>
    /// becomes a reflective <c>Activator.CreateInstance</c>, whereas a
    /// <c>static () =&gt; new X()</c> lambda is cached by the compiler and costs no
    /// allocation per call. It runs only when a slot is created.
    /// </para>
    /// <para>
    /// What this does <b>not</b> own is the routing — binding the element index,
    /// switching into the element's scope. That has a different shape per schema
    /// and stays generated; this owns growth and the bound, and stops at the slot.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">element type</typeparam>
    /// <param name="list">the destination list, which this grows</param>
    /// <param name="id">the element's wire id, which is its index</param>
    /// <param name="make">the element factory, called once per slot this creates</param>
    /// <param name="cap">the array's schema <c>count</c>, or negative where the schema declares none</param>
    /// <param name="rcap">the receiver's <c>max_dyn_array_count</c>, applied only where <paramref name="cap"/> is negative</param>
    /// <exception cref="SofabException">see <see cref="CheckIndex"/></exception>
    public static void ReserveElem<T>(List<T> list, int id, Func<T> make, long cap, long rcap)
    {
        CheckIndex(id, cap, rcap);
        while (list.Count <= id)
        {
            list.Add(make());
        }
    }

    /// <summary>
    /// <b>Reserve a matrix row</b> — the row at index <paramref name="id"/> of an
    /// array whose elements are themselves arrays — as an empty row, growing the
    /// outer list with empty rows so that a gap in the ids decodes as an empty row
    /// instead of shifting every later row down by one.
    /// </summary>
    /// <remarks>
    /// The row index is bounded first, as in every placement here. Each gap row is
    /// a <b>distinct</b> empty list — a row is mutable, so a shared one would alias.
    /// A row id delivered again replaces the row's value (§7.4): the list already
    /// there is emptied in place rather than swapped for a new one, which reads the
    /// same and allocates one list per row instead of two. The row's own element
    /// count is not bounded here; it arrives at <see cref="IVisitor.ArrayBegin"/>
    /// or in the row's sequence frame, and the caller judges it.
    /// </remarks>
    /// <typeparam name="T">row element type</typeparam>
    /// <param name="rows">the outer list, one entry per row</param>
    /// <param name="id">the row's wire id, which is its index</param>
    /// <param name="cap">the outer array's schema <c>count</c>, or negative where the schema declares none</param>
    /// <param name="rcap">the receiver's <c>max_dyn_array_count</c>, applied only where <paramref name="cap"/> is negative</param>
    /// <exception cref="SofabException">see <see cref="CheckIndex"/></exception>
    public static void ReserveRow<T>(List<List<T>> rows, int id, long cap, long rcap)
    {
        CheckIndex(id, cap, rcap);
        while (rows.Count < id)
        {
            rows.Add(new List<T>());
        }
        if (rows.Count == id)
        {
            rows.Add(new List<T>());
            return;
        }
        rows[id].Clear();
    }

    // -----------------------------------------------------------------------
    // The index bound
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reject an element index the caller may not accept, before any list is
    /// grown to hold it (CORELIB_PLAN §6.2.1).
    /// </summary>
    /// <remarks>
    /// Every placement here runs this first. It is public for the one site that
    /// has <b>no reservation to ride</b>: a generated <c>FixlenBegin</c> arm bounds
    /// a <c>string</c> or <c>blob</c> element's index at the length word, so that a
    /// message ending right there is refused rather than reported
    /// <see cref="DecodeStatus.Incomplete"/> (MESSAGE_SPEC §5.2). The placement
    /// that follows re-runs it, which costs one comparison against a folded
    /// constant and spares the two sites from having to agree by inspection.
    /// <para>
    /// Exactly one of the two numbers applies, and the schema picks which — see
    /// the class remarks. The comparison is one branch; the refusal is built out of
    /// line so that this stays small enough to inline at every call site. A negative
    /// id — which no decoder delivers — is refused too, rather than reaching the
    /// list's indexer; against a folded bound the JIT turns the pair into one
    /// unsigned comparison.
    /// </para>
    /// </remarks>
    /// <param name="id">the element's wire id, which is its index</param>
    /// <param name="cap">the array's schema <c>count</c>, or negative where the schema declares none</param>
    /// <param name="rcap">the receiver's <c>max_dyn_array_count</c>, applied only where <paramref name="cap"/> is negative</param>
    /// <exception cref="SofabException">
    /// (<see cref="SofabError.InvalidMessage"/>) when <paramref name="id"/> reaches
    /// a declared <paramref name="cap"/>; (<see cref="SofabError.LimitExceeded"/>)
    /// when a schema-uncounted <paramref name="id"/> reaches a stated
    /// <paramref name="rcap"/>; (<see cref="SofabError.Argument"/>) when a
    /// schema-uncounted array was handed no cap at all (<paramref name="rcap"/>
    /// negative).
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CheckIndex(int id, long cap, long rcap)
    {
        long bound = cap >= 0 ? cap : rcap;
        if (id < 0 || id >= bound)
        {
            ThrowIndex(id, cap, rcap);
        }
    }

    /// <summary>
    /// Raise the refusal an index <see cref="CheckIndex"/> rejected has earned.
    /// </summary>
    /// <remarks>
    /// Out of line and never inlined, like <see cref="PayloadAcc"/>'s cap refusal:
    /// the message building stays off the hot path. A declared count makes the
    /// verdict <see cref="SofabError.InvalidMessage"/>, a stated receiver cap
    /// <see cref="SofabError.LimitExceeded"/>, and no cap at all
    /// <see cref="SofabError.Argument"/>.
    /// </remarks>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndex(int id, long cap, long rcap) =>
        throw (cap >= 0
            ? new SofabException(SofabError.InvalidMessage, "array index " + id + " above declared count " + cap)
            : rcap < 0
                ? new SofabException(SofabError.Argument, "max_dyn_array_count not stated (cap " + rcap + ") for array index " + id)
                : new SofabException(SofabError.LimitExceeded, "array index " + id + " above configured limit " + rcap));
}
