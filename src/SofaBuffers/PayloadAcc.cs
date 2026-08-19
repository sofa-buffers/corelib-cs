/*
 * SofaBuffers C# - generated-code support layer: reassembly of a string or blob
 * payload delivered in chunks.
 *
 * SPDX-License-Identifier: MIT
 */

using System;

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
///     string? s = _acc.String(total, offset, data, co, cl);
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
/// claim, bounded by nothing this class knows about, so the buffer grows by
/// doubling against bytes that have actually arrived. A caller holding a schema
/// <c>maxlen</c> or a receiver limit rejects an oversized <c>total</c> at
/// <see cref="IVisitor.FixlenBegin"/>, before the first chunk reaches here
/// (MESSAGE_SPEC §7.1); a caller holding neither still cannot be made to allocate
/// more than the peer actually sent.
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
    /// <returns>the completed string, or <c>null</c> while the payload is incomplete</returns>
    /// <exception cref="SofabException">
    /// (<see cref="SofabError.InvalidMessage"/>) when the completed payload is not
    /// valid UTF-8.
    /// </exception>
    public string? String(int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
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
    /// <returns>the completed payload, or <c>null</c> while it is incomplete</returns>
    public byte[]? Blob(int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
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
