/*
 * SofaBuffers C# - streaming input decoder (port of istream.c).
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static sofab.WireFormat;

namespace sofab;

/// <summary>
/// Streaming SofaBuffers decoder.
/// </summary>
/// <remarks>
/// <see cref="IStream"/> is a byte-at-a-time state machine. Feed it arbitrary
/// chunks with <see cref="Feed(byte[], IVisitor)"/>; it parses field headers and
/// pushes decoded fields to your <see cref="IVisitor"/>. Because all parse state
/// lives inside the decoder, a message may be split across any number of
/// <c>Feed</c> calls at any byte boundary — true streaming on the input side.
/// <para>
/// Each <c>Feed</c> returns a <see cref="DecodeStatus"/> (also readable via
/// <see cref="Status"/>): <see cref="DecodeStatus.Complete"/> if the bytes so far
/// end at a field boundary, or <see cref="DecodeStatus.Incomplete"/> if they end
/// inside a field or with an open sequence (MESSAGE_SPEC §7). Incomplete is
/// <em>not</em> an error and is <em>not</em> a rejection — the partial field is
/// held and resumed on the next chunk; the caller owns end-of-input and decides
/// whether a trailing Incomplete is a truncation. Genuinely malformed input still
/// throws <see cref="SofabException"/> (<see cref="SofabError.InvalidMessage"/>).
/// There is no finish / finalize step.
/// </para>
/// <para>
/// <b>A rejection is terminal.</b> Malformed bytes are malformed regardless of
/// what follows, so once a <c>Feed</c> has thrown
/// <see cref="SofabError.InvalidMessage"/> the decoder latches that verdict:
/// <see cref="Status"/> reports <see cref="DecodeStatus.Invalid"/> and every
/// later <c>Feed</c> throws again, consuming nothing and emitting no visitor
/// callback — a caller that logs the error and keeps reading its socket cannot
/// resume a stream this decoder has already rejected. Decode a new message with
/// a new <see cref="IStream"/>.
/// </para>
/// <para>
/// Unlike the C decoder there is no per-field "bind a destination" step and no
/// explicit skip bookkeeping: an <see cref="IVisitor"/> simply ignores fields it
/// does not care about. Scalars and floats are delivered whole; string / blob
/// payloads are delivered in chunks (so they may exceed RAM); array elements are
/// announced with <see cref="IVisitor.ArrayBegin"/> and then delivered through
/// the scalar / float callbacks. Every fixlen field is likewise announced with
/// <see cref="IVisitor.FixlenBegin"/> on its length word, before any payload
/// byte, so a schema <c>maxlen</c> bound is judged at the word that violates it
/// rather than at whichever payload byte happens to arrive first.
/// </para>
/// <para>
/// <b>Hot-path convention.</b> <see cref="Feed(byte[], int, int, IVisitor)"/>
/// validates its slice once and the decode paths then read it without a per-byte
/// bounds check; the single-byte varint case is written out at each call site
/// rather than shared behind a helper, for the reason given on
/// <c>ReadVarintMulti</c>, which decodes longer varints eight bytes at a time.
/// </para>
/// <para>
/// This class is not thread-safe; decode one message from one thread. Reuse an
/// instance for a new message only after the previous one is fully consumed (or
/// by constructing a fresh <see cref="IStream"/>).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// class Sink : IVisitor {
///     public long A; public long B;
///     public void Unsigned(int id, ulong v) { if (id == 1) A = (long)v; }
///     public void Signed(int id, long v)    { if (id == 2) B = v; }
/// }
/// var sink = new Sink();
/// new IStream().Feed(buf, sink);
/// </code>
/// </example>
public sealed class IStream
{
    /// <summary>
    /// Where the byte-at-a-time machine stands — plus, as its last two members,
    /// the two ways this decoder can be permanently done with a stream.
    /// </summary>
    /// <remarks>
    /// The terminal verdicts live in the state field rather than in a flag of
    /// their own: a dead decoder <em>has</em> no parse position, one field is one
    /// load on the entry check, and the decoder object does not grow. They are
    /// ordered last so that single comparison is <c>&gt;= Rejected</c>.
    /// </remarks>
    private enum State
    {
        Idle,
        VarintUnsigned,
        VarintSigned,
        FixlenLen,
        FixlenVal,
        FixlenRaw,
        ArrayCount,

        /// <summary>
        /// The bytes seen were malformed: the <c>INVALID</c> outcome (§5.2),
        /// which is terminal — no continuation can make them valid.
        /// </summary>
        Rejected,

        /// <summary>
        /// A receiver-side limit was exceeded (§6.2.1). Also terminal, but
        /// <b>not</b> <c>INVALID</c>: the bytes are well-formed and §6.3 forbids
        /// folding a policy rejection into the wire-conformance outcome, so this
        /// state closes the stream without ever making <see cref="Status"/> say
        /// <see cref="DecodeStatus.Invalid"/>.
        /// </summary>
        LimitStopped,
    }

    // incremental varint accumulator
    private ulong _varintValue;
    private int _varintShift;
    private ulong _varintOut;

    private State _state = State.Idle;
    private int _id;

    // array context
    private ArrayKind _arrayKind = ArrayKind.Unsigned;
    private int _arrayRemaining;
    private bool _inArray;
    // Wire type 0b101 (fixlen array): the element subtype is only known once the
    // fixlen_word has been read, so _arrayKind is not yet meaningful and the
    // ArrayBegin hook has not fired. Set between the header and the word.
    private bool _arrayFixlen;
    // The array's element count, kept across the fixlen_word so the deferred
    // ArrayBegin can still report it (_arrayRemaining is decremented per element).
    private int _arrayCount;

    // fixlen context
    private FixlenType _fixlenType = FixlenType.Fp32;
    private int _fixlenTotal;
    private int _fixlenRemaining;

    /// <summary>
    /// Little-endian accumulator for the raw bytes of an <c>fp32</c>/<c>fp64</c>
    /// value split across a <c>Feed</c> boundary — the widest such value is 8
    /// bytes, so a single register holds it. Kept as a scalar rather than a
    /// <c>byte[8]</c> so that constructing a decoder allocates nothing beyond the
    /// decoder object itself.
    /// </summary>
    private ulong _accBits;
    private int _accLen;

    /// <summary>
    /// Handed to <see cref="IVisitor.String"/> / <see cref="IVisitor.Blob"/> for a
    /// zero-length payload, where the visitor is given a length of 0 and must not
    /// read anything.
    /// </summary>
    private static readonly byte[] EmptyPayload = Array.Empty<byte>();

    /// <summary>Longest possible varint encoding (10 bytes for a 64-bit value).</summary>
    private const int MaxVarintBytes = 10;

    /// <summary>The continuation flag of each of eight packed varint bytes.</summary>
    private const ulong ContinuationBits = 0x8080_8080_8080_8080UL;

    /// <summary>The 7-bit payload of each of eight packed varint bytes.</summary>
    private const ulong PayloadBits = 0x7F7F_7F7F_7F7F_7F7FUL;

    // sequence nesting depth (for balanced start/end validation)
    private ulong _depth;

    /// <summary>Create a fresh decoder ready to accept a new message.</summary>
    public IStream()
    {
    }

    /// <summary>
    /// Feed a whole chunk of encoded bytes, pushing decoded fields to
    /// <paramref name="visitor"/>.
    /// </summary>
    /// <param name="data">encoded bytes</param>
    /// <param name="visitor">sink for decoded fields</param>
    /// <returns>
    /// <see cref="DecodeStatus.Complete"/> if the bytes consumed so far end at a
    /// clean field boundary, or <see cref="DecodeStatus.Incomplete"/> if they end
    /// inside a field or with an open sequence (MESSAGE_SPEC §7). Incomplete is
    /// not an error — feed more bytes to continue.
    /// </returns>
    /// <exception cref="SofabException">
    /// with <see cref="SofabError.InvalidMessage"/> on malformed input — and on
    /// every later call once this decoder has rejected a stream, since that
    /// verdict is terminal (§5.2)
    /// </exception>
    public DecodeStatus Feed(byte[] data, IVisitor visitor)
    {
        return Feed(data, 0, data.Length, visitor);
    }

    /// <summary>
    /// Feed a slice of encoded bytes, pushing decoded fields to
    /// <paramref name="visitor"/>. Decoding can continue across many <c>Feed</c>
    /// calls; the decoder keeps all state internally.
    /// </summary>
    /// <param name="data">backing array</param>
    /// <param name="off">start offset</param>
    /// <param name="len">number of bytes to consume</param>
    /// <param name="visitor">sink for decoded fields</param>
    /// <returns>
    /// <see cref="DecodeStatus.Complete"/> if the bytes consumed so far end at a
    /// clean field boundary (a valid message), or
    /// <see cref="DecodeStatus.Incomplete"/> if they end <em>inside</em> a field
    /// — a partial varint, an unfinished fixlen / array payload, or a still-open
    /// nested sequence (MESSAGE_SPEC §7). Incomplete is not an error and not a
    /// rejection: the decoder keeps its partial state, so feeding the next chunk
    /// resumes exactly where this one stopped. The same value is available
    /// afterwards via <see cref="Status"/>.
    /// </returns>
    /// <exception cref="SofabException">
    /// with <see cref="SofabError.InvalidMessage"/> on malformed input — including
    /// on every later call, once this decoder has rejected a stream (§5.2: the
    /// <c>INVALID</c> verdict is terminal)
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <c>null</c></exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <c>[off, off + len)</c> is not a range of <paramref name="data"/>
    /// </exception>
    public DecodeStatus Feed(byte[] data, int off, int len, IVisitor visitor)
    {
        // A terminal verdict is terminal: no continuation of bytes can make input
        // this decoder already rejected valid, so refuse the chunk before looking
        // at it -- nothing is consumed and no visitor callback is emitted (§5.2).
        // The two dead states sort last, so one comparison covers both.
        if (_state >= State.Rejected)
        {
            ThrowLatched(_state);
        }

        // Validate the slice once, here, rather than paying an array bounds check
        // on every byte of the hot decode loops: everything below reads strictly
        // inside [off, off+len), which this establishes is inside `data`.
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }
        if ((uint)off > (uint)data.Length || (uint)len > (uint)(data.Length - off))
        {
            throw new ArgumentOutOfRangeException(nameof(len), "slice out of range");
        }

        int i = off;
        int endExclusive = off + len;
        // The decode loop runs inside a catch so that a terminal verdict is
        // latched wherever it is raised -- by any of the throw sites below, or by
        // a visitor judging a field against its schema (MESSAGE_SPEC §7.1) or a
        // receiver-side cap (§6.2.1) -- without every one of them having to
        // remember to record it. The handler is entered only on the error path;
        // the loop itself is untouched, which the Callgrind decode rows confirm.
        try
        {
            while (i < endExclusive)
            {
                // Fast path: stream string/blob payloads in bulk rather than one
                // callback per byte.
                if (_state == State.FixlenRaw)
                {
                    int take = Math.Min(endExclusive - i, _fixlenRemaining);
                    int chunkOffset = _fixlenTotal - _fixlenRemaining;
                    if (_fixlenType == FixlenType.String)
                    {
                        visitor.String(_id, _fixlenTotal, chunkOffset, data, i, take);
                    }
                    else if (_fixlenType == FixlenType.Blob)
                    {
                        visitor.Blob(_id, _fixlenTotal, chunkOffset, data, i, take);
                    }
                    else
                    {
                        throw new SofabException(SofabError.InvalidMessage, "raw fixlen type");
                    }
                    _fixlenRemaining -= take;
                    i += take;
                    if (_fixlenRemaining == 0)
                    {
                        _state = State.Idle;
                    }
                    continue;
                }

                // Fast path: at a clean field boundary (no partial varint or
                // mid-array element carried over from a previous Feed) advance an
                // index straight over the contiguous buffer, decoding whole fields
                // -- and whole arrays -- inline. This skips the per-byte state-
                // machine dispatch that dominates decode cost. We only fall back to
                // the byte-at-a-time machine for the tail of a field that is split
                // across a Feed boundary.
                if (_state == State.Idle && _varintShift == 0 && !_inArray)
                {
                    int consumed = FastField(data, i, endExclusive, visitor);
                    if (consumed > 0)
                    {
                        i += consumed;
                        continue;
                    }
                    // consumed == 0: the field is not fully present in this chunk.
                    // Fall through to the byte machine, which accumulates the
                    // partial header/value and resumes on the next Feed.
                }

                Step(data[i] & 0xFF, visitor);
                i++;
            }
        }
        catch (SofabException e) when (
            e.Error == SofabError.InvalidMessage || e.Error == SofabError.LimitExceeded)
        {
            // Latch the first terminal verdict: INVALID (§5.2) is malformed
            // "regardless of what follows", and a receiver-side limit (§6.2.1) is
            // just as terminal, so this decoder is done with the stream either
            // way. Whatever parse position it held is meaningless now, so the
            // verdict simply replaces it.
            _state = e.Error == SofabError.InvalidMessage
                ? State.Rejected
                : State.LimitStopped;
            throw;
        }
        return Status;
    }

    /// <summary>
    /// The decode outcome for the bytes consumed so far (MESSAGE_SPEC §7): a pure
    /// accessor that never throws and never mutates state.
    /// </summary>
    /// <value>
    /// <see cref="DecodeStatus.Invalid"/> once a <c>Feed</c> has rejected the
    /// stream as malformed — that verdict is terminal and outranks both other
    /// outcomes (§5.2). Otherwise <see cref="DecodeStatus.Complete"/> when the
    /// decoder rests at a clean field boundary — nothing buffered, no open
    /// sequence — so the bytes seen form a valid message; or
    /// <see cref="DecodeStatus.Incomplete"/>, because a field is only partially
    /// consumed (a partial header/value varint, an unfinished fixlen / array
    /// payload) or a nested sequence is still open.
    /// </value>
    /// <remarks>
    /// This is the same value the last <see cref="Feed(byte[], IVisitor)"/>
    /// returned; it lets a caller that fed byte-at-a-time query the outcome
    /// without another <c>Feed</c>. Per the finish-less spec there is no finalize
    /// step: a trailing <see cref="DecodeStatus.Incomplete"/> is a truncation the
    /// caller interprets, not an error the decoder raises.
    /// <para>
    /// A malformed message is reported by <c>Feed</c> as a thrown
    /// <see cref="SofabException"/> (<see cref="SofabError.InvalidMessage"/>) — but
    /// the verdict is latched, so this property answers
    /// <see cref="DecodeStatus.Invalid"/> from then on and never again claims the
    /// stream is <see cref="DecodeStatus.Complete"/> or merely
    /// <see cref="DecodeStatus.Incomplete"/>. A caller that catches the exception
    /// and keeps feeding gets the same exception from every later <c>Feed</c>.
    /// </para>
    /// <para>
    /// A receiver-side limit (<see cref="SofabError.LimitExceeded"/>, §6.2.1) is
    /// terminal too — the stream is closed to further feeds — but it is
    /// deliberately <em>not</em> reported as <see cref="DecodeStatus.Invalid"/>:
    /// those bytes are well-formed, and §6.3 forbids folding a receiver's policy
    /// rejection into the wire-conformance outcome. Such a decoder reports
    /// <see cref="DecodeStatus.Incomplete"/>, which is what happened — it stopped
    /// part-way through a message and will not finish it.
    /// </para>
    /// </remarks>
    public DecodeStatus Status =>
        _state == State.Rejected ? DecodeStatus.Invalid
            : AtBoundary ? DecodeStatus.Complete : DecodeStatus.Incomplete;

    /// <summary>
    /// Whether the decoder rests at a clean top-level field boundary: idle state,
    /// no partial varint accumulated, and no open (unclosed) sequence.
    /// </summary>
    /// <remarks>
    /// A non-idle <see cref="State"/> means a value/array/fixlen field is mid-parse;
    /// a non-zero <see cref="_varintShift"/> means a header or value varint is
    /// partially accumulated while otherwise idle; a non-zero <see cref="_depth"/>
    /// means a <c>SEQUENCE_START</c> has no matching <c>SEQUENCE_END</c> yet. Any
    /// of these is INCOMPLETE (§7). Mid-array parsing already implies a non-idle
    /// state, so <see cref="_inArray"/> needs no separate check. The two terminal
    /// states are not a parse position at all and are answered by
    /// <see cref="Status"/> before it consults this.
    /// </remarks>
    private bool AtBoundary =>
        _state == State.Idle && _varintShift == 0 && _depth == 0;

    /// <summary>
    /// Decode one complete top-level field (or one complete array) starting at
    /// <paramref name="start"/>, advancing over the contiguous buffer.
    /// </summary>
    /// <returns>
    /// The number of bytes consumed (&gt; 0) when a whole field was decoded; or
    /// <c>0</c> when the field is not fully present in <c>[start, end)</c>, in
    /// which case no visitor callback was emitted and no decoder state was
    /// mutated (so the byte-at-a-time machine can re-parse from <paramref name="start"/>).
    /// An array whose elements are only partially present commits the elements
    /// that did fit and leaves the decoder in the correct mid-array state.
    /// </returns>
    private int FastField(byte[] data, int start, int end, IVisitor visitor)
    {
        // The single-byte varint case is expanded at each hot site rather than
        // shared behind a helper: measured under Callgrind the JIT declines to
        // inline a helper that itself contains a call, so every varint read went
        // through a real call. See ReadVarintMulti's remarks.
        ref byte origin = ref MemoryMarshal.GetArrayDataReference(data);
        ulong header;
        int n;
        if (end - start >= MaxVarintBytes)
        {
            ref byte h0 = ref Unsafe.Add(ref origin, (nint)(uint)start);
            header = h0;
            n = header < 0x80 ? 1 : ReadVarintMulti(ref h0, header, out header);
        }
        else
        {
            n = ReadVarintChecked(data, start, end, out header);
            if (n == 0)
            {
                return 0;
            }
        }
        int p = start + n;

        int wireType = (int)(header & 0x07);
        ulong idValue = header >> 3;
        if (idValue > (ulong)ID_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "id " + idValue);
        }
        int id = (int)idValue;

        switch (wireType)
        {
            case T_VARINT_UNSIGNED:
            {
                ulong value;
                int m;
                if (end - p >= MaxVarintBytes)
                {
                    ref byte v0 = ref Unsafe.Add(ref origin, (nint)(uint)p);
                    value = v0;
                    m = value < 0x80 ? 1 : ReadVarintMulti(ref v0, value, out value);
                }
                else
                {
                    m = ReadVarintChecked(data, p, end, out value);
                    if (m == 0)
                    {
                        return 0;
                    }
                }
                visitor.Unsigned(id, value);
                return p + m - start;
            }
            case T_VARINT_SIGNED:
            {
                ulong value;
                int m;
                if (end - p >= MaxVarintBytes)
                {
                    ref byte v0 = ref Unsafe.Add(ref origin, (nint)(uint)p);
                    value = v0;
                    m = value < 0x80 ? 1 : ReadVarintMulti(ref v0, value, out value);
                }
                else
                {
                    m = ReadVarintChecked(data, p, end, out value);
                    if (m == 0)
                    {
                        return 0;
                    }
                }
                visitor.Signed(id, ZigzagDecode(value));
                return p + m - start;
            }
            case T_FIXLEN:
                return FastFixlen(data, start, p, end, id, visitor);
            case T_VARINTARRAY_UNSIGNED:
                return FastVarintArray(data, start, p, end, id, ArrayKind.Unsigned, signed: false, visitor);
            case T_VARINTARRAY_SIGNED:
                return FastVarintArray(data, start, p, end, id, ArrayKind.Signed, signed: true, visitor);
            case T_FIXLENARRAY:
                return FastFixlenArray(data, start, p, end, id, visitor);
            case T_SEQUENCE_START:
                if (_depth >= (ulong)MAX_DEPTH)
                {
                    throw new SofabException(SofabError.InvalidMessage, "sequence too deep");
                }
                _depth++;
                visitor.SequenceBegin(id);
                return p - start;
            case T_SEQUENCE_END:
                if (_depth == 0)
                {
                    throw new SofabException(SofabError.InvalidMessage, "dangling sequence end");
                }
                _depth--;
                visitor.SequenceEnd();
                return p - start;
            default:
                throw new SofabException(SofabError.InvalidMessage, "field type " + wireType);
        }
    }

    /// <summary>Fast-path decode of a single fixlen field (fp32/fp64/string/blob).</summary>
    private int FastFixlen(byte[] data, int start, int p, int end, int id, IVisitor visitor)
    {
        ulong lenHeader;
        int n;
        if (end - p >= MaxVarintBytes)
        {
            ref byte w0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), (nint)(uint)p);
            lenHeader = w0;
            n = lenHeader < 0x80 ? 1 : ReadVarintMulti(ref w0, lenHeader, out lenHeader);
        }
        else
        {
            n = ReadVarintChecked(data, p, end, out lenHeader);
            if (n == 0)
            {
                return 0;
            }
        }
        p += n;

        // Decoded inline rather than through a helper: the tag is three bits and
        // only 0..3 are assigned, so the reserved subtypes 4..7 (§4.6) are one
        // unsigned comparison away.
        var subtype = (FixlenType)(lenHeader & 0x07);
        if ((uint)subtype > (uint)FixlenType.Blob)
        {
            ThrowFixlenType((int)(lenHeader & 0x07));
        }
        ulong lengthValue = lenHeader >> 3;
        if (lengthValue > ARRAY_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "fixlen length " + lengthValue);
        }
        int length = (int)lengthValue;

        // FixlenBegin is announced below, per arm, and deliberately NOT here:
        // every `return 0` in this method means "field not fully present, no
        // callback emitted, no state mutated", after which the byte machine
        // re-parses the same bytes from the header and announces the field from
        // StepFixlenLen. Announcing before such a return would fire the hook
        // twice for one field. The hook still cannot be missed: a message ending
        // at the length word is exactly the case the byte machine picks up.
        switch (subtype)
        {
            case FixlenType.Fp32:
                if (length != 4)
                {
                    throw new SofabException(SofabError.InvalidMessage, "fp32 length " + length);
                }
                if (end - p < 4)
                {
                    return 0;
                }
                visitor.FixlenBegin(id, subtype, 4);
                visitor.Fp32(id, BitConverter.Int32BitsToSingle(ReadInt32Le(data, p)));
                return p + 4 - start;
            case FixlenType.Fp64:
                if (length != 8)
                {
                    throw new SofabException(SofabError.InvalidMessage, "fp64 length " + length);
                }
                if (end - p < 8)
                {
                    return 0;
                }
                visitor.FixlenBegin(id, subtype, 8);
                visitor.Fp64(id, BitConverter.Int64BitsToDouble(ReadInt64Le(data, p)));
                return p + 8 - start;
            case FixlenType.String:
            case FixlenType.Blob:
                if (length == 0)
                {
                    visitor.FixlenBegin(id, subtype, 0);
                    if (subtype == FixlenType.String)
                    {
                        visitor.String(id, 0, 0, EmptyPayload, 0, 0);
                    }
                    else
                    {
                        visitor.Blob(id, 0, 0, EmptyPayload, 0, 0);
                    }
                    return p - start;
                }
                // Deliver the whole payload in one chunk only if it is fully
                // present; otherwise defer to the byte machine's chunked
                // FixlenRaw path (handles split-across-feeds streaming).
                if (end - p < length)
                {
                    return 0;
                }
                visitor.FixlenBegin(id, subtype, length);
                if (subtype == FixlenType.String)
                {
                    visitor.String(id, length, 0, data, p, length);
                }
                else
                {
                    visitor.Blob(id, length, 0, data, p, length);
                }
                return p + length - start;
            default:
                throw new SofabException(SofabError.InvalidMessage, "fixlen type");
        }
    }

    /// <summary>Fast-path decode of a whole varint array (unsigned or signed).</summary>
    private int FastVarintArray(byte[] data, int start, int p, int end, int id, ArrayKind kind, bool signed, IVisitor visitor)
    {
        ulong count;
        int n;
        if (end - p >= MaxVarintBytes)
        {
            ref byte c0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), (nint)(uint)p);
            count = c0;
            n = count < 0x80 ? 1 : ReadVarintMulti(ref c0, count, out count);
        }
        else if ((n = ReadVarintChecked(data, p, end, out count)) == 0)
        {
            return 0; // count varint not complete; re-parse from header later
        }
        if (count > ARRAY_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "array count");
        }
        p += n;
        int remaining = (int)count;
        // A zero-count array (§4.7) is just [ header ][ count=0 ]: announce it and
        // resume at the next field without reading any elements.
        visitor.ArrayBegin(id, kind, remaining);

        // The element loop is the decoder's densest path — one varint per element
        // and no headers — so the whole SWAR read is expanded into it rather than
        // called (a call costs more than the decode it wraps at this size) and the
        // buffer's data reference is hoisted out. The tail, where fewer than
        // MaxVarintBytes bytes remain, drops back to the shared checked path
        // below, which is also where a Feed boundary inside an element lands.
        ref byte origin = ref MemoryMarshal.GetArrayDataReference(data);
        int bulkEnd = end - MaxVarintBytes;
        while (remaining > 0 && p <= bulkEnd)
        {
            ref byte e0 = ref Unsafe.Add(ref origin, (nint)(uint)p);
            ulong value = e0;
            if (value < 0x80)
            {
                p++;
            }
            else
            {
                ulong word = Unsafe.ReadUnaligned<ulong>(ref e0);
                if (!BitConverter.IsLittleEndian)
                {
                    word = BinaryPrimitives.ReverseEndianness(word);
                }
                ulong ends = ~word & ContinuationBits;
                if (ends != 0)
                {
                    int tz = BitOperations.TrailingZeroCount(ends);
                    word &= ends ^ (ends - 1);
                    p += (tz >> 3) + 1;
                }
                else
                {
                    p += MaxVarintBytes - 2;
                }
                ulong g = word & PayloadBits;
                g = ((g & 0x7F007F007F007F00UL) >> 1) | (g & 0x007F007F007F007FUL);
                g = ((g & 0x3FFF00003FFF0000UL) >> 2) | (g & 0x00003FFF00003FFFUL);
                value = ((g & 0x0FFFFFFF00000000UL) >> 4) | (g & 0x000000000FFFFFFFUL);
                if (ends == 0)
                {
                    // Eight continuation bytes: 56 payload bits so far, at most
                    // two to go, and only the tenth can break the 64-bit bound.
                    ulong x = Unsafe.Add(ref e0, 8);
                    value |= (x & 0x7F) << 56;
                    p++;
                    if (x >= 0x80)
                    {
                        x = Unsafe.Add(ref e0, 9);
                        if (x > 1)
                        {
                            ThrowVarintOverflow();
                        }
                        value |= x << 63;
                        p++;
                    }
                }
            }
            if (signed)
            {
                visitor.Signed(id, ZigzagDecode(value));
            }
            else
            {
                visitor.Unsigned(id, value);
            }
            remaining--;
        }
        while (remaining > 0)
        {
            int m = ReadVarintChecked(data, p, end, out ulong tail);
            if (m == 0)
            {
                // This element is split across the Feed boundary. Commit the
                // elements decoded so far and hand the rest to the byte machine.
                _id = id;
                _inArray = true;
                _arrayKind = kind;
                _arrayRemaining = remaining;
                _state = signed ? State.VarintSigned : State.VarintUnsigned;
                return p - start;
            }
            p += m;
            if (signed)
            {
                visitor.Signed(id, ZigzagDecode(tail));
            }
            else
            {
                visitor.Unsigned(id, tail);
            }
            remaining--;
        }
        return p - start;
    }

    /// <summary>Fast-path decode of a whole fixlen array (fp32 / fp64 elements).</summary>
    /// <remarks>
    /// The type+length header is encoded once, for the first element; the
    /// remaining elements are raw payloads of that same size (mirrors the byte
    /// machine, which stays in <c>FixlenVal</c> between elements).
    /// </remarks>
    private int FastFixlenArray(byte[] data, int start, int p, int end, int id, IVisitor visitor)
    {
        ulong count;
        int n;
        if (end - p >= MaxVarintBytes)
        {
            ref byte c0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(data), (nint)(uint)p);
            count = c0;
            n = count < 0x80 ? 1 : ReadVarintMulti(ref c0, count, out count);
        }
        else if ((n = ReadVarintChecked(data, p, end, out count)) == 0)
        {
            return 0;
        }
        if (count > ARRAY_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "array count");
        }
        p += n;
        int remaining = (int)count;

        // Single type+length header for the whole array. A fixlen array always
        // carries its fixlen_word, even when empty (§4.8): the header is read and
        // validated here, and the payload loop below simply runs zero times.
        // ArrayBegin deliberately has NOT fired yet — §4.8 fixes the order as
        // count word, format ceiling, fixlen_word, then the hook, so the receiver
        // learns the element subtype (fp32 vs fp64) before it judges the field.
        int hn = ReadVarint(data, p, end, out ulong lenHeader);
        if (hn == 0)
        {
            // Header split across the Feed boundary: resume reading it in the
            // byte machine's FixlenLen state, which fires ArrayBegin once the
            // word is complete.
            _id = id;
            _inArray = true;
            _arrayFixlen = true;
            _arrayRemaining = remaining;
            _arrayCount = remaining;
            _accLen = 0;
            _accBits = 0;
            _state = State.FixlenLen;
            return p - start;
        }

        // Decoded inline rather than through a helper: the tag is three bits and
        // only 0..3 are assigned, so the reserved subtypes 4..7 (§4.6) are one
        // unsigned comparison away.
        var subtype = (FixlenType)(lenHeader & 0x07);
        if ((uint)subtype > (uint)FixlenType.Blob)
        {
            ThrowFixlenType((int)(lenHeader & 0x07));
        }
        ulong lengthValue = lenHeader >> 3;
        if (lengthValue > ARRAY_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "fixlen length " + lengthValue);
        }
        int length = (int)lengthValue;

        int need;
        ArrayKind kind;
        if (subtype == FixlenType.Fp32)
        {
            if (length != 4)
            {
                throw new SofabException(SofabError.InvalidMessage, "fp32 length " + length);
            }
            need = 4;
            kind = ArrayKind.Fp32;
        }
        else if (subtype == FixlenType.Fp64)
        {
            if (length != 8)
            {
                throw new SofabException(SofabError.InvalidMessage, "fp64 length " + length);
            }
            need = 8;
            kind = ArrayKind.Fp64;
        }
        else
        {
            // String/blob are not valid as fixlen-array elements. §4.8 allows only
            // fixed-width subtypes here, so this is a FORMAT violation judged
            // before the hook fires -- never a §7.3 schema-mismatch skip.
            throw new SofabException(SofabError.InvalidMessage, "dynamic fixlen array element");
        }
        p += hn;

        // The subtype is now known and legal: announce the array. A zero-count
        // array still gets exactly this one call, with the correct kind.
        visitor.ArrayBegin(id, kind, remaining);

        while (remaining > 0)
        {
            if (end - p < need)
            {
                // Payload split across the Feed boundary: resume reading this
                // element in the byte machine's FixlenVal state.
                _id = id;
                _inArray = true;
                _arrayFixlen = false; // the word is behind us; the hook has fired
                _arrayKind = kind;
                _arrayRemaining = remaining;
                _fixlenType = subtype;
                _fixlenTotal = need;
                _fixlenRemaining = need;
                _accLen = 0;
                _accBits = 0;
                _state = State.FixlenVal;
                return p - start;
            }
            if (need == 4)
            {
                visitor.Fp32(id, BitConverter.Int32BitsToSingle(ReadInt32Le(data, p)));
            }
            else
            {
                visitor.Fp64(id, BitConverter.Int64BitsToDouble(ReadInt64Le(data, p)));
            }
            p += need;
            remaining--;
        }
        return p - start;
    }

    /// <summary>
    /// Read a base-128 varint from <c>data[pos..end)</c>.
    /// </summary>
    /// <returns>
    /// The number of bytes consumed (&gt; 0) with the value in <paramref name="value"/>;
    /// or <c>0</c> if the varint is not fully present in the buffer.
    /// </returns>
    /// <exception cref="SofabException">on varint overflow (&gt; <see cref="VALUE_BITS"/> bits).</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadVarint(byte[] data, int pos, int end, out ulong value)
    {
        if (end - pos >= MaxVarintBytes)
        {
            return ReadVarintUnchecked(ref MemoryMarshal.GetArrayDataReference(data), pos, out value);
        }
        return ReadVarintChecked(data, pos, end, out value);
    }

    /// <summary>
    /// Decode a varint at <paramref name="pos"/> knowing the buffer holds at least
    /// <see cref="MaxVarintBytes"/> bytes there, so neither an end check nor an
    /// array bounds check is needed per byte.
    /// </summary>
    /// <remarks>
    /// Fully unrolled rather than looped: the shift is then an immediate and there
    /// is no shift counter to maintain or test, which is what makes the
    /// single-byte case (every small field header and small scalar — the common
    /// case by design, CORELIB_PLAN §1) two instructions and each further byte
    /// about four.
    /// <para>
    /// The 64-bit bound (§4.1) is enforced where it can actually be violated — the
    /// tenth byte — instead of on every byte: bytes 1..9 carry 63 payload bits and
    /// can never overflow, and on the tenth only bit 0 fits, so any value above
    /// <c>1</c> either spills past bit 63 or continues into an 11th byte. Both are
    /// the <c>INVALID</c> outcome, and one comparison rejects both.
    /// </para>
    /// </remarks>
    /// <returns>the number of bytes consumed (1..<see cref="MaxVarintBytes"/>)</returns>
    /// <exception cref="SofabException">on varint overflow (&gt; <see cref="VALUE_BITS"/> bits).</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadVarintUnchecked(ref byte data, int pos, out ulong value)
    {
        ref byte b = ref Unsafe.Add(ref data, (nint)(uint)pos);
        ulong v = b;
        if (v < 0x80)
        {
            value = v;
            return 1;
        }
        return ReadVarintMulti(ref b, v, out value);
    }

    /// <summary>
    /// Decode a varint of two or more bytes whose first byte is
    /// <paramref name="first"/>, knowing at least <see cref="MaxVarintBytes"/>
    /// bytes are readable from <paramref name="b"/>.
    /// </summary>
    /// <remarks>
    /// Decodes eight bytes at a time with word arithmetic (SWAR) instead of
    /// walking the encoding byte by byte. One unaligned 64-bit load covers the
    /// whole common case; the terminating byte is the lowest byte whose
    /// continuation bit is clear, which <c>~word &amp; 0x80..80</c> isolates and a
    /// trailing-zero count locates, and the bytes past it are masked away. The
    /// three shift-and-merge steps then close the one-bit gap each byte leaves,
    /// pairing 7-bit groups into 14-, then 28-, then 56-bit halves — so the
    /// payload is gathered in a fixed dozen instructions regardless of length,
    /// where a per-byte loop costs a load, mask, shift, merge and test each time.
    /// <para>
    /// Only a varint spilling past eight bytes (a value ≥ 2^56) needs the tail
    /// below, and only its tenth byte can break the 64-bit bound (§4.1): bit 63
    /// is the sole payload bit left there, so anything above <c>1</c> either
    /// overflows or continues into an eleventh byte, and one comparison rejects
    /// both. This is deliberately portable word arithmetic — no BMI2
    /// <c>PEXT</c> — because that instruction is microcoded and an order of
    /// magnitude slower on some x86-64 parts, which would trade real throughput
    /// on those hosts for a lower instruction count.
    /// </para>
    /// </remarks>
    /// <param name="b">reference to the varint's first byte</param>
    /// <param name="first">that first byte, continuation flag still set</param>
    /// <param name="value">the decoded value</param>
    /// <returns>the number of bytes consumed (2..<see cref="MaxVarintBytes"/>)</returns>
    /// <exception cref="SofabException">on varint overflow (&gt; <see cref="VALUE_BITS"/> bits).</exception>
    private static int ReadVarintMulti(ref byte b, ulong first, out ulong value)
    {
        ulong word = Unsafe.ReadUnaligned<ulong>(ref b);
        if (!BitConverter.IsLittleEndian)
        {
            word = BinaryPrimitives.ReverseEndianness(word);
        }

        // A clear continuation bit marks the last byte of the encoding.
        ulong ends = ~word & ContinuationBits;
        if (ends != 0)
        {
            int tz = BitOperations.TrailingZeroCount(ends);
            // Keep bits 0..tz — every byte up to and including the terminator —
            // and drop whatever followed the varint in the buffer.
            word &= ends ^ (ends - 1);
            ulong g = word & PayloadBits;
            g = ((g & 0x7F007F007F007F00UL) >> 1) | (g & 0x007F007F007F007FUL);
            g = ((g & 0x3FFF00003FFF0000UL) >> 2) | (g & 0x00003FFF00003FFFUL);
            value = ((g & 0x0FFFFFFF00000000UL) >> 4) | (g & 0x000000000FFFFFFFUL);
            return (tz >> 3) + 1;
        }

        // Eight continuation bytes: 56 payload bits so far, at most two to go.
        ulong low = word & PayloadBits;
        low = ((low & 0x7F007F007F007F00UL) >> 1) | (low & 0x007F007F007F007FUL);
        low = ((low & 0x3FFF00003FFF0000UL) >> 2) | (low & 0x00003FFF00003FFFUL);
        low = ((low & 0x0FFFFFFF00000000UL) >> 4) | (low & 0x000000000FFFFFFFUL);
        ulong x = Unsafe.Add(ref b, 8);
        low |= (x & 0x7F) << 56;
        if (x < 0x80)
        {
            value = low;
            return 9;
        }
        x = Unsafe.Add(ref b, 9);
        if (x > 1)
        {
            ThrowVarintOverflow();
        }
        value = low | (x << 63);
        return MaxVarintBytes;
    }


    /// <summary>Raise the <c>INVALID</c> outcome for a reserved fixlen subtype (§4.6).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFixlenType(int raw) =>
        throw new SofabException(SofabError.InvalidMessage, "fixlen type " + raw);

    /// <summary>Raise the <c>INVALID</c> outcome for a varint past the 64-bit bound (§4.1).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowVarintOverflow() =>
        throw new SofabException(SofabError.InvalidMessage, "varint overflow");

    /// <summary>
    /// Re-raise a terminal verdict this decoder already reached, for a caller that
    /// caught the first one and fed on anyway (§5.2).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowLatched(State state) =>
        throw new SofabException(
            state == State.Rejected ? SofabError.InvalidMessage : SofabError.LimitExceeded,
            "stream already rejected");

    /// <summary>
    /// Per-byte decode for the buffer tail, where fewer than
    /// <see cref="MaxVarintBytes"/> bytes remain; <c>0</c> when the varint is not
    /// complete in them.
    /// </summary>
    /// <remarks>
    /// Both callers reach this only with fewer than <see cref="MaxVarintBytes"/>
    /// bytes left, so at most nine can be consumed — and nine bytes carry 63
    /// payload bits, one short of the 64-bit bound (§4.1). Only a tenth byte can
    /// break that bound and by construction it is not in this buffer: such a
    /// varint is <c>INCOMPLETE</c> here and is decoded, and bounds-checked, by the
    /// fast path once the next chunk arrives. So no per-byte overflow test is
    /// needed, which matters because a short message ends in this path — every
    /// field within <see cref="MaxVarintBytes"/> bytes of the end comes through here.
    /// </remarks>
    private static int ReadVarintChecked(byte[] data, int pos, int end, out ulong value)
    {
        ref byte origin = ref MemoryMarshal.GetArrayDataReference(data);
        ulong v = 0;
        int shift = 0;
        for (int p = pos; p < end; shift += 7)
        {
            ulong b = Unsafe.Add(ref origin, (nint)(uint)p);
            p++;
            v |= (b & 0x7F) << shift;
            if (b < 0x80)
            {
                value = v;
                return p - pos;
            }
        }
        value = 0;
        return 0;
    }

    /// <summary>Read 4 little-endian bytes at <paramref name="p"/> as an <c>int</c> bit pattern.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadInt32Le(byte[] d, int p) =>
        BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p, 4));

    /// <summary>Read 8 little-endian bytes at <paramref name="p"/> as a <c>long</c> bit pattern.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ReadInt64Le(byte[] d, int p) =>
        BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(p, 8));

    /// <summary>
    /// Feed one byte into the byte-at-a-time state machine, dispatching to the
    /// handler for the current <see cref="State"/>. This is the slow-path fallback
    /// used for the tail of a field split across a <see cref="Feed(byte[], int, int, IVisitor)"/>
    /// boundary (<c>FixlenRaw</c> payloads are streamed in bulk by <c>Feed</c> itself).
    /// </summary>
    /// <param name="b">the next input byte (low 8 bits used)</param>
    /// <param name="visitor">sink for decoded fields</param>
    private void Step(int b, IVisitor visitor)
    {
        switch (_state)
        {
            case State.Idle: StepIdle(b, visitor); break;
            case State.VarintUnsigned: StepVarintUnsigned(b, visitor); break;
            case State.VarintSigned: StepVarintSigned(b, visitor); break;
            case State.FixlenLen: StepFixlenLen(b, visitor); break;
            case State.FixlenVal: StepFixlenVal(b, visitor); break;
            case State.ArrayCount: StepArrayCount(b, visitor); break;
            default: /* FixlenRaw handled in Feed's bulk path */ break;
        }
    }

    /// <summary>
    /// Feed one byte into the varint accumulator.
    /// </summary>
    /// <returns><c>true</c> if a complete value is now in <see cref="_varintOut"/>;
    /// <c>false</c> if more bytes are needed</returns>
    private bool VarintPush(int b)
    {
        int chunk = b & 0x7F;
        // Reject an overlong (>64-bit) varint: any payload bit that would spill
        // past bit 63 makes the input malformed (§4.1/§6.3).
        int room = VALUE_BITS - _varintShift;
        if (room < 7 && (uint)chunk >> room != 0)
        {
            _varintValue = 0;
            _varintShift = 0;
            throw new SofabException(SofabError.InvalidMessage, "varint overflow");
        }
        _varintValue |= ((ulong)chunk) << _varintShift;
        _varintShift += 7;

        if ((b & 0x80) == 0)
        {
            _varintOut = _varintValue;
            _varintValue = 0;
            _varintShift = 0;
            return true;
        }

        if (_varintShift >= VALUE_BITS)
        {
            _varintValue = 0;
            _varintShift = 0;
            throw new SofabException(SofabError.InvalidMessage, "varint overflow");
        }
        return false;
    }

    /// <summary>
    /// Accumulate the field-header varint; once complete, decode the id and 3-bit
    /// wire type and transition to the matching value/array/sequence state.
    /// Sequences are emitted inline (depth-checked) and leave the machine idle.
    /// </summary>
    /// <param name="b">the next input byte</param>
    /// <param name="visitor">sink for decoded fields</param>
    private void StepIdle(int b, IVisitor visitor)
    {
        if (!VarintPush(b))
        {
            return;
        }
        ulong header = _varintOut;
        int wireType = (int)(header & 0x07);
        ulong idValue = header >> 3;
        if (idValue > (ulong)ID_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "id " + idValue);
        }
        _id = (int)idValue;
        _inArray = false;
        _arrayFixlen = false;

        switch (wireType)
        {
            case T_VARINT_UNSIGNED:
                _state = State.VarintUnsigned;
                break;
            case T_VARINT_SIGNED:
                _state = State.VarintSigned;
                break;
            case T_FIXLEN:
                _state = State.FixlenLen;
                break;
            case T_VARINTARRAY_UNSIGNED:
                _arrayKind = ArrayKind.Unsigned;
                _state = State.ArrayCount;
                break;
            case T_VARINTARRAY_SIGNED:
                _arrayKind = ArrayKind.Signed;
                _state = State.ArrayCount;
                break;
            case T_FIXLENARRAY:
                // _arrayKind stays unset: fp32 vs fp64 is only decided by the
                // fixlen_word, which follows the count word (§4.8).
                _arrayFixlen = true;
                _state = State.ArrayCount;
                break;
            case T_SEQUENCE_START:
                if (_depth >= (ulong)MAX_DEPTH)
                {
                    throw new SofabException(SofabError.InvalidMessage, "sequence too deep");
                }
                _depth++;
                visitor.SequenceBegin(_id);
                // stays Idle
                break;
            case T_SEQUENCE_END:
                if (_depth == 0)
                {
                    throw new SofabException(SofabError.InvalidMessage, "dangling sequence end");
                }
                _depth--;
                visitor.SequenceEnd();
                // stays Idle
                break;
            default:
                throw new SofabException(SofabError.InvalidMessage, "field type " + wireType);
        }
    }

    /// <summary>
    /// Accumulate an unsigned-varint value (a scalar field or one array element);
    /// once complete, push it to the visitor and advance to the next element or
    /// back to idle.
    /// </summary>
    /// <param name="b">the next input byte</param>
    /// <param name="visitor">sink for decoded fields</param>
    private void StepVarintUnsigned(int b, IVisitor visitor)
    {
        if (VarintPush(b))
        {
            visitor.Unsigned(_id, _varintOut);
            AdvanceAfterElement();
        }
    }

    /// <summary>
    /// Accumulate a signed-varint value (a scalar field or one array element);
    /// once complete, ZigZag-decode it, push it to the visitor and advance to the
    /// next element or back to idle.
    /// </summary>
    /// <param name="b">the next input byte</param>
    /// <param name="visitor">sink for decoded fields</param>
    private void StepVarintSigned(int b, IVisitor visitor)
    {
        if (VarintPush(b))
        {
            visitor.Signed(_id, ZigzagDecode(_varintOut));
            AdvanceAfterElement();
        }
    }

    /// <summary>Shared "next element or back to idle" logic for varint scalars/arrays.</summary>
    private void AdvanceAfterElement()
    {
        if (_inArray)
        {
            _arrayRemaining--;
            if (_arrayRemaining > 0)
            {
                return; // stay in the same state for the next element
            }
            _inArray = false;
        }
        _state = State.Idle;
    }

    /// <summary>
    /// Accumulate the fixlen type+length header. Once complete it validates the
    /// sub-type and length, then: floats transition to <c>FixlenVal</c>; a
    /// zero-length string/blob is emitted immediately as an empty chunk; a
    /// non-empty string/blob transitions to <c>FixlenRaw</c> for chunked streaming.
    /// String/blob sub-types are rejected when reached as a fixlen-array element.
    /// This is also where the field's header hook fires, once the validated word
    /// has named the subtype and length: <see cref="IVisitor.FixlenBegin"/> for a
    /// scalar fixlen field, or <see cref="IVisitor.ArrayBegin"/> for a fixlen
    /// array (§4.8) — one field, one hook, always before any payload byte.
    /// </summary>
    /// <param name="b">the next input byte</param>
    /// <param name="visitor">sink for decoded fields</param>
    private void StepFixlenLen(int b, IVisitor visitor)
    {
        if (!VarintPush(b))
        {
            return;
        }
        ulong header = _varintOut;
        var subtype = (FixlenType)(header & 0x07);
        if ((uint)subtype > (uint)FixlenType.Blob)
        {
            ThrowFixlenType((int)(header & 0x07));
        }
        ulong lengthValue = header >> 3;
        if (lengthValue > ARRAY_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "fixlen length " + lengthValue);
        }
        int length = (int)lengthValue;

        _fixlenType = subtype;
        _fixlenTotal = length;
        _fixlenRemaining = length;
        _accLen = 0;
        _accBits = 0;

        // Everything the FORMAT rejects is judged here, ahead of either hook: a
        // reserved subtype (above), a wrong-width float (§4.6) and a dynamic
        // subtype used as a fixlen-array element (§4.8) are INVALID regardless
        // of what follows, never a §7.3 skip the receiver gets a say in.
        switch (subtype)
        {
            case FixlenType.Fp32:
                if (length != 4)
                {
                    throw new SofabException(SofabError.InvalidMessage, "fp32 length " + length);
                }
                _arrayKind = ArrayKind.Fp32;
                _state = State.FixlenVal;
                break;
            case FixlenType.Fp64:
                if (length != 8)
                {
                    throw new SofabException(SofabError.InvalidMessage, "fp64 length " + length);
                }
                _arrayKind = ArrayKind.Fp64;
                _state = State.FixlenVal;
                break;
            case FixlenType.String:
            case FixlenType.Blob:
                if (_inArray)
                {
                    throw new SofabException(SofabError.InvalidMessage, "dynamic fixlen array element");
                }
                // A non-empty payload streams through FixlenRaw; an empty one has
                // no payload state at all and is emitted below, after the field
                // has been announced.
                _state = length == 0 ? State.Idle : State.FixlenRaw;
                break;
            default:
                throw new SofabException(SofabError.InvalidMessage, "fixlen type");
        }

        // The word is read and validated, so the field's declared length is now
        // established -- announce it before a single payload byte is consumed or
        // waited for. This is the whole point of the hook: a maxlen violation is
        // decided by this word, so it must be decidable here, whether or not the
        // message ends right at it (§5.2, INVALID over INCOMPLETE). A fixlen
        // ARRAY is announced by ArrayBegin below instead -- its ArrayKind already
        // names the element subtype, and one field gets one header hook.
        if (!_inArray)
        {
            visitor.FixlenBegin(_id, subtype, length);
        }

        if (length == 0 && (subtype == FixlenType.String || subtype == FixlenType.Blob))
        {
            if (subtype == FixlenType.String)
            {
                visitor.String(_id, 0, 0, EmptyPayload, 0, 0);
            }
            else
            {
                visitor.Blob(_id, 0, 0, EmptyPayload, 0, 0);
            }
        }

        // The fixlen_word of a fixlen array has now been consumed and validated,
        // so the element subtype is known: announce the array (§4.8 step 5). This
        // is the one and only ArrayBegin for this field -- the machine never
        // re-enters FixlenLen for the array's later elements.
        if (_arrayFixlen)
        {
            _arrayFixlen = false;
            visitor.ArrayBegin(_id, _arrayKind, _arrayCount);
        }

        // An empty fixlen array (§4.8) carries its fixlen_word but no payload: the
        // word has now been consumed and validated, so finish the array rather
        // than reading a (non-existent) element.
        if (_inArray && _arrayRemaining == 0)
        {
            _inArray = false;
            _state = State.Idle;
        }
    }

    /// <summary>
    /// Accumulate the raw little-endian bytes of a fixed-size float value
    /// (<c>fp32</c> / <c>fp64</c>) into <see cref="_accBits"/>. Once the value is
    /// complete it is decoded and pushed to the visitor; within an array the
    /// element size is reused for the next element, otherwise the machine returns
    /// to idle.
    /// </summary>
    /// <param name="b">the next input byte</param>
    /// <param name="visitor">sink for decoded fields</param>
    private void StepFixlenVal(int b, IVisitor visitor)
    {
        // Little-endian: byte k of the payload contributes bits [8k, 8k+8).
        _accBits |= (ulong)(byte)b << (_accLen * 8);
        _accLen++;
        _fixlenRemaining--;
        if (_fixlenRemaining != 0)
        {
            return;
        }

        if (_fixlenType == FixlenType.Fp32)
        {
            visitor.Fp32(_id, BitConverter.Int32BitsToSingle((int)(uint)_accBits));
        }
        else if (_fixlenType == FixlenType.Fp64)
        {
            visitor.Fp64(_id, BitConverter.Int64BitsToDouble((long)_accBits));
        }
        else
        {
            throw new SofabException(SofabError.InvalidMessage, "fixlen value type");
        }

        // Next array element (reuse the element size) or back to idle.
        if (_inArray)
        {
            _arrayRemaining--;
            if (_arrayRemaining > 0)
            {
                _fixlenRemaining = _fixlenTotal;
                _accLen = 0;
                _accBits = 0;
                return;
            }
            _inArray = false;
        }
        _state = State.Idle;
    }

    /// <summary>
    /// Accumulate an array's element-count varint. Once complete it enforces the
    /// format ceiling and transitions to the per-element state. An integer array
    /// is announced via <see cref="IVisitor.ArrayBegin"/> here; a fixlen array is
    /// announced later, from <see cref="StepFixlenLen"/>, once its
    /// <c>fixlen_word</c> has named the element subtype (§4.8).
    /// </summary>
    /// <param name="b">the next input byte</param>
    /// <param name="visitor">sink for decoded fields</param>
    private void StepArrayCount(int b, IVisitor visitor)
    {
        if (!VarintPush(b))
        {
            return;
        }
        ulong count = _varintOut;
        if (count > ARRAY_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "array count");
        }
        int c = (int)count;
        _arrayCount = c;

        // A fixlen array (§4.8) always carries its fixlen_word next -- even when
        // empty -- and only that word says fp32 or fp64. So do NOT announce the
        // array here: read the word first (FixlenLen), which fires ArrayBegin
        // once the subtype is known and legal. This is what lets a receiver
        // decide the field is not its array's value (MESSAGE_SPEC §7.3) before
        // applying any schema bound, and what makes a message truncated between
        // the two words INCOMPLETE rather than judged on the count alone.
        if (_arrayFixlen)
        {
            _arrayRemaining = c;
            _inArray = true;
            _state = State.FixlenLen;
            return;
        }

        // An integer array's kind is fixed by its wire type, so its hook fires
        // right here, immediately after the count word.
        visitor.ArrayBegin(_id, _arrayKind, c);

        // A zero-count integer array is just [ header ][ count=0 ] (§4.7): no
        // elements follow, so return straight to idle.
        if (c == 0)
        {
            _inArray = false;
            _state = State.Idle;
            return;
        }

        _arrayRemaining = c;
        _inArray = true;
        _state = _arrayKind == ArrayKind.Signed ? State.VarintSigned : State.VarintUnsigned;
    }
}
