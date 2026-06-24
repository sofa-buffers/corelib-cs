/*
 * SofaBuffers C# - streaming input decoder (port of istream.c).
 *
 * SPDX-License-Identifier: MIT
 */

using System;

using static SofaBuffers.WireFormat;

namespace SofaBuffers;

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

            Step(data[i] & 0xFF, visitor);
            i++;
        }
    }

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
                if (_depth == ulong.MaxValue)
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

    private void StepVarintUnsigned(int b, IVisitor visitor)
    {
        if (VarintPush(b))
        {
            visitor.Unsigned(_id, _varintOut);
            AdvanceAfterElement();
        }
    }

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
    }

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

    private void StepArrayCount(int b, IVisitor visitor)
    {
        if (!VarintPush(b))
        {
            return;
        }
        ulong count = _varintOut;
        if (count == 0 || count > ARRAY_MAX)
        {
            throw new SofabException(SofabError.InvalidMessage, "array count");
        }
        int c = (int)count;
        _arrayRemaining = c;
        _inArray = true;
        visitor.ArrayBegin(_id, _arrayKind, c);

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
