/*
 * SofaBuffers C# - generated-code support layer: reassembly of a string or blob
 * payload delivered in chunks.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Runtime.CompilerServices;

namespace sofab;

/// <summary>
/// Reassembles a <c>string</c> or <c>blob</c> payload that arrives in more than
/// one piece — the <b>support layer</b> for generated code, not part of the codec.
/// </summary>
/// <remarks>
/// <see cref="IVisitor.String"/> and <see cref="IVisitor.Blob"/> deliver a payload
/// in one or more chunks, split wherever the input happened to be split, and the
/// <c>data</c> array they hand over is only valid for the duration of the call
/// (that is what lets a payload exceed the input buffer, and RAM). A consumer that
/// wants the whole value therefore has to buffer the pieces. That is all this is,
/// and its code has the same shape for every schema, so it lives here rather than
/// being emitted into every generated source tree (generator#345).
/// <para>
/// Hold one per visitor and pass the callback's arguments straight through; the
/// value comes back on the chunk that completes it, and <c>null</c> before that:
/// </para>
/// <code>
/// public void String(int id, int total, int offset, byte[] data, int co, int cl)
/// {
///     string? s = _acc.String(total, offset, data, co, cl, MaxDynStringLen);
///     if (s is null)
///     {
///         return;                  // more chunks to come
///     }
///     ...                          // route s to its field
/// }
/// </code>
/// <para>
/// <b>A payload that arrives whole never touches the buffer.</b> The common case —
/// one chunk carrying the entire field — is answered straight out of the caller's
/// input array, so an accumulator that is never needed never allocates one byte.
/// </para>
/// <para>
/// <b><c>total</c> is not an allocation.</b> The announced length is the wire's
/// claim, so the buffer grows by doubling against bytes that have actually
/// arrived: even a payload nothing bounds cannot make this class allocate more
/// than the peer really sent.
/// </para>
/// <para>
/// <b>The cap is an argument, never a possession (CORELIB_PLAN §6.2.1).</b> Both
/// methods take the <c>max_dyn_string_len</c> / <c>max_dyn_blob_len</c> the
/// receiver configured and compare <c>total</c> against it before a byte is
/// taken, at the length header, which is where §6.2.1 requires the check to run
/// — ahead of the allocation it exists to prevent. The <em>number</em> stays
/// generated code's: it is used for that one comparison and not retained, and
/// this class holds no limit of its own, defaults none, reads no omitted
/// argument as unlimited and clamps to nothing. Which is why the parameter is
/// <b>required</b>: there is no unset state and no unlimited mode.
/// </para>
/// <para>
/// <b>...and at the LENGTH WORD, not only at the first chunk.</b> <see cref="String"/>
/// and <see cref="Blob"/> cannot be the enforcement point on their own: they fire
/// only once a payload byte exists, so a message that ends immediately after the
/// length word reaches neither, and a decode whose verdict was already decided
/// answers <c>Incomplete</c> instead — losing the category (§6.3 makes the refusal
/// terminal) and inviting a caller to keep feeding a stream this receiver has
/// already refused. So the comparison is reachable on its own, as
/// <see cref="CheckStringLength"/> / <see cref="CheckBlobLength"/>, for a caller to
/// make from <see cref="IVisitor.FixlenBegin"/>. The payload methods call the same
/// two, so the rule has <b>one implementation</b> applied at two points.
/// </para>
/// <para>
/// <b>Behind the tag test, and only for a field that is read.</b> Generated code
/// resolves the destination first (MESSAGE_SPEC §7.3: a field whose wire type
/// contradicts the declared one is skipped, and a skipped field is never capped)
/// and calls only for a payload it is actually materializing, so the check here
/// already sits behind both conditions.
/// </para>
/// <para>
/// <b>A schema-bounded field is not capped here.</b> Where the schema declares a
/// <c>maxlen</c>, that bound governs and exceeding it is <c>InvalidMessage</c>,
/// not <c>LimitExceeded</c> (MESSAGE_SPEC §7.1, CORELIB_PLAN §6.3). Generated
/// code rejects it at the same header before calling and passes that same bound
/// on, where it can no longer fire: this class is never the one to decide which
/// of the two a field has.
/// </para>
/// <para>
/// <b>No re-arming step.</b> Every payload's first chunk is reported at offset 0,
/// and that is where the buffer is emptied — so an accumulator still holding the
/// remains of a payload that never completed (a stream that ended mid-field) is
/// correct again the moment the next one starts, whether or not the visitor around
/// it was reused.
/// </para>
/// <para>
/// <b>The split must not be observable.</b> CORELIB_PLAN §6.4 forbids an outcome
/// that depends on where a chunk boundary fell: for the same bytes the value — and
/// for a string the UTF-8 verdict — is the same whether they arrive in one piece or
/// one byte at a time. Which is why a string is validated once, on the reassembled
/// payload, and never per chunk: a multi-byte sequence split across a feed is a
/// well-formed prefix, not a defect.
/// </para>
/// <para>This class is not thread-safe; decode one message on one thread.</para>
/// </remarks>
public sealed class PayloadAcc
{
    /// <summary>Accumulated bytes of the payload in flight; empty until one is split.</summary>
    private byte[] _buffer = Array.Empty<byte>();

    /// <summary>How much of <see cref="_buffer"/> is filled.</summary>
    private int _length;

    /// <summary>
    /// Offer a chunk of a <c>string</c> payload; returns the decoded string once
    /// the last chunk has arrived, <c>null</c> while more are expected.
    /// </summary>
    /// <remarks>
    /// Validation happens on the reassembled payload, once, at the point it is
    /// complete (<see cref="Utf8.Decode"/>).
    /// </remarks>
    /// <param name="total">full payload length in bytes, as <see cref="IVisitor.String"/> reports it</param>
    /// <param name="offset">byte position of this chunk within the payload</param>
    /// <param name="data">backing array containing the chunk</param>
    /// <param name="chunkOffset">start of the chunk within <paramref name="data"/></param>
    /// <param name="chunkLength">number of bytes in the chunk</param>
    /// <param name="cap">
    /// the receiver's <c>max_dyn_string_len</c> for this field, in bytes — the
    /// caller's number, used for this one comparison and not retained (§6.2.1);
    /// for a field the schema bounds, the schema <c>maxlen</c> the caller has
    /// already enforced
    /// </param>
    /// <returns>the completed string, or <c>null</c> while the payload is incomplete</returns>
    /// <exception cref="SofabException">
    /// (<see cref="SofabError.LimitExceeded"/>) when <paramref name="total"/>
    /// exceeds <paramref name="cap"/> — checked before a byte is taken;
    /// (<see cref="SofabError.Argument"/>) when no cap was stated
    /// (<paramref name="cap"/> is negative), a caller defect and never
    /// <see cref="SofabError.LimitExceeded"/>, which would promise a limit to
    /// raise that was never configured (§6.3);
    /// (<see cref="SofabError.InvalidMessage"/>) when the completed payload is not
    /// valid UTF-8.
    /// </exception>
    public string? String(int total, int offset, byte[] data, int chunkOffset, int chunkLength, long cap)
    {
        CheckStringLength(total, cap);

        if (offset == 0 && chunkLength >= total)
        {
            return Utf8.Decode(data, chunkOffset, total);
        }
        if (!Append(total, offset, data, chunkOffset, chunkLength))
        {
            return null;
        }
        _length = 0;
        return Utf8.Decode(_buffer, 0, total);
    }

    /// <summary>
    /// Offer a chunk of a <c>blob</c> payload; returns the payload once the last
    /// chunk has arrived, <c>null</c> while more are expected.
    /// </summary>
    /// <remarks>
    /// The returned array is the caller's to keep: it is a copy, never a view into
    /// the decoder's input buffer or into this accumulator.
    /// </remarks>
    /// <param name="total">full payload length in bytes, as <see cref="IVisitor.Blob"/> reports it</param>
    /// <param name="offset">byte position of this chunk within the payload</param>
    /// <param name="data">backing array containing the chunk</param>
    /// <param name="chunkOffset">start of the chunk within <paramref name="data"/></param>
    /// <param name="chunkLength">number of bytes in the chunk</param>
    /// <param name="cap">
    /// the receiver's <c>max_dyn_blob_len</c> for this field, in bytes — the
    /// caller's number, used for this one comparison and not retained (§6.2.1);
    /// for a field the schema bounds, the schema <c>maxlen</c> the caller has
    /// already enforced
    /// </param>
    /// <returns>the completed payload, or <c>null</c> while it is incomplete</returns>
    /// <exception cref="SofabException">
    /// (<see cref="SofabError.LimitExceeded"/>) when <paramref name="total"/>
    /// exceeds <paramref name="cap"/> — checked before a byte is taken;
    /// (<see cref="SofabError.Argument"/>) when no cap was stated
    /// (<paramref name="cap"/> is negative).
    /// </exception>
    public byte[]? Blob(int total, int offset, byte[] data, int chunkOffset, int chunkLength, long cap)
    {
        CheckBlobLength(total, cap);

        if (offset == 0 && chunkLength >= total)
        {
            var whole = new byte[total];
            Array.Copy(data, chunkOffset, whole, 0, total);
            return whole;
        }
        if (!Append(total, offset, data, chunkOffset, chunkLength))
        {
            return null;
        }
        // Allocated here and not a chunk earlier: `total` is the wire's claim, so
        // the payload is sized only once its bytes are actually in hand.
        _length = 0;
        var value = new byte[total];
        Array.Copy(_buffer, 0, value, 0, total);
        return value;
    }

    /// <summary>
    /// Refuse an announced <c>string</c> length the receiver's cap does not admit
    /// — the §6.2.1 comparison on its own, for a caller to make at the
    /// <b>length word</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="String"/> calls this first, so a caller that only routes payload
    /// chunks is already covered. Call it in addition from
    /// <see cref="IVisitor.FixlenBegin"/> to close the case no payload callback can
    /// see: a message whose length word declares more than the cap and then
    /// <em>ends</em>. There is no chunk, so there is no <see cref="String"/> call,
    /// and the decode reports <c>Incomplete</c> for bytes this receiver has already
    /// refused — the wrong category (§6.3 makes the refusal terminal) and an active
    /// invitation to feed more of a stream that will never be accepted. Three bytes
    /// claiming a hundred, or five claiming a megabyte, would hold a connection
    /// open: the amplification the caps exist to close.
    /// </para>
    /// <para>
    /// Calling it at both points is not two implementations of the rule. It is this
    /// one, applied where §6.2.1 requires it ("at the count/length header — before
    /// the allocation it is meant to prevent") and again where an accumulator driven
    /// by hand would otherwise slip past it.
    /// </para>
    /// <para>
    /// <b>Only for a field this message actually reads.</b> §6.2.1: "a skipped field
    /// is never capped" — a limit bounds an allocation, and a field walked over
    /// allocates nothing. The caller resolves the destination, and the MESSAGE_SPEC
    /// §7.3 subtype test, before it gets here.
    /// </para>
    /// </remarks>
    /// <param name="total">the announced payload length, as the <c>fixlen_word</c> gives it</param>
    /// <param name="cap">
    /// the receiver's <c>max_dyn_string_len</c> for this field — the caller's
    /// number, used for this one comparison and not retained; for a field the
    /// schema bounds, the schema <c>maxlen</c> the caller has already enforced
    /// </param>
    /// <exception cref="SofabException">
    /// (<see cref="SofabError.LimitExceeded"/>) when <paramref name="total"/> exceeds
    /// <paramref name="cap"/>; (<see cref="SofabError.Argument"/>) when no cap was
    /// stated (<paramref name="cap"/> is negative).
    /// </exception>
    public static void CheckStringLength(int total, long cap)
    {
        if (total > cap)
        {
            ThrowCap(total, cap, "max_dyn_string_len");
        }
    }

    /// <summary>
    /// Refuse an announced <c>blob</c> length the receiver's cap does not admit —
    /// the <c>blob</c> twin of <see cref="CheckStringLength"/>, down to why it
    /// exists. A <c>blob</c> and a <c>string</c> are separate limits.
    /// </summary>
    /// <param name="total">the announced payload length, as the <c>fixlen_word</c> gives it</param>
    /// <param name="cap">the receiver's <c>max_dyn_blob_len</c> for this field</param>
    /// <exception cref="SofabException">
    /// (<see cref="SofabError.LimitExceeded"/>) when <paramref name="total"/> exceeds
    /// <paramref name="cap"/>; (<see cref="SofabError.Argument"/>) when no cap was
    /// stated.
    /// </exception>
    public static void CheckBlobLength(int total, long cap)
    {
        if (total > cap)
        {
            ThrowCap(total, cap, "max_dyn_blob_len");
        }
    }

    /// <summary>
    /// Refuse a payload the receiver's cap does not admit — or a call that stated
    /// no cap at all (CORELIB_PLAN §6.2.1, §6.3).
    /// </summary>
    /// <remarks>
    /// Out of line and never inlined: the comparison at the call site is one
    /// branch on a call generated code already makes, and the throw path carries
    /// the message building.
    /// <para>
    /// The two categories are not interchangeable. An over-cap payload is a
    /// <b>policy</b> rejection: the bytes are well-formed and the same message
    /// decodes under a looser limit, so it is <see cref="SofabError.LimitExceeded"/>
    /// and never <see cref="SofabError.InvalidMessage"/>. A negative
    /// <paramref name="cap"/> is not a limit at all but a <b>caller defect</b> — a
    /// number never stated, or a sentinel meant to read as "unlimited", which
    /// §6.2.1 forbids this class to honour — so it is
    /// <see cref="SofabError.Argument"/> (§6.3's <c>InvalidArgument</c>).
    /// Reporting it as a limit would promise a limit to raise that nobody set.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCap(int total, long cap, string which) =>
        throw (cap < 0
            ? new SofabException(SofabError.Argument, which + " not stated (cap " + cap + ")")
            : new SofabException(SofabError.LimitExceeded, which + " " + cap + " < " + total));

    /// <summary>
    /// Append a chunk, growing the buffer as bytes actually arrive.
    /// </summary>
    /// <returns><c>true</c> once <paramref name="total"/> bytes stand in the buffer</returns>
    private bool Append(int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
        if (offset == 0)
        {
            // A payload starting over: whatever stands here belongs to one that
            // never completed -- a stream that ended mid-field -- and must not be
            // prefixed onto this one.
            _length = 0;
        }
        int need = _length + chunkLength;
        if (need > _buffer.Length)
        {
            // Double, but never below what this chunk needs, and never above the
            // announced total: a payload arriving in n pieces is copied a
            // logarithmic number of times, and one that arrives whole after a first
            // partial chunk lands in an exactly-sized buffer.
            long grown = (long)_buffer.Length * 2;
            if (grown < need)
            {
                grown = need;
            }
            if (grown > total)
            {
                grown = Math.Max(total, need);
            }
            Array.Resize(ref _buffer, (int)grown);
        }
        Array.Copy(data, chunkOffset, _buffer, _length, chunkLength);
        _length = need;
        return _length >= total;
    }
}
