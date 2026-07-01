/*
 * SofaBuffers C# - exercises every OStream writer overload and its argument
 * validation, decoding the result back to confirm the values survive.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using Xunit;

namespace SofaBuffers.Tests;

public class EncoderOverloadsTests
{
    private sealed class UnsignedSink : IVisitor
    {
        public readonly List<ulong> Out = new();
        public void Unsigned(int id, ulong v) { Out.Add(v); }
    }

    private sealed class SignedSink : IVisitor
    {
        public readonly List<long> Out = new();
        public void Signed(int id, long v) { Out.Add(v); }
    }

    private sealed class Fp32Sink : IVisitor
    {
        public readonly List<float> Out = new();
        public void Fp32(int id, float v) { Out.Add(v); }
    }

    private sealed class BlobSink : IVisitor
    {
        public byte[]? Got;
        public void Blob(int id, int total, int offset, byte[] d, int o, int l)
        {
            Got = new byte[l];
            Array.Copy(d, o, Got, 0, l);
        }
    }

    private static List<ulong> DecodeUnsigned(byte[] buf, int len)
    {
        var s = new UnsignedSink();
        new IStream().Feed(buf, 0, len, s);
        return s.Out;
    }

    private static List<long> DecodeSigned(byte[] buf, int len)
    {
        var s = new SignedSink();
        new IStream().Feed(buf, 0, len, s);
        return s.Out;
    }

    [Fact]
    public void UnsignedArrayOverloads()
    {
        var buf = new byte[64];

        var os = new OStream(buf);
        os.WriteArrayUnsigned(1, new byte[] { 0, 1, 255 });
        Assert.Equal(new ulong[] { 0UL, 1UL, 255UL }, DecodeUnsigned(buf, os.BytesUsed));

        os = new OStream(buf);
        os.WriteArrayUnsigned(1, new ushort[] { 0, 1, 0xFFFF });
        Assert.Equal(new ulong[] { 0UL, 1UL, 0xFFFFUL }, DecodeUnsigned(buf, os.BytesUsed));

        os = new OStream(buf);
        os.WriteArrayUnsigned(1, new ulong[] { 0UL, ulong.MaxValue, 0x1234_5678_9ABC_DEF0UL }); // 64-bit
        Assert.Equal(new ulong[] { 0UL, ulong.MaxValue, 0x1234_5678_9ABC_DEF0UL }, DecodeUnsigned(buf, os.BytesUsed));
    }

    [Fact]
    public void SignedArrayOverloads()
    {
        var buf = new byte[64];

        var os = new OStream(buf);
        os.WriteArraySigned(1, new sbyte[] { -1, 0, 1, sbyte.MinValue });
        Assert.Equal(new long[] { -1L, 0L, 1L, sbyte.MinValue }, DecodeSigned(buf, os.BytesUsed));

        os = new OStream(buf);
        os.WriteArraySigned(1, new short[] { -1, 0, 1, short.MinValue });
        Assert.Equal(new long[] { -1L, 0L, 1L, short.MinValue }, DecodeSigned(buf, os.BytesUsed));

        os = new OStream(buf);
        os.WriteArraySigned(1, new long[] { long.MinValue, -1L, long.MaxValue });
        Assert.Equal(new long[] { long.MinValue, -1L, long.MaxValue }, DecodeSigned(buf, os.BytesUsed));
    }

    [Fact]
    public void Fp32ArrayRoundtrips()
    {
        var buf = new byte[64];
        var os = new OStream(buf);
        os.WriteArrayFp32(1, new float[] { 1.5f, -2.5f, 3.25f });
        var s = new Fp32Sink();
        new IStream().Feed(buf, 0, os.BytesUsed, s);
        Assert.Equal(new[] { 1.5f, -2.5f, 3.25f }, s.Out);
    }

    [Fact]
    public void BooleanFalse()
    {
        var buf = new byte[8];
        var os = new OStream(buf);
        os.WriteBoolean(9, false);
        Assert.Equal(new ulong[] { 0UL }, DecodeUnsigned(buf, os.BytesUsed));
    }

    [Fact]
    public void BlobSlice()
    {
        var buf = new byte[32];
        var src = new byte[] { 9, 9, 1, 2, 3, 9 };
        var os = new OStream(buf);
        os.WriteBlob(1, src, 2, 3); // only {1,2,3}
        var s = new BlobSink();
        new IStream().Feed(buf, 0, os.BytesUsed, s);
        Assert.Equal(new byte[] { 1, 2, 3 }, s.Got);
    }

    [Fact]
    public void WriteFixlenRejectsNegativeLength()
    {
        var ex = Assert.Throws<SofabException>(
            () => new OStream(new byte[16]).WriteFixlen(0, new byte[4], 0, -1, FixlenType.Blob));
        Assert.Equal(SofabError.Argument, ex.Error);
    }

    private sealed class ArraySink : IVisitor
    {
        public int Begins;
        public ArrayKind LastKind;
        public int LastCount = -1;
        public int Elements;
        public void ArrayBegin(int id, ArrayKind kind, int count)
        {
            Begins++;
            LastKind = kind;
            LastCount = count;
        }
        public void Unsigned(int id, ulong v) { Elements++; }
        public void Signed(int id, long v) { Elements++; }
        public void Fp32(int id, float v) { Elements++; }
        public void Fp64(int id, double v) { Elements++; }
    }

    [Fact]
    public void ZeroCountArraysRoundtrip()
    {
        var buf = new byte[16];

        // Zero-count unsigned array: exactly [ header (id 1 | type 3) ][ count = 0 ].
        var os = new OStream(buf);
        os.WriteArrayUnsigned(1, Array.Empty<uint>());
        Assert.Equal(2, os.BytesUsed);
        Assert.Equal((byte)((1 << 3) | 0x3), buf[0]);
        Assert.Equal(0, buf[1]);
        var sink = new ArraySink();
        new IStream().Feed(buf, 0, os.BytesUsed, sink);
        Assert.Equal(1, sink.Begins);
        Assert.Equal(ArrayKind.Unsigned, sink.LastKind);
        Assert.Equal(0, sink.LastCount);
        Assert.Equal(0, sink.Elements);

        // Zero-count signed array.
        os = new OStream(buf);
        os.WriteArraySigned(1, Array.Empty<long>());
        Assert.Equal(2, os.BytesUsed);
        Assert.Equal((byte)((1 << 3) | 0x4), buf[0]);
        Assert.Equal(0, buf[1]);
        sink = new ArraySink();
        new IStream().Feed(buf, 0, os.BytesUsed, sink);
        Assert.Equal(1, sink.Begins);
        Assert.Equal(ArrayKind.Signed, sink.LastKind);
        Assert.Equal(0, sink.LastCount);
        Assert.Equal(0, sink.Elements);

        // Zero-count fp32 fixlen array: the fixlen_word is still emitted (so it is
        // distinguishable from an empty fp64 array), followed by no payload (§4.8).
        os = new OStream(buf);
        os.WriteArrayFp32(1, Array.Empty<float>());
        Assert.Equal(3, os.BytesUsed);
        Assert.Equal((byte)((1 << 3) | 0x5), buf[0]);
        Assert.Equal(0, buf[1]);
        Assert.Equal((byte)((4 << 3) | 0x0), buf[2]); // fixlen_word 0x20: len 4, fp32
        sink = new ArraySink();
        new IStream().Feed(buf, 0, os.BytesUsed, sink);
        Assert.Equal(1, sink.Begins);
        Assert.Equal(ArrayKind.Fixlen, sink.LastKind);
        Assert.Equal(0, sink.LastCount);
        Assert.Equal(0, sink.Elements);

        // Zero-count fp64 fixlen array: carries its own fixlen_word too, so it is
        // no longer byte-identical to an empty fp32 array.
        os = new OStream(buf);
        os.WriteArrayFp64(1, Array.Empty<double>());
        Assert.Equal(3, os.BytesUsed);
        Assert.Equal((byte)((1 << 3) | 0x5), buf[0]);
        Assert.Equal(0, buf[1]);
        Assert.Equal((byte)((8 << 3) | 0x1), buf[2]); // fixlen_word 0x41: len 8, fp64
        sink = new ArraySink();
        new IStream().Feed(buf, 0, os.BytesUsed, sink);
        Assert.Equal(1, sink.Begins);
        Assert.Equal(ArrayKind.Fixlen, sink.LastKind);
        Assert.Equal(0, sink.LastCount);
        Assert.Equal(0, sink.Elements);
    }

    [Fact]
    public void ZeroCountArrayResumesNextField()
    {
        // A zero-count array must not swallow the following field. Encode an empty
        // unsigned array then a scalar, and confirm both decode (byte-by-byte too).
        var buf = new byte[16];
        var os = new OStream(buf);
        os.WriteArrayUnsigned(1, Array.Empty<uint>());
        os.WriteUnsigned(2, 42);
        int len = os.BytesUsed;

        var whole = new ArraySink();
        new IStream().Feed(buf, 0, len, whole);
        Assert.Equal(1, whole.Begins);
        Assert.Equal(1, whole.Elements); // the trailing scalar, not an array element

        // Same result feeding one byte at a time (exercises the byte machine).
        var chunked = new ArraySink();
        var iss = new IStream();
        for (int i = 0; i < len; i++)
        {
            iss.Feed(buf, i, 1, chunked);
        }
        Assert.Equal(1, chunked.Begins);
        Assert.Equal(1, chunked.Elements);
    }

    [Fact]
    public void SequenceDepthBeyondMaxRejectedOnEncode()
    {
        var os = new OStream(new byte[1024]);
        for (int i = 0; i < 255; i++)
        {
            os.WriteSequenceBegin(0); // 255 levels open fine
        }
        var ex = Assert.Throws<SofabException>(() => os.WriteSequenceBegin(0)); // 256th rejected
        Assert.Equal(SofabError.Argument, ex.Error);
    }

    [Fact]
    public void ConstructorValidatesArguments()
    {
        Assert.Throws<ArgumentException>(() => new OStream(Array.Empty<byte>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OStream(new byte[8], 9));
        Assert.Throws<ArgumentException>(() => new OStream(null!));
    }

    [Fact]
    public void BufferSetSwapsBufferAndValidates()
    {
        var os = new OStream(new byte[4]);
        var fresh = new byte[16];
        os.BufferSet(fresh, 2); // swap in a new buffer, reserving 2 bytes up front
        os.WriteUnsigned(7, 42);
        Assert.Equal(2 + 2, os.BytesUsed); // 2 reserved + header(1) + value(1)

        // The field lands in the new buffer, after the reserved prefix.
        var got = new List<ulong>();
        new IStream().Feed(fresh, 2, os.BytesUsed - 2, new BufferSetSink(got));
        Assert.Equal(new ulong[] { 42UL }, got);

        Assert.Throws<ArgumentException>(() => os.BufferSet(Array.Empty<byte>(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => os.BufferSet(new byte[4], 99));
    }

    private sealed class BufferSetSink : IVisitor
    {
        private readonly List<ulong> _got;
        public BufferSetSink(List<ulong> got) { _got = got; }
        public void Unsigned(int id, ulong v)
        {
            Assert.Equal(7, id);
            _got.Add(v);
        }
    }
}
