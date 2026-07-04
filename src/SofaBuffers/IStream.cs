/*
 * SofaBuffers C# - streaming input decoder (port of istream.c).
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

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
/// Unlike the C decoder there is no per-field "bind a destination" step and no
/// explicit skip bookkeeping: an <see cref="IVisitor"/> simply ignores fields it
/// does not care about. Scalars and floats are delivered whole; string / blob
/// payloads are delivered in chunks (so they may exceed RAM); array elements are
/// announced with <see cref="IVisitor.ArrayBegin"/> and then delivered through
/// the scalar / float callbacks.
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
    private enum State
    {
        Idle,
        VarintUnsigned,
        VarintSigned,
        FixlenLen,
        FixlenVal,
        FixlenRaw,
        ArrayCount,
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

    // fixlen context
    private FixlenType _fixlenType = FixlenType.Fp32;
    private int _fixlenTotal;
    private int _fixlenRemaining;
    private readonly byte[] _acc = new byte[8];
    private int _accLen;

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
    /// <exception cref="SofabException">
    /// with <see cref="SofabError.InvalidMessage"/> on malformed input
    /// </exception>
    public void Feed(byte[] data, IVisitor visitor)
    {
        Feed(data, 0, data.Length, visitor);
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
    /// <exception cref="SofabException">
    /// with <see cref="SofabError.InvalidMessage"/> on malformed input
    /// </exception>
    public void Feed(byte[] data, int off, int len, IVisitor visitor)
    {
        int i = off;
        int endExclusive = off + len;
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
        int n = ReadVarint(data, start, end, out ulong header);
        if (n == 0)
        {
            return 0;
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
                int m = ReadVarint(data, p, end, out ulong value);
                if (m == 0)
                {
                    return 0;
                }
                visitor.Unsigned(id, value);
                return p + m - start;
            }
            case T_VARINT_SIGNED:
            {
                int m = ReadVarint(data, p, end, out ulong value);
                if (m == 0)
                {
                    return 0;
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
        int n = ReadVarint(data, p, end, out ulong lenHeader);
        if (n == 0)
        {
            return 0;
        }
        p += n;

        FixlenType subtype = FixlenTypeExtensions.FromRaw((int)(lenHeader & 0x07));
        ulong lengthValue = lenHeader >> 3;
        if (lengthValue > ARRAY_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "fixlen length " + lengthValue);
        }
        int length = (int)lengthValue;

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
                visitor.Fp64(id, BitConverter.Int64BitsToDouble(ReadInt64Le(data, p)));
                return p + 8 - start;
            case FixlenType.String:
            case FixlenType.Blob:
                if (length == 0)
                {
                    if (subtype == FixlenType.String)
                    {
                        visitor.String(id, 0, 0, _acc, 0, 0);
                    }
                    else
                    {
                        visitor.Blob(id, 0, 0, _acc, 0, 0);
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
        int n = ReadVarint(data, p, end, out ulong count);
        if (n == 0)
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

        while (remaining > 0)
        {
            int m = ReadVarint(data, p, end, out ulong value);
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
            if (signed)
            {
                visitor.Signed(id, ZigzagDecode(value));
            }
            else
            {
                visitor.Unsigned(id, value);
            }
            p += m;
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
        int n = ReadVarint(data, p, end, out ulong count);
        if (n == 0)
        {
            return 0;
        }
        if (count > ARRAY_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "array count");
        }
        p += n;
        int remaining = (int)count;
        visitor.ArrayBegin(id, ArrayKind.Fixlen, remaining);

        // Single type+length header for the whole array. A fixlen array always
        // carries its fixlen_word, even when empty (§4.8): the header is read and
        // validated here, and the payload loop below simply runs zero times.
        int hn = ReadVarint(data, p, end, out ulong lenHeader);
        if (hn == 0)
        {
            // Header split across the Feed boundary: resume reading it in the
            // byte machine's FixlenLen state.
            _id = id;
            _inArray = true;
            _arrayKind = ArrayKind.Fixlen;
            _arrayRemaining = remaining;
            _accLen = 0;
            _state = State.FixlenLen;
            return p - start;
        }

        FixlenType subtype = FixlenTypeExtensions.FromRaw((int)(lenHeader & 0x07));
        ulong lengthValue = lenHeader >> 3;
        if (lengthValue > ARRAY_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "fixlen length " + lengthValue);
        }
        int length = (int)lengthValue;

        int need;
        if (subtype == FixlenType.Fp32)
        {
            if (length != 4)
            {
                throw new SofabException(SofabError.InvalidMessage, "fp32 length " + length);
            }
            need = 4;
        }
        else if (subtype == FixlenType.Fp64)
        {
            if (length != 8)
            {
                throw new SofabException(SofabError.InvalidMessage, "fp64 length " + length);
            }
            need = 8;
        }
        else
        {
            // String/blob are not valid as fixlen-array elements.
            throw new SofabException(SofabError.InvalidMessage, "dynamic fixlen array element");
        }
        p += hn;

        while (remaining > 0)
        {
            if (end - p < need)
            {
                // Payload split across the Feed boundary: resume reading this
                // element in the byte machine's FixlenVal state.
                _id = id;
                _inArray = true;
                _arrayKind = ArrayKind.Fixlen;
                _arrayRemaining = remaining;
                _fixlenType = subtype;
                _fixlenTotal = need;
                _fixlenRemaining = need;
                _accLen = 0;
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
        int p = pos;
        if (end - p >= 10)
        {
            // Room for a max-length (10-byte) varint: decode with no per-byte
            // end check, continuation flagged by the sign bit of the raw byte.
            int b = (sbyte)data[p++];
            ulong v = (ulong)(b & 0x7F);
            if (b < 0)
            {
                int shift = 7;
                do
                {
                    b = (sbyte)data[p++];
                    v |= ((ulong)(b & 0x7F)) << shift;
                    shift += 7;
                }
                while (b < 0 && shift < VALUE_BITS);
                if (b < 0)
                {
                    throw new SofabException(SofabError.InvalidMessage, "varint overflow");
                }
            }
            value = v;
            return p - pos;
        }
        return ReadVarintChecked(data, pos, end, out value);
    }

    /// <summary>Per-byte checked decode for the buffer tail; 0 when incomplete.</summary>
    private static int ReadVarintChecked(byte[] data, int pos, int end, out ulong value)
    {
        ulong v = 0;
        int shift = 0;
        int p = pos;
        while (p < end)
        {
            int b = data[p++] & 0xFF;
            v |= ((ulong)(b & 0x7F)) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                value = v;
                return p - pos;
            }
            if (shift >= VALUE_BITS)
            {
                throw new SofabException(SofabError.InvalidMessage, "varint overflow");
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
        _varintValue |= ((ulong)(b & 0x7F)) << _varintShift;
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
                _arrayKind = ArrayKind.Fixlen;
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
        FixlenType subtype = FixlenTypeExtensions.FromRaw((int)(header & 0x07));
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

        switch (subtype)
        {
            case FixlenType.Fp32:
                if (length != 4)
                {
                    throw new SofabException(SofabError.InvalidMessage, "fp32 length " + length);
                }
                _state = State.FixlenVal;
                break;
            case FixlenType.Fp64:
                if (length != 8)
                {
                    throw new SofabException(SofabError.InvalidMessage, "fp64 length " + length);
                }
                _state = State.FixlenVal;
                break;
            case FixlenType.String:
            case FixlenType.Blob:
                // String/blob are not valid as fixlen-array elements.
                if (_inArray)
                {
                    throw new SofabException(SofabError.InvalidMessage, "dynamic fixlen array element");
                }
                if (length == 0)
                {
                    if (subtype == FixlenType.String)
                    {
                        visitor.String(_id, 0, 0, _acc, 0, 0);
                    }
                    else
                    {
                        visitor.Blob(_id, 0, 0, _acc, 0, 0);
                    }
                    _state = State.Idle;
                }
                else
                {
                    _state = State.FixlenRaw;
                }
                break;
            default:
                throw new SofabException(SofabError.InvalidMessage, "fixlen type");
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
    /// (<c>fp32</c> / <c>fp64</c>) into <see cref="_acc"/>. Once the value is
    /// complete it is decoded and pushed to the visitor; within an array the
    /// element size is reused for the next element, otherwise the machine returns
    /// to idle.
    /// </summary>
    /// <param name="b">the next input byte</param>
    /// <param name="visitor">sink for decoded fields</param>
    private void StepFixlenVal(int b, IVisitor visitor)
    {
        _acc[_accLen++] = (byte)b;
        _fixlenRemaining--;
        if (_fixlenRemaining != 0)
        {
            return;
        }

        if (_fixlenType == FixlenType.Fp32)
        {
            int bits = (_acc[0] & 0xFF)
                    | ((_acc[1] & 0xFF) << 8)
                    | ((_acc[2] & 0xFF) << 16)
                    | ((_acc[3] & 0xFF) << 24);
            visitor.Fp32(_id, BitConverter.Int32BitsToSingle(bits));
        }
        else if (_fixlenType == FixlenType.Fp64)
        {
            long bits = 0;
            for (int i = 0; i < 8; i++)
            {
                bits |= ((long)(_acc[i] & 0xFF)) << (i * 8);
            }
            visitor.Fp64(_id, BitConverter.Int64BitsToDouble(bits));
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
                return;
            }
            _inArray = false;
        }
        _state = State.Idle;
    }

    /// <summary>
    /// Accumulate an array's element-count varint. Once complete it validates the
    /// count, announces the array via <see cref="IVisitor.ArrayBegin"/>, and
    /// transitions to the per-element state for the array's <see cref="ArrayKind"/>.
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
        visitor.ArrayBegin(_id, _arrayKind, c);

        // A zero-count array has no elements. An empty integer array (§4.7) ends
        // right here. An empty fixlen array (§4.8) still carries its fixlen_word,
        // so enter FixlenLen (with _arrayRemaining == 0) to read and validate it
        // before finishing; any other zero-count array returns straight to idle.
        if (c == 0)
        {
            if (_arrayKind == ArrayKind.Fixlen)
            {
                _arrayRemaining = 0;
                _inArray = true;
                _state = State.FixlenLen;
                return;
            }
            _inArray = false;
            _state = State.Idle;
            return;
        }

        _arrayRemaining = c;
        _inArray = true;

        switch (_arrayKind)
        {
            case ArrayKind.Unsigned:
                _state = State.VarintUnsigned;
                break;
            case ArrayKind.Signed:
                _state = State.VarintSigned;
                break;
            case ArrayKind.Fixlen:
            default:
                _state = State.FixlenLen;
                break;
        }
    }
}
