/*
 * SofaBuffers C# - encode -> decode value-preservation tests.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace SofaBuffers.Tests;

public class RoundTripTests
{
    /// <summary>Visitor that captures every decoded value into typed lists.</summary>
    private sealed class Capture : IVisitor
    {
        public readonly List<ulong> Unsigneds = new();
        public readonly List<long> Signeds = new();
        public readonly List<float> F32 = new();
        public readonly List<double> F64 = new();
        public readonly List<string> Strings = new();
        public readonly List<string> SeqEvents = new();

        public void Unsigned(int id, ulong v) { Unsigneds.Add(v); }
        public void Signed(int id, long v) { Signeds.Add(v); }
        public void Fp32(int id, float v) { F32.Add(v); }
        public void Fp64(int id, double v) { F64.Add(v); }
        public void String(int id, int total, int offset, byte[] d, int o, int l)
        {
            Strings.Add(Encoding.UTF8.GetString(d, o, l));
        }
        public void ArrayBegin(int id, ArrayKind kind, int count)
        {
            SeqEvents.Add("arr:" + kind.ToString().ToUpperInvariant() + ":" + count);
        }
        public void SequenceBegin(int id) { SeqEvents.Add("seq{" + id); }
        public void SequenceEnd() { SeqEvents.Add("seq}"); }
    }

    private static Capture Roundtrip(Action<OStream> body)
    {
        var buf = new byte[4096];
        var os = new OStream(buf);
        body(os);
        var c = new Capture();
        new IStream().Feed(buf, 0, os.BytesUsed, c);
        return c;
    }

    [Fact]
    public void Scalars()
    {
        Capture c = Roundtrip(os =>
        {
            os.WriteUnsigned(1, 0xDEAD_BEEFUL);
            os.WriteUnsigned(2, ulong.MaxValue);          // UINT64_MAX
            os.WriteSigned(3, -5_000_000_000_000L);
            os.WriteSigned(4, long.MinValue);
            os.WriteBoolean(5, true);
            os.WriteFp32(6, 3.14159f);
            os.WriteFp64(7, 2.718281828459045);
        });
        Assert.Equal(new ulong[] { 0xDEAD_BEEFUL, ulong.MaxValue, 1UL }, c.Unsigneds);
        Assert.Equal(new[] { -5_000_000_000_000L, long.MinValue }, c.Signeds);
        Assert.Equal(new[] { 3.14159f }, c.F32);
        Assert.Equal(new[] { 2.718281828459045 }, c.F64);
    }

    [Fact]
    public void StringsAndBlobs()
    {
        var blob = new byte[300];
        for (int i = 0; i < blob.Length; i++)
        {
            blob[i] = (byte)(i * 7);
        }
        var buf = new byte[4096];
        var os = new OStream(buf);
        os.WriteString(1, "grüße"); // multi-byte UTF-8
        os.WriteBlob(2, blob);

        var v = new StringsAndBlobsVisitor();
        new IStream().Feed(buf, 0, os.BytesUsed, v);
        Assert.Equal(new[] { "grüße" }, v.Texts);
        Assert.Equal(blob, v.Captured);
    }

    /// <summary>Collects string fields and reassembles a chunked blob field.</summary>
    private sealed class StringsAndBlobsVisitor : IVisitor
    {
        public readonly List<string> Texts = new();
        public byte[]? Captured;

        public void String(int id, int total, int offset, byte[] d, int o, int l)
        {
            Texts.Add(Encoding.UTF8.GetString(d, o, l));
        }

        public void Blob(int id, int total, int offset, byte[] d, int o, int l)
        {
            Captured ??= new byte[total];
            Array.Copy(d, o, Captured, offset, l);
        }
    }

    [Fact]
    public void ArraysPreserveValues()
    {
        var u = new ulong[] { 0, 1, 0xFFFF_FFFF_FFFF_FFFFUL, 0x1234_5678_9ABC_DEF0UL };
        var s = new int[] { 0, -1, int.MinValue, int.MaxValue };
        var d = new double[] { 1.5, -2.5, 1e300 };

        Capture c = Roundtrip(os =>
        {
            os.WriteArrayUnsigned(1, u);
            os.WriteArraySigned(2, s);
            os.WriteArrayFp64(3, d);
        });

        Assert.Equal(new ulong[] { 0UL, 1UL, ulong.MaxValue, 0x1234_5678_9ABC_DEF0UL }, c.Unsigneds);
        Assert.Equal(new long[] { 0L, -1L, int.MinValue, int.MaxValue }, c.Signeds);
        Assert.Equal(new[] { 1.5, -2.5, 1e300 }, c.F64);
    }

    [Fact]
    public void NestedSequencesBalance()
    {
        Capture c = Roundtrip(os =>
        {
            os.WriteUnsigned(1, 1);
            os.WriteSequenceBegin(2);
            os.WriteUnsigned(1, 2);
            os.WriteSequenceBegin(3);
            os.WriteUnsigned(1, 3);
            os.WriteSequenceEnd();
            os.WriteSequenceEnd();
        });
        Assert.Equal(new[] { "seq{2", "seq{3", "seq}", "seq}" }, c.SeqEvents);
        Assert.Equal(new ulong[] { 1UL, 2UL, 3UL }, c.Unsigneds);
    }
}
