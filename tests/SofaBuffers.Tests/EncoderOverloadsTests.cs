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

    [Fact]
    public void EmptyArraysRejected()
    {
        var os = new OStream(new byte[16]);
        Assert.Equal(SofabError.Argument,
            Assert.Throws<SofabException>(() => os.WriteArrayUnsigned(1, Array.Empty<uint>())).Error);
        Assert.Equal(SofabError.Argument,
            Assert.Throws<SofabException>(() => os.WriteArraySigned(1, Array.Empty<long>())).Error);
        Assert.Equal(SofabError.Argument,
            Assert.Throws<SofabException>(() => os.WriteArrayFp32(1, Array.Empty<float>())).Error);
        Assert.Equal(SofabError.Argument,
            Assert.Throws<SofabException>(() => os.WriteArrayFp64(1, Array.Empty<double>())).Error);
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
