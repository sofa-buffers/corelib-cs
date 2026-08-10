/*
 * SofaBuffers C# - streaming output encoder (port of ostream.c).
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using static sofab.WireFormat;

namespace sofab;

/// <summary>
/// Streaming SofaBuffers encoder writing into a caller-provided byte buffer.
/// </summary>
/// <remarks>
/// The encoder never allocates the output buffer itself: it writes into the
/// array you hand it. When that array fills, the accumulated bytes are passed to
/// an optional <see cref="FlushSink"/> and writing resumes at the start of the
/// buffer, so a message larger than the buffer (or larger than RAM) can be
/// streamed out. With no sink, a full buffer raises
/// <see cref="SofabError.BufferFull"/>.
/// <para>
/// An initial <c>offset</c> reserves space at the front of the buffer for a
/// lower-layer protocol header, avoiding a copy. A sink that takes the buffer it
/// is handed installs a replacement with <see cref="BufferSet"/> and gets that
/// reservation again — the offset belongs to the installation, not to the buffer
/// (CORELIB_PLAN §5.1).
/// </para>
/// <para>
/// <b>Hot-path convention.</b> The single-byte varint case — every small field
/// header and small scalar, which the format is designed to make the common one
/// (CORELIB_PLAN §1) — is written out at each call site rather than shared
/// behind a helper, and each writer checks buffer capacity once for a whole
/// field instead of once per byte. Measured under Callgrind
/// (<c>bench/run_callgrind.sh</c>) the JIT does not inline a helper that itself
/// contains a call, even when marked <c>AggressiveInlining</c>, which cost about
/// twenty instructions per varint written; longer varints go to
/// <c>WriteVarintAtMulti</c>, which assembles the whole encoding in a register.
/// </para>
/// <para>This class is not thread-safe; encode one message from one thread.</para>
/// </remarks>
/// <example>
/// <code>
/// byte[] buf = new byte[64];
/// var os = new OStream(buf);
/// os.WriteUnsigned(1, 42);
/// os.WriteSigned(2, -7);
/// os.WriteString(3, "hi");
/// int used = os.BytesUsed;
/// </code>
/// </example>
public sealed class OStream
{
    private byte[] _buffer;
    private int _end;
    private int _offset;
    private int _depth;

    /// <summary>
    /// Ids of the innermost open sequences whose header has not been written yet
    /// (MESSAGE_SPEC §2 lazy framing). Always a contiguous suffix of the open
    /// sequences: writing any field commits the whole run at once, so
    /// <see cref="WriteSequenceEnd"/> can simply pop the last entry.
    /// </summary>
    /// <remarks>
    /// The run is <b>unbounded</b>: CORELIB_PLAN §6 requires an implementation that
    /// can allocate to hold back to the full <c>MAX_DEPTH</c>, so this encoder is
    /// canonical at every depth the format permits. Only a heap-free profile may
    /// bound the run and frame eagerly past the bound; C# is not one, so there is
    /// no eager fallback and no depth constant to configure.
    /// <para>
    /// Spill storage only: the first <see cref="InlinePendingCapacity"/> ids live
    /// in <see cref="_inlinePending"/>, inside the encoder, so nesting that shallow
    /// — and an encoder that never opens a sequence at all — allocates nothing.
    /// This array appears only past that depth and grows by doubling;
    /// <c>MAX_DEPTH</c> caps it at 255 entries in total.
    /// </para>
    /// </remarks>
    private int[]? _pending;

    /// <summary>
    /// The first <see cref="InlinePendingCapacity"/> held-back ids, stored in the
    /// encoder itself so that the common shallow nesting holds a sequence back
    /// without allocating anything; <see cref="_pending"/> takes over beyond that.
    /// </summary>
    private PendingIds _inlinePending;

    /// <summary>Number of valid held-back ids (inline entries first, then <see cref="_pending"/>).</summary>
    private int _nPending;

    /// <summary>
    /// Held-back ids kept inline in the encoder. Nesting deeper than this spills
    /// to <see cref="_pending"/>, which grows on demand; real schemas rarely reach
    /// even this depth.
    /// </summary>
    private const int InlinePendingCapacity = 4;

    /// <summary>
    /// Entries the spill array allocates the first time nesting exceeds
    /// <see cref="InlinePendingCapacity"/>. Deeper nesting doubles it.
    /// </summary>
    private const int InitialPendingCapacity = 8;

    /// <summary>Inline storage for the first <see cref="InlinePendingCapacity"/> held-back ids.</summary>
    [InlineArray(InlinePendingCapacity)]
    private struct PendingIds
    {
        private int _first;
    }

    /// <summary>Longest possible varint encoding (10 bytes for a 64-bit value).</summary>
    private const int MaxVarintBytes = 10;

    /// <summary>The continuation flag of each of eight packed varint bytes.</summary>
    private const ulong ContinuationBits = 0x8080_8080_8080_8080UL;

    /// <summary>
    /// Longest string <see cref="WriteString"/> transcodes with its own scalar
    /// ASCII loop. Beyond this the runtime's vectorized UTF-8 encoder is faster
    /// than a byte-at-a-time copy, so the general path takes over.
    /// </summary>
    private const int AsciiFastPathMaxChars = 96;

    /// <summary>
    /// Longest UTF-8 encoding of a single Unicode scalar value (4 bytes, for a
    /// character written in C# as a surrogate pair). The chunked transcoder in
    /// <see cref="PushTranscoded"/> never hands the runtime encoder a
    /// destination smaller than this, because <c>Encoder.Convert</c> cannot emit
    /// a partial rune.
    /// </summary>
    private const int MaxUtf8BytesPerRune = 4;

    private readonly FlushSink? _sink;

    /// <summary>
    /// Stateful UTF-8 encoder used by <see cref="PushTranscoded"/> to split a
    /// string payload across flushes, created on first use and reused for the
    /// life of this <see cref="OStream"/>. Its state (a pending high surrogate)
    /// lives only within one <see cref="WriteString"/> call, which resets it
    /// before use. <c>null</c> until a string actually spans the buffer, so the
    /// common one-shot encode never pays for it.
    /// </summary>
    private Encoder? _utf8Encoder;

    /// <summary>
    /// Strict UTF-8 codec used for <see cref="WriteString"/>. Constructed with
    /// <c>throwOnInvalidBytes: true</c> so that an unencodable <c>string</c> — a
    /// C# UTF-16 value containing an unpaired surrogate — raises an
    /// <see cref="System.Text.EncoderFallbackException"/> instead of the default
    /// <see cref="System.Text.Encoding.UTF8"/> behaviour of silently substituting
    /// <c>U+FFFD</c>. Silent replacement would violate MESSAGE_SPEC §8 ("no silent
    /// replacement, ever"); C# <c>string</c> is a Unicode type, so it is always
    /// strict (CORELIB_PLAN §6.4). Valid strings encode to exactly the same bytes
    /// as the default UTF-8 encoder.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Create an encoder over <paramref name="buffer"/> with no flush sink.
    /// Writing past the end of the buffer raises <see cref="SofabError.BufferFull"/>.
    /// </summary>
    /// <param name="buffer">caller-owned output buffer (any length, incl. 0)</param>
    public OStream(byte[] buffer)
        : this(buffer, 0, null)
    {
    }

    /// <summary>
    /// Like <see cref="OStream(byte[])"/> but begin writing at <paramref name="offset"/>
    /// bytes into the buffer, reserving room for a lower-layer header.
    /// </summary>
    /// <param name="buffer">caller-owned output buffer</param>
    /// <param name="offset">initial write position (<c>0..buffer.Length</c>)</param>
    public OStream(byte[] buffer, int offset)
        : this(buffer, offset, null)
    {
    }

    /// <summary>
    /// Create an encoder with a flush <paramref name="sink"/>. When the buffer
    /// fills, the accumulated bytes are passed to <paramref name="sink"/> and
    /// writing resumes at the start of the buffer.
    /// </summary>
    /// <remarks>
    /// With a sink the buffer is a <i>streaming</i> buffer and must satisfy
    /// <c>buffer.Length - offset &gt;= <see cref="Sofab.MinOutputBuffer"/></c>;
    /// it is rejected here, where it is handed over, rather than partway through
    /// a message. Without a sink no flush can occur and no minimum applies
    /// (CORELIB_PLAN §5.1).
    /// </remarks>
    /// <param name="buffer">caller-owned output buffer</param>
    /// <param name="offset">initial write position (<c>0..buffer.Length</c>)</param>
    /// <param name="sink">flush sink, or <c>null</c> for none</param>
    public OStream(byte[] buffer, int offset, FlushSink? sink)
    {
        CheckBuffer(buffer, offset, sink != null);
        _buffer = buffer;
        _end = buffer.Length;
        _offset = offset;
        _sink = sink;
    }

    /// <summary>Number of bytes written to the active buffer since the last flush.</summary>
    public int BytesUsed => _offset;

    /// <summary>
    /// Flush any pending bytes to the sink (if one is set) and report how many
    /// bytes were pending. With no sink the buffer is left intact.
    /// </summary>
    /// <returns>number of bytes that were pending</returns>
    public int Flush()
    {
        int used = _offset;
        if (used > 0 && _sink != null)
        {
            FlushPending();
        }
        return used;
    }

    /// <summary>
    /// Replace the active buffer (typically from within a flush sink), resuming
    /// writes at <paramref name="offset"/> in the new buffer.
    /// </summary>
    /// <remarks>
    /// The start offset belongs to the installation, not to the buffer
    /// (CORELIB_PLAN §5.1): the cursor starts at <paramref name="offset"/> and
    /// the offset is consumed there, so a later flush the sink returns from
    /// without installing anything resumes at 0. Handing the <i>same</i> buffer
    /// back is an installation like any other, which is how a sink re-arms its
    /// framing-header reservation for every flushed packet.
    /// <para>
    /// An encoder that has a sink installed is streaming, so the replacement is
    /// held to <see cref="Sofab.MinOutputBuffer"/> exactly as the constructor's
    /// buffer was — <c>buffer.Length - offset</c> must reach it, and a buffer
    /// that does not is rejected here rather than partway through the message.
    /// On a sinkless encoder no minimum applies (CORELIB_PLAN §5.1).
    /// </para>
    /// </remarks>
    /// <param name="buffer">new caller-owned output buffer</param>
    /// <param name="offset">initial write position (<c>0..buffer.Length</c>)</param>
    public void BufferSet(byte[] buffer, int offset)
    {
        CheckBuffer(buffer, offset, _sink != null);
        _buffer = buffer;
        _end = buffer.Length;
        _offset = offset;
    }

    /// <summary>
    /// Validate a buffer at the point it is handed over: non-null, an in-range
    /// start offset, and — for a buffer installed <b>with</b> a flush sink — at
    /// least <see cref="Sofab.MinOutputBuffer"/> writable bytes beyond that
    /// offset. A sinkless buffer has no minimum: no flush can occur, so a
    /// message that fits is encoded and one that does not reports
    /// <see cref="SofabError.BufferFull"/> (CORELIB_PLAN §5.1).
    /// </summary>
    /// <param name="buffer">the buffer being installed</param>
    /// <param name="offset">its start offset</param>
    /// <param name="streaming">whether a flush sink is attached</param>
    private static void CheckBuffer(byte[] buffer, int offset, bool streaming)
    {
        if (buffer == null)
        {
            throw new ArgumentException("buffer must not be null", nameof(buffer));
        }
        if (offset < 0 || offset > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "offset out of range");
        }
        if (streaming && buffer.Length - offset < Sofab.MinOutputBuffer)
        {
            throw new ArgumentOutOfRangeException(
                nameof(buffer),
                $"a streaming buffer needs at least {Sofab.MinOutputBuffer} byte(s) past its offset");
        }
    }

    // --- primitives ---------------------------------------------------------

    /// <summary>
    /// Append one byte to the active buffer. When the buffer is full, flush it to
    /// the sink and resume where the returning callback left the cursor; with no
    /// sink this raises <see cref="SofabError.BufferFull"/>.
    /// </summary>
    /// <param name="b">byte value (low 8 bits used)</param>
    private void PushByte(int b)
    {
        if (_offset >= _end)
        {
            if (_sink == null)
            {
                throw new SofabException(SofabError.BufferFull);
            }
            FlushPending();
        }
        _buffer[_offset++] = (byte)b;
    }

    /// <summary>
    /// Hand the pending bytes to the sink (which must be set) and leave the cursor
    /// where the returning callback wants writing to resume.
    /// </summary>
    /// <remarks>
    /// CORELIB_PLAN §5.1: the cursor is dropped to 0 <i>before</i> the callback
    /// runs, so a sink that copies and returns resumes at the start of the still
    /// active buffer, while a sink that takes the buffer and installs a
    /// replacement with <see cref="BufferSet"/> keeps that installation's start
    /// offset — its reserved header room survives the flush that created it.
    /// Zeroing after the callback instead would silently discard the reservation.
    /// </remarks>
    private void FlushPending()
    {
        int used = _offset;
        _offset = 0;
        _sink!(_buffer, 0, used);
    }

    /// <summary>Append <paramref name="len"/> raw bytes from <paramref name="data"/>, flushing as needed.</summary>
    /// <remarks>
    /// Copies in bulk up to each buffer boundary instead of byte-by-byte, so a
    /// large payload streams out in a handful of <see cref="Array.Copy(Array, int, Array, int, int)"/> calls.
    /// </remarks>
    /// <param name="data">source array</param>
    /// <param name="from">start offset within <paramref name="data"/></param>
    /// <param name="len">number of bytes to append</param>
    private void PushRaw(byte[] data, int from, int len)
    {
        int src = from;
        int remaining = len;
        while (remaining > 0)
        {
            if (_offset >= _end)
            {
                if (_sink == null)
                {
                    throw new SofabException(SofabError.BufferFull);
                }
                FlushPending();
            }
            int n = Math.Min(_end - _offset, remaining);
            Array.Copy(data, src, _buffer, _offset, n);
            _offset += n;
            src += n;
            remaining -= n;
        }
    }

    /// <summary>Append a value as a base-128 LEB128 varint (7 bits per byte, low bytes first).</summary>
    /// <remarks>
    /// Fast path: a varint is at most 10 bytes. When that much room is
    /// guaranteed, advance a local cursor over the buffer with no per-byte
    /// bounds or flush check; single-byte values (field headers, small scalars)
    /// are by far the most common and skip the loop entirely.
    /// </remarks>
    /// <param name="value">the unsigned value to encode</param>
    private void WriteVarint(ulong value)
    {
        int p = _offset;
        if (_end - p >= MaxVarintBytes)
        {
            ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
            if (value < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)value;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, value);
            }
            _offset = p;
            return;
        }
        WriteVarintSlow(value);
    }

    /// <summary>
    /// Write a varint needing two or more bytes at <paramref name="p"/>, knowing
    /// the buffer holds at least <see cref="MaxVarintBytes"/> bytes there, and
    /// return the position just past it.
    /// </summary>
    /// <remarks>
    /// The single-byte case — every small field header and small scalar, the case
    /// the format is designed around (CORELIB_PLAN §1) — is deliberately *not*
    /// handled here. Each caller tests <c>value &lt; 0x80</c> and does that store
    /// itself, so the common field costs a compare and a store with no call at
    /// all; only a genuinely multi-byte value reaches this method. (The test was a
    /// shared <c>AggressiveInlining</c> helper at first; measured under Callgrind
    /// the JIT declined to inline it — a helper containing a call is not inlined
    /// here even when marked — which cost ~20 instructions per varint written.)
    /// <para>
    /// Unrolled rather than looped, for the same reason the decoder's reader is:
    /// the shift is then an immediate and there is no counter to maintain or test.
    /// </para>
    /// <para>
    /// The caller's single capacity check stands in for both the flush check and
    /// the array bounds check a byte-at-a-time writer would pay per byte —
    /// <c>_end</c> is always the buffer's length, so <c>p + 10 &lt;= _end</c> is
    /// exactly the proof that ten stores from <paramref name="p"/> stay in range.
    /// </para>
    /// </remarks>
    /// <param name="b">reference to the output buffer's first byte</param>
    /// <param name="p">write position</param>
    /// <param name="value">the unsigned value to encode (at least <c>0x80</c>)</param>
    private static int WriteVarintAtMulti(ref byte b, int p, ulong value)
    {
        ref byte d = ref Unsafe.Add(ref b, (nint)(uint)p);
        if (value < 0x4000)
        {
            // Two bytes: the next most common width after one, and cheaper as a
            // pair of stores than as a word to assemble.
            d = (byte)(value | 0x80);
            Unsafe.Add(ref d, 1) = (byte)(value >> 7);
            return p + 2;
        }
        if (value < 1UL << 56)
        {
            // Three to eight bytes: build the whole encoding in a register and
            // store it in one go. The caller guaranteed ten bytes of room, so
            // writing a full eight is always in range whatever the length is.
            int n = ((VALUE_BITS - BitOperations.LeadingZeroCount(value)) + 6) / 7;
            ulong x = ScatterPayload(value) | (ContinuationBits & ((1UL << ((n - 1) << 3)) - 1));
            if (!BitConverter.IsLittleEndian)
            {
                x = BinaryPrimitives.ReverseEndianness(x);
            }
            Unsafe.WriteUnaligned(ref d, x);
            return p + n;
        }

        // Nine or ten bytes: the first eight all continue, and what is left of the
        // value past bit 55 needs one byte, or two when it reaches bit 63.
        ulong head = ScatterPayload(value) | ContinuationBits;
        if (!BitConverter.IsLittleEndian)
        {
            head = BinaryPrimitives.ReverseEndianness(head);
        }
        Unsafe.WriteUnaligned(ref d, head);
        ulong tail = value >> 56;
        if (tail < 0x80)
        {
            Unsafe.Add(ref d, 8) = (byte)tail;
            return p + 9;
        }
        Unsafe.Add(ref d, 8) = (byte)(tail | 0x80);
        Unsafe.Add(ref d, 9) = (byte)(tail >> 7);
        return p + MaxVarintBytes;
    }

    /// <summary>
    /// Spread the low 56 bits of <paramref name="value"/> into eight bytes, seven
    /// payload bits per byte, leaving each continuation flag clear.
    /// </summary>
    /// <remarks>
    /// The exact inverse of the decoder's gather (IStream): three shift-and-merge
    /// steps open the one-bit gap each byte needs, splitting the value into 28-bit
    /// halves, then 14-bit quarters, then the eight 7-bit groups. Bits above 55
    /// are dropped, which is what lets the caller handle a nine- or ten-byte
    /// encoding by writing this word and then the remaining byte or two.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ScatterPayload(ulong value)
    {
        ulong x = ((value & 0x00FFFFFFF0000000UL) << 4) | (value & 0x000000000FFFFFFFUL);
        x = ((x & 0x0FFFC0000FFFC000UL) << 2) | (x & 0x00003FFF00003FFFUL);
        return ((x & 0x3F803F803F803F80UL) << 1) | (x & 0x007F007F007F007FUL);
    }

    /// <summary>Buffer-spanning varint write: flushes mid-value when the buffer is tiny.</summary>
    /// <param name="value">the unsigned value to encode</param>
    private void WriteVarintSlow(ulong value)
    {
        do
        {
            int b = (int)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }
            PushByte(b);
        }
        while (value != 0);
    }

    /// <summary>
    /// Write a field-header varint packing the field id and 3-bit wire type as
    /// <c>(id &lt;&lt; 3) | wireType</c>.
    /// </summary>
    /// <param name="id">field id (<c>0..ID_MAX</c>)</param>
    /// <param name="wireType">3-bit wire-type tag (one of the <c>T_*</c> constants)</param>
    /// <remarks>
    /// This is the single choke point every field write in this class passes
    /// through — scalar, fixlen, float, both array kinds, blob and string all
    /// reach the wire via it — so it is also where a held-back sequence run is
    /// committed: the field about to be written is content, which means every
    /// enclosing sequence is non-default and must be framed after all
    /// (MESSAGE_SPEC §2).
    /// </remarks>
    /// <exception cref="SofabException">
    /// with <see cref="SofabError.Argument"/> if <paramref name="id"/> is out of range
    /// </exception>
    private void WriteIdType(int id, int wireType)
    {
        if (id < 0)
        {
            ThrowBadId(id);
        }
        if (_nPending != 0 && wireType != T_SEQUENCE_START && wireType != T_SEQUENCE_END)
        {
            CommitPending();
        }
        WriteVarint(((ulong)(uint)id << 3) | (uint)wireType);
    }


    /// <summary>Raise <see cref="SofabError.Argument"/> for an out-of-range field id.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowBadId(int id) =>
        throw new SofabException(SofabError.Argument, "id " + id);

    /// <summary>Write out the held-back sequence headers, outermost first.</summary>
    /// <remarks>
    /// Runs at most once per non-default sequence, never once per field, so it is
    /// kept off the inlined fast path of <see cref="WriteIdType"/>.
    /// </remarks>
    private void CommitPending()
    {
        int n = _nPending;
        _nPending = 0;
        for (int i = 0; i < n; i++)
        {
            int id = i < InlinePendingCapacity ? _inlinePending[i] : _pending![i - InlinePendingCapacity];
            WriteVarint(((ulong)(uint)id << 3) | T_SEQUENCE_START);
        }
    }

    // --- scalar writers -----------------------------------------------------

    /// <summary>
    /// Write an unsigned-integer field.
    /// </summary>
    /// <param name="id">field id (<c>0..ID_MAX</c>)</param>
    /// <param name="value">unsigned value</param>
    public void WriteUnsigned(int id, ulong value)
    {
        if (id < 0)
        {
            ThrowBadId(id);
        }
        if (_nPending != 0)
        {
            CommitPending();
        }
        ulong header = ((ulong)(uint)id << 3) | T_VARINT_UNSIGNED;
        int p = _offset;
        if (_end - p >= 2 * MaxVarintBytes)
        {
            ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
            if (header < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)header;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, header);
            }
            ulong v = value;
            if (v < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)v;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, v);
            }
            _offset = p;
            return;
        }
        WriteVarint(header);
        WriteVarint(value);
    }

    /// <summary>Write a signed-integer field (ZigZag + varint).</summary>
    /// <param name="id">field id</param>
    /// <param name="value">signed value</param>
    public void WriteSigned(int id, long value)
    {
        if (id < 0)
        {
            ThrowBadId(id);
        }
        if (_nPending != 0)
        {
            CommitPending();
        }
        ulong header = ((ulong)(uint)id << 3) | T_VARINT_SIGNED;
        int p = _offset;
        if (_end - p >= 2 * MaxVarintBytes)
        {
            ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
            if (header < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)header;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, header);
            }
            ulong v = ZigzagEncode(value);
            if (v < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)v;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, v);
            }
            _offset = p;
            return;
        }
        WriteVarint(header);
        WriteVarint(ZigzagEncode(value));
    }


    /// <summary>Write a boolean as an unsigned <c>0</c> / <c>1</c>.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">boolean value</param>
    public void WriteBoolean(int id, bool value)
    {
        WriteUnsigned(id, value ? 1UL : 0UL);
    }

    // --- fixed-length writers ----------------------------------------------

    /// <summary>
    /// Write a fixed-length field: the id header, a <c>(len &lt;&lt; 3) | subtype</c>
    /// length header, then <paramref name="length"/> raw bytes from
    /// <paramref name="data"/> (already in wire / little-endian order for floats).
    /// </summary>
    /// <param name="id">field id</param>
    /// <param name="data">payload bytes (may be <c>null</c> only if <paramref name="length"/> is 0)</param>
    /// <param name="from">start offset within <paramref name="data"/></param>
    /// <param name="length">number of payload bytes</param>
    /// <param name="subtype">fixed-length sub-type</param>
    /// <exception cref="SofabException">
    /// <see cref="SofabError.Argument"/> when <paramref name="subtype"/> is not one of the
    /// four defined tags (0x4–0x7 are reserved) or when it is
    /// <see cref="FixlenType.Fp32"/> / <see cref="FixlenType.Fp64"/> and
    /// <paramref name="length"/> is not exactly 4 / 8. Both make a malformed
    /// <c>fixlen_word</c> (CORELIB_PLAN §4.6), so the encoder rejects them rather than
    /// emit bytes its own decoder reports as <c>INVALID</c>.
    /// </exception>
    public void WriteFixlen(int id, byte[] data, int from, int length, FixlenType subtype)
    {
        if (length < 0)
        {
            throw new SofabException(SofabError.Argument, "length " + length);
        }
        if ((uint)subtype > (uint)FixlenType.Blob)
        {
            throw new SofabException(SofabError.Argument, "fixlen subtype " + (int)subtype);
        }
        if ((subtype == FixlenType.Fp32 && length != 4) || (subtype == FixlenType.Fp64 && length != 8))
        {
            throw new SofabException(
                SofabError.Argument, "fixlen length " + length + " for " + subtype);
        }
        if (id < 0)
        {
            ThrowBadId(id);
        }
        if (_nPending != 0)
        {
            CommitPending();
        }
        ulong header = ((ulong)(uint)id << 3) | T_FIXLEN;
        ulong word = ((ulong)(uint)length << 3) | (uint)subtype.Raw();
        int p = _offset;
        if (_end - p >= 2 * MaxVarintBytes)
        {
            ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
            if (header < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)header;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, header);
            }
            if (word < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)word;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, word);
            }
            _offset = p;
        }
        else
        {
            WriteVarint(header);
            WriteVarint(word);
        }
        PushRaw(data, from, length);
    }

    /// <summary>Write a 32-bit float field.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">value</param>
    public void WriteFp32(int id, float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        if (id < 0)
        {
            ThrowBadId(id);
        }
        if (_nPending != 0)
        {
            CommitPending();
        }
        ulong header = ((ulong)(uint)id << 3) | T_FIXLEN;
        int p = _offset;
        if (_end - p >= MaxVarintBytes + 1 + 4)
        {
            // The fixlen_word for an fp32 is always the single byte (4 << 3) | 0.
            ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
            if (header < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)header;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, header);
            }
            Unsafe.Add(ref b, (nint)(uint)p) = (4 << 3) | (byte)FixlenType.Fp32;
            BinaryPrimitives.WriteInt32LittleEndian(
                MemoryMarshal.CreateSpan(ref Unsafe.Add(ref b, (nint)(uint)(p + 1)), 4), bits);
            _offset = p + 5;
            return;
        }
        WriteVarint(header);
        WriteVarint((4UL << 3) | (uint)FixlenType.Fp32.Raw());
        PutLe32(bits);
    }

    /// <summary>Write four little-endian bytes, fast when the buffer has room.</summary>
    private void PutLe32(int bits)
    {
        int p = _offset;
        if (_end - p >= 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(p, 4), bits);
            _offset = p + 4;
            return;
        }
        PushByte(bits & 0xFF);
        PushByte((bits >> 8) & 0xFF);
        PushByte((bits >> 16) & 0xFF);
        PushByte((int)((uint)bits >> 24) & 0xFF);
    }

    /// <summary>Write eight little-endian bytes, fast when the buffer has room.</summary>
    private void PutLe64(long bits)
    {
        int p = _offset;
        if (_end - p >= 8)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(p, 8), bits);
            _offset = p + 8;
            return;
        }
        for (int i = 0; i < 8; i++)
        {
            PushByte((int)((ulong)bits >> (i * 8)) & 0xFF);
        }
    }

    /// <summary>Write a 64-bit float field.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">value</param>
    public void WriteFp64(int id, double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        if (id < 0)
        {
            ThrowBadId(id);
        }
        if (_nPending != 0)
        {
            CommitPending();
        }
        ulong header = ((ulong)(uint)id << 3) | T_FIXLEN;
        int p = _offset;
        if (_end - p >= MaxVarintBytes + 1 + 8)
        {
            // The fixlen_word for an fp64 is always the single byte (8 << 3) | 1.
            ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
            if (header < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)header;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, header);
            }
            Unsafe.Add(ref b, (nint)(uint)p) = (8 << 3) | (byte)FixlenType.Fp64;
            BinaryPrimitives.WriteInt64LittleEndian(
                MemoryMarshal.CreateSpan(ref Unsafe.Add(ref b, (nint)(uint)(p + 1)), 8), bits);
            _offset = p + 9;
            return;
        }
        WriteVarint(header);
        WriteVarint((8UL << 3) | (uint)FixlenType.Fp64.Raw());
        PutLe64(bits);
    }

    /// <summary>Write a string field (raw UTF-8 bytes, no NUL on the wire).</summary>
    /// <param name="id">field id</param>
    /// <param name="text">string value (must be encodable as valid UTF-8)</param>
    /// <exception cref="SofabException">
    /// with <see cref="SofabError.Argument"/> if <paramref name="text"/> cannot be
    /// encoded as valid UTF-8 (i.e. it contains an unpaired surrogate). Per
    /// MESSAGE_SPEC §8 the value is refused, never silently rewritten to
    /// <c>U+FFFD</c>. Embedded <c>U+0000</c> is valid UTF-8 and is written verbatim.
    /// </exception>
    public void WriteString(int id, string text)
    {
        // Encode UTF-8 straight into the output buffer instead of allocating an
        // intermediate byte[] per call: measure once (vectorized), then let the
        // runtime encoder write in place when the buffer has room. The strict
        // codec throws on an unpaired surrogate rather than emitting U+FFFD, so an
        // invalid string is rejected up front — before any header is written and
        // before any held-back sequence header is committed (§6.4: the refusal
        // leaves the stream exactly as it found it).
        if (text == null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        // ASCII fast path. Every char below U+0080 encodes to itself as one byte,
        // so the payload length is known up front (the char count) and no
        // surrogate can be involved — the two facts the general path pays
        // Encoding.GetByteCount and Encoding.GetBytes to establish. The
        // ASCII-ness question is settled by a vectorized Ascii.IsValid BEFORE
        // anything is committed, rather than discovered mid-transcode: committing
        // the held-back sequence headers first and only then meeting a char that
        // turns out to be an unpaired surrogate would leave the §6.4 refusal
        // non-atomic, framing an empty sequence MESSAGE_SPEC §2 says must be
        // omitted. Bounded by AsciiFastPathMaxChars because past that the
        // runtime's general transcoder wins over this measure-free in-place copy.
        if (text.Length <= AsciiFastPathMaxChars && Ascii.IsValid(text))
        {
            if (id < 0)
            {
                ThrowBadId(id);
            }
            if (_nPending != 0)
            {
                CommitPending();
            }
            int len = text.Length;
            int p = _offset;
            if (_end - p >= (2 * MaxVarintBytes) + len)
            {
                ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
                ulong header = ((ulong)(uint)id << 3) | T_FIXLEN;
                if (header < 0x80)
                {
                    Unsafe.Add(ref b, (nint)(uint)p) = (byte)header;
                    p++;
                }
                else
                {
                    p = WriteVarintAtMulti(ref b, p, header);
                }
                ulong word = ((ulong)(uint)len << 3) | (uint)FixlenType.String.Raw();
                if (word < 0x80)
                {
                    Unsafe.Add(ref b, (nint)(uint)p) = (byte)word;
                    p++;
                }
                else
                {
                    p = WriteVarintAtMulti(ref b, p, word);
                }
                // Every char is known ASCII, so this narrows the whole string in
                // one vectorized pass and cannot fail partway.
                Ascii.FromUtf16(
                    text,
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref b, (nint)(uint)p), len),
                    out _);
                _offset = p + len;
                return;
            }
        }

        int n;
        try
        {
            n = StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException e)
        {
            throw new SofabException(SofabError.Argument, "invalid UTF-8 string: " + e.Message);
        }
        WriteIdType(id, T_FIXLEN);
        WriteVarint(((ulong)n << 3) | (uint)FixlenType.String.Raw());
        if (_end - _offset >= n)
        {
            _offset += StrictUtf8.GetBytes(text, 0, text.Length, _buffer, _offset);
            return;
        }
        // Buffer-spanning write: transcode straight into the room the buffer has
        // left, flush, and carry on — never into a payload-sized temporary.
        PushTranscoded(text);
    }

    /// <summary>
    /// Transcode <paramref name="text"/> into the output buffer in as many
    /// pieces as the buffer's remaining room dictates, flushing in between.
    /// </summary>
    /// <remarks>
    /// CORELIB_PLAN §5.1 makes the payload run of a <c>string</c> <i>divisible</i>
    /// at any byte boundary, and states normatively that the output buffer bounds
    /// memory, not the message: a field with no schema <c>maxlen</c> can exceed
    /// any buffer. Materializing the transcoded payload in a temporary array —
    /// the only way a <c>string</c> would differ from a <c>blob</c>, which streams
    /// the caller's bytes — would hand that bound back to the message and put a
    /// payload-sized gen0 allocation on the hot path, triggered by nothing more
    /// than an unlucky buffer position.
    /// <para>
    /// A stateful <see cref="Encoder"/> is what makes the split legal: it carries
    /// a surrogate pair whose halves land on either side of a chunk boundary, so
    /// the pieces concatenate to exactly the one-shot bytes. It is created once
    /// per <see cref="OStream"/> and reset per call, so repeated large strings
    /// cost nothing after the first.
    /// </para>
    /// <para>
    /// Two chunk sizes: while the buffer has room for a whole rune the encoder
    /// writes in place, and once fewer than <see cref="MaxUtf8BytesPerRune"/>
    /// bytes are left it transcodes one rune into a stack scratch and pushes it
    /// byte-wise across the flush. That second path is what lets a UTF-8 sequence
    /// itself straddle a flush, which <c>MIN_OUTPUT_BUFFER == 1</c> requires:
    /// <see cref="Encoder"/>.<c>Convert</c> cannot emit a partial rune, so it is
    /// never handed a destination too small to hold one.
    /// </para>
    /// <para>
    /// The caller has already run <see cref="Encoding.GetByteCount(string)"/>
    /// over the whole value — that is where the <c>fixlen_word</c> length comes
    /// from — so the string is known encodable and the loop cannot fail partway
    /// on an unpaired surrogate. It can still hit a full buffer with no sink,
    /// which reports <see cref="SofabError.BufferFull"/> exactly like any other
    /// oversized payload.
    /// </para>
    /// </remarks>
    /// <param name="text">the (already validated) string value</param>
    private void PushTranscoded(string text)
    {
        Encoder encoder = _utf8Encoder ??= StrictUtf8.GetEncoder();
        encoder.Reset();

        Span<byte> rune = stackalloc byte[MaxUtf8BytesPerRune];
        int from = 0;
        int charsLeft = text.Length;
        while (charsLeft > 0)
        {
            if (_offset >= _end)
            {
                if (_sink == null)
                {
                    throw new SofabException(SofabError.BufferFull);
                }
                FlushPending();
            }

            int room = _end - _offset;
            int charsUsed, bytesUsed;
            if (room >= MaxUtf8BytesPerRune)
            {
                encoder.Convert(
                    text.AsSpan(from, charsLeft),
                    _buffer.AsSpan(_offset, room),
                    flush: false,
                    out charsUsed,
                    out bytesUsed,
                    out _);
                _offset += bytesUsed;
            }
            else
            {
                // Tail too short for a whole rune: encode one into the scratch
                // and let PushByte carry it over the flush boundary.
                encoder.Convert(
                    text.AsSpan(from, charsLeft),
                    rune,
                    flush: false,
                    out charsUsed,
                    out bytesUsed,
                    out _);
                for (int i = 0; i < bytesUsed; i++)
                {
                    PushByte(rune[i]);
                }
            }
            from += charsUsed;
            charsLeft -= charsUsed;
        }
    }

    /// <summary>Write a binary blob field.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">blob bytes</param>
    public void WriteBlob(int id, byte[] data)
    {
        WriteFixlen(id, data, 0, data.Length, FixlenType.Blob);
    }

    /// <summary>Write a slice of a byte array as a binary blob field.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">backing array</param>
    /// <param name="from">start offset</param>
    /// <param name="length">number of bytes</param>
    public void WriteBlob(int id, byte[] data, int from, int length)
    {
        WriteFixlen(id, data, from, length, FixlenType.Blob);
    }

    // --- array writers ------------------------------------------------------

    /// <summary>
    /// Write an array field's id header followed by its element <paramref name="count"/>.
    /// </summary>
    /// <param name="id">field id</param>
    /// <param name="wireType">array wire-type tag (<c>T_VARINTARRAY_*</c> / <c>T_FIXLENARRAY</c>)</param>
    /// <param name="count">number of elements (<c>0</c> is valid: a zero-count array is
    /// exactly <c>[ header ][ count=0 ]</c> with no elements, per §4.7)</param>
    private void WriteArrayHeader(int id, int wireType, int count)
    {
        if (id < 0)
        {
            ThrowBadId(id);
        }
        if (_nPending != 0)
        {
            CommitPending();
        }
        ulong header = ((ulong)(uint)id << 3) | (uint)wireType;
        int p = _offset;
        if (_end - p >= 2 * MaxVarintBytes)
        {
            ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
            if (header < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)header;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, header);
            }
            ulong countWord = (uint)count;
            if (countWord < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)countWord;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, countWord);
            }
            _offset = p;
            return;
        }
        WriteVarint(header);
        WriteVarint((uint)count);
    }

    /// <summary>Write an array of unsigned 8-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArrayUnsigned(int id, byte[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_UNSIGNED, data.Length);
        int p = _offset;
        int e = _end;
        ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
        // One capacity check for the whole run when the buffer can hold the worst
        // case (10 bytes per element): the element loop is then pure encoding.
        if ((long)(e - p) >= (long)data.Length * MaxVarintBytes)
        {
            foreach (byte elem in data)
            {
                if (elem < 0x80)
                {
                    Unsafe.Add(ref b, (nint)(uint)p) = (byte)elem;
                    p++;
                }
                else
                {
                    p = WriteVarintAtMulti(ref b, p, elem);
                }
            }
            _offset = p;
            return;
        }
        foreach (byte elem in data)
        {
            ulong v = elem;
            if (e - p < MaxVarintBytes)
            {
                _offset = p;
                WriteVarintSlow(v);
                b = ref MemoryMarshal.GetArrayDataReference(_buffer);
                p = _offset;
                e = _end;
                continue;
            }
            if (v < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)v;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, v);
            }
        }
        _offset = p;
    }

    /// <summary>Write an array of unsigned 16-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArrayUnsigned(int id, ushort[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_UNSIGNED, data.Length);
        int p = _offset;
        int e = _end;
        ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
        // One capacity check for the whole run when the buffer can hold the worst
        // case (10 bytes per element): the element loop is then pure encoding.
        if ((long)(e - p) >= (long)data.Length * MaxVarintBytes)
        {
            foreach (ushort elem in data)
            {
                if (elem < 0x80)
                {
                    Unsafe.Add(ref b, (nint)(uint)p) = (byte)elem;
                    p++;
                }
                else
                {
                    p = WriteVarintAtMulti(ref b, p, elem);
                }
            }
            _offset = p;
            return;
        }
        foreach (ushort elem in data)
        {
            ulong v = elem;
            if (e - p < MaxVarintBytes)
            {
                _offset = p;
                WriteVarintSlow(v);
                b = ref MemoryMarshal.GetArrayDataReference(_buffer);
                p = _offset;
                e = _end;
                continue;
            }
            if (v < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)v;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, v);
            }
        }
        _offset = p;
    }

    /// <summary>Write an array of unsigned 32-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArrayUnsigned(int id, uint[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_UNSIGNED, data.Length);
        int p = _offset;
        int e = _end;
        ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
        // One capacity check for the whole run when the buffer can hold the worst
        // case (10 bytes per element): the element loop is then pure encoding.
        if ((long)(e - p) >= (long)data.Length * MaxVarintBytes)
        {
            foreach (uint elem in data)
            {
                if (elem < 0x80)
                {
                    Unsafe.Add(ref b, (nint)(uint)p) = (byte)elem;
                    p++;
                }
                else
                {
                    p = WriteVarintAtMulti(ref b, p, elem);
                }
            }
            _offset = p;
            return;
        }
        foreach (uint elem in data)
        {
            ulong v = elem;
            if (e - p < MaxVarintBytes)
            {
                _offset = p;
                WriteVarintSlow(v);
                b = ref MemoryMarshal.GetArrayDataReference(_buffer);
                p = _offset;
                e = _end;
                continue;
            }
            if (v < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)v;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, v);
            }
        }
        _offset = p;
    }

    /// <summary>Write an array of unsigned 64-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArrayUnsigned(int id, ulong[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_UNSIGNED, data.Length);
        int p = _offset;
        int e = _end;
        ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
        // One capacity check for the whole run when the buffer can hold the worst
        // case (10 bytes per element): the element loop is then pure encoding.
        if ((long)(e - p) >= (long)data.Length * MaxVarintBytes)
        {
            foreach (ulong elem in data)
            {
                if (elem < 0x80)
                {
                    Unsafe.Add(ref b, (nint)(uint)p) = (byte)elem;
                    p++;
                }
                else
                {
                    p = WriteVarintAtMulti(ref b, p, elem);
                }
            }
            _offset = p;
            return;
        }
        foreach (ulong elem in data)
        {
            ulong v = elem;
            if (e - p < MaxVarintBytes)
            {
                _offset = p;
                WriteVarintSlow(v);
                b = ref MemoryMarshal.GetArrayDataReference(_buffer);
                p = _offset;
                e = _end;
                continue;
            }
            if (v < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)v;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, v);
            }
        }
        _offset = p;
    }

    /// <summary>Write an array of signed 8-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArraySigned(int id, sbyte[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_SIGNED, data.Length);
        int p = _offset;
        int e = _end;
        ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
        // One capacity check for the whole run when the buffer can hold the worst
        // case (10 bytes per element): the element loop is then pure encoding.
        if ((long)(e - p) >= (long)data.Length * MaxVarintBytes)
        {
            foreach (sbyte elem in data)
            {
                ulong zz = ZigzagEncode(elem);
                if (zz < 0x80)
                {
                    Unsafe.Add(ref b, (nint)(uint)p) = (byte)zz;
                    p++;
                }
                else
                {
                    p = WriteVarintAtMulti(ref b, p, zz);
                }
            }
            _offset = p;
            return;
        }
        foreach (sbyte elem in data)
        {
            ulong v = ZigzagEncode(elem);
            if (e - p < MaxVarintBytes)
            {
                _offset = p;
                WriteVarintSlow(v);
                b = ref MemoryMarshal.GetArrayDataReference(_buffer);
                p = _offset;
                e = _end;
                continue;
            }
            if (v < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)v;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, v);
            }
        }
        _offset = p;
    }

    /// <summary>Write an array of signed 16-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArraySigned(int id, short[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_SIGNED, data.Length);
        int p = _offset;
        int e = _end;
        ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
        // One capacity check for the whole run when the buffer can hold the worst
        // case (10 bytes per element): the element loop is then pure encoding.
        if ((long)(e - p) >= (long)data.Length * MaxVarintBytes)
        {
            foreach (short elem in data)
            {
                ulong zz = ZigzagEncode(elem);
                if (zz < 0x80)
                {
                    Unsafe.Add(ref b, (nint)(uint)p) = (byte)zz;
                    p++;
                }
                else
                {
                    p = WriteVarintAtMulti(ref b, p, zz);
                }
            }
            _offset = p;
            return;
        }
        foreach (short elem in data)
        {
            ulong v = ZigzagEncode(elem);
            if (e - p < MaxVarintBytes)
            {
                _offset = p;
                WriteVarintSlow(v);
                b = ref MemoryMarshal.GetArrayDataReference(_buffer);
                p = _offset;
                e = _end;
                continue;
            }
            if (v < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)v;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, v);
            }
        }
        _offset = p;
    }

    /// <summary>Write an array of signed 32-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArraySigned(int id, int[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_SIGNED, data.Length);
        int p = _offset;
        int e = _end;
        ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
        // One capacity check for the whole run when the buffer can hold the worst
        // case (10 bytes per element): the element loop is then pure encoding.
        if ((long)(e - p) >= (long)data.Length * MaxVarintBytes)
        {
            foreach (int elem in data)
            {
                ulong zz = ZigzagEncode(elem);
                if (zz < 0x80)
                {
                    Unsafe.Add(ref b, (nint)(uint)p) = (byte)zz;
                    p++;
                }
                else
                {
                    p = WriteVarintAtMulti(ref b, p, zz);
                }
            }
            _offset = p;
            return;
        }
        foreach (int elem in data)
        {
            ulong v = ZigzagEncode(elem);
            if (e - p < MaxVarintBytes)
            {
                _offset = p;
                WriteVarintSlow(v);
                b = ref MemoryMarshal.GetArrayDataReference(_buffer);
                p = _offset;
                e = _end;
                continue;
            }
            if (v < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)v;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, v);
            }
        }
        _offset = p;
    }

    /// <summary>Write an array of signed 64-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArraySigned(int id, long[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_SIGNED, data.Length);
        int p = _offset;
        int e = _end;
        ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
        // One capacity check for the whole run when the buffer can hold the worst
        // case (10 bytes per element): the element loop is then pure encoding.
        if ((long)(e - p) >= (long)data.Length * MaxVarintBytes)
        {
            foreach (long elem in data)
            {
                ulong zz = ZigzagEncode(elem);
                if (zz < 0x80)
                {
                    Unsafe.Add(ref b, (nint)(uint)p) = (byte)zz;
                    p++;
                }
                else
                {
                    p = WriteVarintAtMulti(ref b, p, zz);
                }
            }
            _offset = p;
            return;
        }
        foreach (long elem in data)
        {
            ulong v = ZigzagEncode(elem);
            if (e - p < MaxVarintBytes)
            {
                _offset = p;
                WriteVarintSlow(v);
                b = ref MemoryMarshal.GetArrayDataReference(_buffer);
                p = _offset;
                e = _end;
                continue;
            }
            if (v < 0x80)
            {
                Unsafe.Add(ref b, (nint)(uint)p) = (byte)v;
                p++;
            }
            else
            {
                p = WriteVarintAtMulti(ref b, p, v);
            }
        }
        _offset = p;
    }

    /// <summary>Write an array of 32-bit floats.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArrayFp32(int id, float[] data)
    {
        WriteIdType(id, T_FIXLENARRAY);
        WriteVarint((uint)data.Length);
        // A fixlen array always carries its fixlen_word, even when empty (§4.8),
        // so an empty fp32 array is distinguishable from an empty fp64 array.
        WriteVarint((4UL << 3) | (uint)FixlenType.Fp32.Raw());
        int p = _offset;
        if ((long)(_end - p) >= (long)data.Length * 4)
        {
            // Room for every element: write the payload with no per-element
            // capacity check and no per-element span construction.
            ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
            foreach (float v in data)
            {
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref b, (nint)(uint)p),
                    BitConverter.IsLittleEndian
                        ? BitConverter.SingleToInt32Bits(v)
                        : BinaryPrimitives.ReverseEndianness(BitConverter.SingleToInt32Bits(v)));
                p += 4;
            }
            _offset = p;
            return;
        }
        foreach (float v in data)
        {
            PutLe32(BitConverter.SingleToInt32Bits(v));
        }
    }

    /// <summary>Write an array of 64-bit floats.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArrayFp64(int id, double[] data)
    {
        WriteIdType(id, T_FIXLENARRAY);
        WriteVarint((uint)data.Length);
        // A fixlen array always carries its fixlen_word, even when empty (§4.8),
        // so an empty fp64 array is distinguishable from an empty fp32 array.
        WriteVarint((8UL << 3) | (uint)FixlenType.Fp64.Raw());
        int p = _offset;
        if ((long)(_end - p) >= (long)data.Length * 8)
        {
            // Room for every element: write the payload with no per-element
            // capacity check and no per-element span construction.
            ref byte b = ref MemoryMarshal.GetArrayDataReference(_buffer);
            foreach (double v in data)
            {
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref b, (nint)(uint)p),
                    BitConverter.IsLittleEndian
                        ? BitConverter.DoubleToInt64Bits(v)
                        : BinaryPrimitives.ReverseEndianness(BitConverter.DoubleToInt64Bits(v)));
                p += 8;
            }
            _offset = p;
            return;
        }
        foreach (double v in data)
        {
            PutLe64(BitConverter.DoubleToInt64Bits(v));
        }
    }

    // --- sequence writers ---------------------------------------------------

    /// <summary>
    /// Open a nested sequence whose header is <b>held back</b> until the sequence
    /// turns out to have content. Fields written until the matching
    /// <see cref="WriteSequenceEnd"/> / <see cref="WriteSequenceEndKeep"/> belong
    /// to the sequence and form a fresh id scope.
    /// </summary>
    /// <remarks>
    /// MESSAGE_SPEC §2 omits a sequence-typed field whose value equals its declared
    /// default, and "not one child was written" is exactly that condition —
    /// evaluated per child field, recursively, for free. A sequence closed with
    /// nothing in it therefore emits <b>nothing</b> instead of a two-byte empty
    /// frame, and an all-default message becomes the empty byte string.
    /// <para>
    /// The predicate never touches a byte image of the object, so struct padding
    /// cannot influence it and a non-zero nested default is handled by the caller's
    /// ordinary per-field test.
    /// </para>
    /// <para>
    /// Held-back ids are encoder <i>state</i>, not buffer content, so a flush can
    /// never split a pending run: an output buffer far smaller than the message
    /// produces exactly the one-shot bytes.
    /// </para>
    /// <para>
    /// The hold-back reaches the full <c>MAX_DEPTH</c> (255): the pending run grows
    /// on demand, so this encoder emits the canonical §2 bytes at <i>every</i>
    /// nesting depth the format allows. CORELIB_PLAN §6 permits a bounded run —
    /// framing eagerly and non-canonically past the bound — only for a heap-free
    /// profile, which C# is not.
    /// </para>
    /// <para>
    /// This is the only way to open a sequence. How it closes decides whether a
    /// contentless one survives: <see cref="WriteSequenceEnd"/> drops it,
    /// <see cref="WriteSequenceEndKeep"/> forces the frame out.
    /// </para>
    /// </remarks>
    /// <param name="id">field id of the sequence</param>
    /// <exception cref="SofabException">
    /// with <see cref="SofabError.Argument"/> if opening this sequence would nest
    /// deeper than <c>MAX_DEPTH</c> (255) levels, or if <paramref name="id"/> is
    /// out of range
    /// </exception>
    public void WriteSequenceBeginLazy(int id)
    {
        if (_depth >= MAX_DEPTH)
        {
            throw new SofabException(SofabError.Argument, "sequence too deep");
        }
        if (id < 0 || id > ID_MAX)
        {
            throw new SofabException(SofabError.Argument, "id " + id);
        }
        // Grow on demand — the run reaches as deep as the nesting does, so there is
        // no depth at which a sequence gets framed eagerly and no fallback path
        // that could break "pending is a contiguous suffix of the open sequences".
        // MAX_DEPTH above already caps this at 255 entries.
        int n = _nPending;
        if (n < InlinePendingCapacity)
        {
            _inlinePending[n] = id;
        }
        else
        {
            int spill = n - InlinePendingCapacity;
            if (_pending == null)
            {
                _pending = new int[InitialPendingCapacity];
            }
            else if (spill == _pending.Length)
            {
                Array.Resize(ref _pending, _pending.Length * 2);
            }
            _pending[spill] = id;
        }
        _nPending = n + 1;
        _depth++;
    }

    /// <summary>
    /// Close the most recently opened nested sequence, letting it <b>vanish</b> if
    /// it received no content.
    /// </summary>
    /// <remarks>
    /// Use it wherever absence encodes the same value as an empty frame: a
    /// <c>struct</c>/<c>union</c> field, and an array field whose declared
    /// <c>default</c> is the empty collection (MESSAGE_SPEC §2). Where the frame
    /// must be visible, close with <see cref="WriteSequenceEndKeep"/> instead.
    /// </remarks>
    public void WriteSequenceEnd()
    {
        if (_nPending != 0)
        {
            // The innermost open sequence is the last held-back one: drop it,
            // header and end marker both.
            _nPending--;
            if (_depth > 0)
            {
                _depth--;
            }
            return;
        }
        WriteIdType(0, T_SEQUENCE_END);
        if (_depth > 0)
        {
            _depth--;
        }
    }

    /// <summary>
    /// Close the most recently opened nested sequence, <b>keeping</b> its frame
    /// even when it received no content.
    /// </summary>
    /// <remarks>
    /// Behaves like a write: it first emits any held-back headers — this frame's
    /// and every enclosing one's — and then the end marker, so an empty sequence
    /// still reaches the wire as <c>begin</c> + <c>end</c>.
    /// <para>
    /// Required wherever the frame carries information beyond its contents:
    /// </para>
    /// <list type="bullet">
    /// <item><description>a <b>wrapper-array element</b> (<c>struct</c>/<c>union</c>/nested row):
    /// element presence is what carries a dynamic array's length — <i>highest
    /// present id + 1</i> (MESSAGE_SPEC §5.1) — so dropping an all-default element
    /// would change the decoded length, not just the bytes;</description></item>
    /// <item><description>an array field already known to <b>differ from a non-empty declared
    /// <c>default</c></b>: absence would reconstruct that default, so the empty
    /// frame is the only encoding of "explicitly empty" (§2, §3).</description></item>
    /// </list>
    /// <para>
    /// The two failure directions are not symmetric, which is why this is the safe
    /// choice when in doubt: using it where <see cref="WriteSequenceEnd"/> would do
    /// costs one non-canonical empty frame that a decoder normalizes away, while
    /// the reverse silently changes an array's length.
    /// </para>
    /// </remarks>
    public void WriteSequenceEndKeep()
    {
        if (_nPending != 0)
        {
            CommitPending();
        }
        WriteIdType(0, T_SEQUENCE_END);
        if (_depth > 0)
        {
            _depth--;
        }
    }
}
