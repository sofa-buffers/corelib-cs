/*
 * SofaBuffers C# - decoder tests over the C reference byte vectors plus
 * malformed-input handling and chunk-boundary streaming.
 *
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Generic;
using System.Globalization;
using Xunit;
using SofaBuffers.Tests.Common;

namespace SofaBuffers.Tests;

public class IStreamTests
{
    private static byte[] Bytes(params int[] values)
    {
        var outp = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            outp[i] = (byte)values[i];
        }
        return outp;
    }

    private static List<string> Decode(byte[] data)
    {
        var v = new RecordingVisitor();
        new IStream().Feed(data, v);
        return v.Events;
    }

    /// <summary>Feed one byte at a time to prove decoding survives any chunk boundary.</summary>
    private static List<string> DecodeByteByByte(byte[] data)
    {
        var v = new RecordingVisitor();
        var iss = new IStream();
        foreach (byte b in data)
        {
            iss.Feed(new[] { b }, v);
        }
        return v.Events;
    }

    private static string F64(double v) => "f64:0=" + v.ToString("R", CultureInfo.InvariantCulture);

    [Fact]
    public void UnsignedScalar()
    {
        Assert.Equal(new[] { "u:0=42" }, Decode(Bytes(0x00, 0x2A)));
    }

    [Fact]
    public void SignedScalar()
    {
        Assert.Equal(new[] { "s:2=-42" }, Decode(Bytes(0x11, 0x53)));
    }

    [Fact]
    public void Fp32Scalar()
    {
        Assert.Equal(new[] { "f32:0=3.1415" }, Decode(Bytes(0x02, 0x20, 0x56, 0x0E, 0x49, 0x40)));
    }

    [Fact]
    public void StringScalar()
    {
        Assert.Equal(
            new[] { "str:0=Hello Couch!" },
            Decode(Bytes(0x02, 0x62, 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0x43, 0x6F, 0x75, 0x63, 0x68, 0x21)));
    }

    [Fact]
    public void UnsignedArray()
    {
        Assert.Equal(
            new[] { "arr:0:UNSIGNED:5", "u:0=1", "u:0=2", "u:0=3", "u:0=2147483648", "u:0=4294967295" },
            Decode(Bytes(0x03, 0x05, 0x01, 0x02, 0x03, 0x80, 0x80, 0x80, 0x80, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F)));
    }

    [Fact]
    public void Fp64Array()
    {
        List<string> ev = Decode(Bytes(
            0x05, 0x05, 0x41, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x08, 0x40, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xEF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xEF, 0x7F));
        Assert.Equal(
            new[] { "arr:0:FIXLEN:5", F64(1.0), F64(2.0), F64(3.0), F64(-double.MaxValue), F64(double.MaxValue) },
            ev);
    }

    [Fact]
    public void NestedSequence()
    {
        Assert.Equal(
            new[] { "u:0=42", "seq{:1", "u:0=42", "s:2=-42", "seq}", "s:2=-42" },
            Decode(Bytes(0x00, 0x2A, 0x0E, 0x00, 0x2A, 0x11, 0x53, 0x07, 0x11, 0x53)));
    }

    [Fact]
    public void ByteByByteMatchesWhole()
    {
        byte[] msg = Bytes(0x00, 0x2A, 0x0E, 0x00, 0x2A, 0x11, 0x53, 0x07, 0x11, 0x53,
            0x02, 0x62, 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0x43, 0x6F, 0x75, 0x63, 0x68, 0x21);
        Assert.Equal(Decode(msg), DecodeByteByByte(msg));
    }

    // --- malformed input ----------------------------------------------------

    [Fact]
    public void VarintOverflowRejected()
    {
        // 10 continuation bytes overflow the 64-bit value type.
        byte[] bad = Bytes(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        var ex = Assert.Throws<SofabException>(() => Decode(bad));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
    }

    [Fact]
    public void DanglingSequenceEndRejected()
    {
        var ex = Assert.Throws<SofabException>(() => Decode(Bytes(0x07)));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
    }

    [Fact]
    public void ZeroCountArraysAccepted()
    {
        // Unsigned (0x03), signed (0x04) and fixlen (0x05) arrays, id 0, count 0.
        // A zero-count array is just [ header ][ count=0 ] (§4.7-4.8); the fixlen
        // form carries no fixlen_word. Each yields a single ArrayBegin, no elements.
        Assert.Equal(new[] { "arr:0:UNSIGNED:0" }, Decode(Bytes(0x03, 0x00)));
        Assert.Equal(new[] { "arr:0:SIGNED:0" }, Decode(Bytes(0x04, 0x00)));
        Assert.Equal(new[] { "arr:0:FIXLEN:0" }, Decode(Bytes(0x05, 0x00)));

        // Byte-at-a-time must agree (exercises StepArrayCount's zero-count path).
        Assert.Equal(new[] { "arr:0:FIXLEN:0" }, DecodeByteByByte(Bytes(0x05, 0x00)));
    }

    [Fact]
    public void NestingBeyondMaxDepthRejected()
    {
        // 255 sequence starts (0x06) decode fine; the 256th must be rejected.
        var ok = new byte[255];
        for (int i = 0; i < 255; i++)
        {
            ok[i] = 0x06;
        }
        Assert.Equal(255, Decode(ok).Count); // 255 SequenceBegin events, no throw

        var bad = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            bad[i] = 0x06;
        }
        var ex = Assert.Throws<SofabException>(() => Decode(bad));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
    }

    [Fact]
    public void BadFp32LengthRejected()
    {
        // fixlen header: id 0, fp32 subtype but length 5 (must be 4).
        var ex = Assert.Throws<SofabException>(() => Decode(Bytes(0x02, 0x28)));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
    }
}
