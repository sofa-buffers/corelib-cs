/*
 * SofaBuffers C# - encoder tests (byte-exact vs. the C reference vectors).
 *
 * The expected byte arrays are copied verbatim from the C corelib reference
 * suite (test/c/test_ostream.c) to guarantee byte-for-byte interoperability.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using Xunit;

namespace SofaBuffers.Tests;

public class OStreamTests
{
    /// <summary>Encode via <paramref name="body"/> into a fresh buffer and return exactly the used bytes.</summary>
    private static byte[] Encode(Action<OStream> body)
    {
        var buf = new byte[256];
        var os = new OStream(buf);
        body(os);
        var outp = new byte[os.BytesUsed];
        Array.Copy(buf, outp, os.BytesUsed);
        return outp;
    }

    private static byte[] Bytes(params int[] values)
    {
        var outp = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            outp[i] = (byte)values[i];
        }
        return outp;
    }

    [Fact]
    public void UnsignedIdMin()
    {
        Assert.Equal(Bytes(0x00, 0x00), Encode(os => os.WriteUnsigned(0, 0)));
    }

    [Fact]
    public void UnsignedIdMax()
    {
        Assert.Equal(
            Bytes(0xF8, 0xFF, 0xFF, 0xFF, 0x3F, 0x00),
            Encode(os => os.WriteUnsigned(int.MaxValue, 0)));
    }

    [Fact]
    public void UnsignedMax()
    {
        // UINT64_MAX -> ten 0xFF payload bytes then 0x01.
        Assert.Equal(
            Bytes(0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01),
            Encode(os => os.WriteUnsigned(0, ulong.MaxValue)));
    }

    [Fact]
    public void SignedMin()
    {
        Assert.Equal(
            Bytes(0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01),
            Encode(os => os.WriteSigned(0, long.MinValue)));
    }

    [Fact]
    public void SignedMax()
    {
        Assert.Equal(
            Bytes(0x01, 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01),
            Encode(os => os.WriteSigned(0, long.MaxValue)));
    }

    [Fact]
    public void BooleanTrue()
    {
        Assert.Equal(Bytes(0x00, 0x01), Encode(os => os.WriteBoolean(0, true)));
    }

    [Fact]
    public void Fp32()
    {
        Assert.Equal(
            Bytes(0x02, 0x20, 0x56, 0x0E, 0x49, 0x40),
            Encode(os => os.WriteFp32(0, 3.1415f)));
    }

    [Fact]
    public void Fp64()
    {
        // The C reference widens a float literal: (double) 3.14159265f.
        Assert.Equal(
            Bytes(0x02, 0x41, 0x00, 0x00, 0x00, 0x60, 0xFB, 0x21, 0x09, 0x40),
            Encode(os => os.WriteFp64(0, (double)3.14159265f)));
    }

    [Fact]
    public void String()
    {
        Assert.Equal(
            Bytes(0x02, 0x62, 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0x43, 0x6F, 0x75, 0x63, 0x68, 0x21),
            Encode(os => os.WriteString(0, "Hello Couch!")));
    }

    [Fact]
    public void StringEmpty()
    {
        Assert.Equal(Bytes(0x02, 0x02), Encode(os => os.WriteString(0, "")));
    }

    [Fact]
    public void Blob()
    {
        Assert.Equal(
            Bytes(0x02, 0x2B, 0x01, 0x02, 0x03, 0x04, 0x05),
            Encode(os => os.WriteBlob(0, Bytes(0x01, 0x02, 0x03, 0x04, 0x05))));
    }

    [Fact]
    public void BlobEmpty()
    {
        Assert.Equal(Bytes(0x02, 0x03), Encode(os => os.WriteBlob(0, Array.Empty<byte>())));
    }

    [Fact]
    public void ArrayUnsigned32()
    {
        var a = new uint[] { 1, 2, 3, 0x80000000, 0xFFFFFFFF };
        Assert.Equal(
            Bytes(0x03, 0x05, 0x01, 0x02, 0x03, 0x80, 0x80, 0x80, 0x80, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F),
            Encode(os => os.WriteArrayUnsigned(0, a)));
    }

    [Fact]
    public void ArrayUnsigned16()
    {
        var a = new ushort[] { 1, 2, 3, 0, 0xFFFF };
        Assert.Equal(
            Bytes(0x03, 0x05, 0x01, 0x02, 0x03, 0x00, 0xFF, 0xFF, 0x03),
            Encode(os => os.WriteArrayUnsigned(0, a)));
    }

    [Fact]
    public void ArraySigned32()
    {
        var a = new int[] { -1, -2, -3, int.MinValue, int.MaxValue };
        Assert.Equal(
            Bytes(0x04, 0x05, 0x01, 0x03, 0x05, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0xFE, 0xFF, 0xFF, 0xFF, 0x0F),
            Encode(os => os.WriteArraySigned(0, a)));
    }

    [Fact]
    public void ArrayFp32()
    {
        var a = new float[] { 1.0f, 2.0f, 3.0f, -float.MaxValue, float.MaxValue };
        Assert.Equal(
            Bytes(0x05, 0x05, 0x20, 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x40, 0x00,
                  0x00, 0x40, 0x40, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF, 0x7F, 0x7F),
            Encode(os => os.WriteArrayFp32(0, a)));
    }

    [Fact]
    public void ArrayFp64()
    {
        var a = new double[] { 1.0, 2.0, 3.0, -double.MaxValue, double.MaxValue };
        Assert.Equal(
            Bytes(0x05, 0x05, 0x41, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F, 0x00,
                  0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00,
                  0x00, 0x08, 0x40, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xEF, 0xFF, 0xFF,
                  0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xEF, 0x7F),
            Encode(os => os.WriteArrayFp64(0, a)));
    }

    [Fact]
    public void NestedSequence()
    {
        Assert.Equal(
            Bytes(0x00, 0x2A, 0x0E, 0x00, 0x2A, 0x11, 0x53, 0x07, 0x11, 0x53),
            Encode(os =>
            {
                os.WriteUnsigned(0, 42);
                os.WriteSequenceBegin(1);
                os.WriteUnsigned(0, 42);
                os.WriteSigned(2, -42);
                os.WriteSequenceEnd();
                os.WriteSigned(2, -42);
            }));
    }

    [Fact]
    public void NestedSequenceWithArray()
    {
        Assert.Equal(
            Bytes(0x00, 0x2A, 0x1E, 0x00, 0x2A, 0x1C, 0x03, 0x53, 0x55, 0x57, 0x07, 0x11, 0x53),
            Encode(os =>
            {
                os.WriteUnsigned(0, 42);
                os.WriteSequenceBegin(3);
                os.WriteUnsigned(0, 42);
                os.WriteArraySigned(3, new int[] { -42, -43, -44 });
                os.WriteSequenceEnd();
                os.WriteSigned(2, -42);
            }));
    }

    // --- error / argument handling -----------------------------------------

    [Fact]
    public void IdOverflowRejected()
    {
        var ex = Assert.Throws<SofabException>(
            () => new OStream(new byte[16]).WriteUnsigned(-1, 0));
        Assert.Equal(SofabError.Argument, ex.Error);
    }

    [Fact]
    public void BufferFullWithoutSink()
    {
        var ex = Assert.Throws<SofabException>(
            () => new OStream(new byte[2]).WriteUnsigned(0, ulong.MaxValue));
        Assert.Equal(SofabError.BufferFull, ex.Error);
    }
}
