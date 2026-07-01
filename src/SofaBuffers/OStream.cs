/*
 * SofaBuffers C# - streaming output encoder (port of ostream.c).
 *
 * SPDX-License-Identifier: MIT
 */

using System;
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
/// lower-layer protocol header, avoiding a copy.
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
    private readonly FlushSink? _sink;

    /// <summary>
    /// Create an encoder over <paramref name="buffer"/> with no flush sink.
    /// Writing past the end of the buffer raises <see cref="SofabError.BufferFull"/>.
    /// </summary>
    /// <param name="buffer">caller-owned output buffer (length &gt; 0)</param>
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
    /// <param name="buffer">caller-owned output buffer (length &gt; 0)</param>
    /// <param name="offset">initial write position (<c>0..buffer.Length</c>)</param>
    /// <param name="sink">flush sink, or <c>null</c> for none</param>
    public OStream(byte[] buffer, int offset, FlushSink? sink)
    {
        if (buffer == null || buffer.Length == 0)
        {
            throw new ArgumentException("buffer must be non-empty", nameof(buffer));
        }
        if (offset < 0 || offset > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "offset out of range");
        }
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
            _sink(_buffer, 0, used);
            _offset = 0;
        }
        return used;
    }

    /// <summary>
    /// Replace the active buffer (typically from within a flush sink), resuming
    /// writes at <paramref name="offset"/> in the new buffer.
    /// </summary>
    /// <param name="buffer">new caller-owned output buffer (length &gt; 0)</param>
    /// <param name="offset">initial write position (<c>0..buffer.Length</c>)</param>
    public void BufferSet(byte[] buffer, int offset)
    {
        if (buffer == null || buffer.Length == 0)
        {
            throw new ArgumentException("buffer must be non-empty", nameof(buffer));
        }
        if (offset < 0 || offset > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "offset out of range");
        }
        _buffer = buffer;
        _end = buffer.Length;
        _offset = offset;
    }

    // --- primitives ---------------------------------------------------------

    /// <summary>
    /// Append one byte to the active buffer. When the buffer is full, flush it to
    /// the sink and resume at the start; with no sink this raises
    /// <see cref="SofabError.BufferFull"/>.
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
            _sink(_buffer, 0, _offset);
            _offset = 0;
        }
        _buffer[_offset++] = (byte)b;
    }

    /// <summary>Append <paramref name="len"/> raw bytes from <paramref name="data"/>, flushing as needed.</summary>
    /// <param name="data">source array</param>
    /// <param name="from">start offset within <paramref name="data"/></param>
    /// <param name="len">number of bytes to append</param>
    private void PushRaw(byte[] data, int from, int len)
    {
        for (int i = 0; i < len; i++)
        {
            PushByte(data[from + i]);
        }
    }

    /// <summary>Append a value as a base-128 LEB128 varint (7 bits per byte, low bytes first).</summary>
    /// <param name="value">the unsigned value to encode</param>
    private void WriteVarint(ulong value)
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
    /// <exception cref="SofabException">
    /// with <see cref="SofabError.Argument"/> if <paramref name="id"/> is out of range
    /// </exception>
    private void WriteIdType(int id, int wireType)
    {
        if (id < 0 || id > ID_MAX)
        {
            throw new SofabException(SofabError.Argument, "id " + id);
        }
        WriteVarint(((ulong)id << 3) | (uint)wireType);
    }

    // --- scalar writers -----------------------------------------------------

    /// <summary>
    /// Write an unsigned-integer field.
    /// </summary>
    /// <param name="id">field id (<c>0..ID_MAX</c>)</param>
    /// <param name="value">unsigned value</param>
    public void WriteUnsigned(int id, ulong value)
    {
        WriteIdType(id, T_VARINT_UNSIGNED);
        WriteVarint(value);
    }

    /// <summary>Write a signed-integer field (ZigZag + varint).</summary>
    /// <param name="id">field id</param>
    /// <param name="value">signed value</param>
    public void WriteSigned(int id, long value)
    {
        WriteIdType(id, T_VARINT_SIGNED);
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
    public void WriteFixlen(int id, byte[] data, int from, int length, FixlenType subtype)
    {
        if (length < 0)
        {
            throw new SofabException(SofabError.Argument, "length " + length);
        }
        WriteIdType(id, T_FIXLEN);
        WriteVarint(((ulong)length << 3) | (uint)subtype.Raw());
        PushRaw(data, from, length);
    }

    /// <summary>Write a 32-bit float field.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">value</param>
    public void WriteFp32(int id, float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        WriteIdType(id, T_FIXLEN);
        WriteVarint((4UL << 3) | (uint)FixlenType.Fp32.Raw());
        PushByte(bits & 0xFF);
        PushByte((bits >> 8) & 0xFF);
        PushByte((bits >> 16) & 0xFF);
        PushByte((int)((uint)bits >> 24) & 0xFF);
    }

    /// <summary>Write a 64-bit float field.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">value</param>
    public void WriteFp64(int id, double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        WriteIdType(id, T_FIXLEN);
        WriteVarint((8UL << 3) | (uint)FixlenType.Fp64.Raw());
        for (int i = 0; i < 8; i++)
        {
            PushByte((int)((ulong)bits >> (i * 8)) & 0xFF);
        }
    }

    /// <summary>Write a string field (raw UTF-8 bytes, no NUL on the wire).</summary>
    /// <param name="id">field id</param>
    /// <param name="text">string value</param>
    public void WriteString(int id, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        WriteFixlen(id, bytes, 0, bytes.Length, FixlenType.String);
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
        WriteIdType(id, wireType);
        WriteVarint((uint)count);
    }

    /// <summary>Write an array of unsigned 8-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArrayUnsigned(int id, byte[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_UNSIGNED, data.Length);
        foreach (byte e in data)
        {
            WriteVarint(e);
        }
    }

    /// <summary>Write an array of unsigned 16-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArrayUnsigned(int id, ushort[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_UNSIGNED, data.Length);
        foreach (ushort e in data)
        {
            WriteVarint(e);
        }
    }

    /// <summary>Write an array of unsigned 32-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArrayUnsigned(int id, uint[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_UNSIGNED, data.Length);
        foreach (uint e in data)
        {
            WriteVarint(e);
        }
    }

    /// <summary>Write an array of unsigned 64-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArrayUnsigned(int id, ulong[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_UNSIGNED, data.Length);
        foreach (ulong e in data)
        {
            WriteVarint(e);
        }
    }

    /// <summary>Write an array of signed 8-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArraySigned(int id, sbyte[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_SIGNED, data.Length);
        foreach (sbyte e in data)
        {
            WriteVarint(ZigzagEncode(e));
        }
    }

    /// <summary>Write an array of signed 16-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArraySigned(int id, short[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_SIGNED, data.Length);
        foreach (short e in data)
        {
            WriteVarint(ZigzagEncode(e));
        }
    }

    /// <summary>Write an array of signed 32-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArraySigned(int id, int[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_SIGNED, data.Length);
        foreach (int e in data)
        {
            WriteVarint(ZigzagEncode(e));
        }
    }

    /// <summary>Write an array of signed 64-bit integers.</summary>
    /// <param name="id">field id</param>
    /// <param name="data">elements</param>
    public void WriteArraySigned(int id, long[] data)
    {
        WriteArrayHeader(id, T_VARINTARRAY_SIGNED, data.Length);
        foreach (long e in data)
        {
            WriteVarint(ZigzagEncode(e));
        }
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
        foreach (float v in data)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            PushByte(bits & 0xFF);
            PushByte((bits >> 8) & 0xFF);
            PushByte((bits >> 16) & 0xFF);
            PushByte((int)((uint)bits >> 24) & 0xFF);
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
        foreach (double v in data)
        {
            long bits = BitConverter.DoubleToInt64Bits(v);
            for (int i = 0; i < 8; i++)
            {
                PushByte((int)((ulong)bits >> (i * 8)) & 0xFF);
            }
        }
    }

    // --- sequence writers ---------------------------------------------------

    /// <summary>
    /// Open a nested sequence with the given field <paramref name="id"/>. Fields
    /// written until the matching <see cref="WriteSequenceEnd"/> belong to the
    /// sequence and form a fresh id scope.
    /// </summary>
    /// <param name="id">field id of the sequence</param>
    /// <exception cref="SofabException">
    /// with <see cref="SofabError.Argument"/> if opening this sequence would nest
    /// deeper than <c>MAX_DEPTH</c> (255) levels
    /// </exception>
    public void WriteSequenceBegin(int id)
    {
        if (_depth >= MAX_DEPTH)
        {
            throw new SofabException(SofabError.Argument, "sequence too deep");
        }
        WriteIdType(id, T_SEQUENCE_START);
        _depth++;
    }

    /// <summary>Close the most recently opened nested sequence.</summary>
    public void WriteSequenceEnd()
    {
        WriteIdType(0, T_SEQUENCE_END);
        if (_depth > 0)
        {
            _depth--;
        }
    }
}
