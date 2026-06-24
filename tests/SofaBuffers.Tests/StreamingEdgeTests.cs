/*
 * SofaBuffers C# - decoder edge cases: empty string/blob fields and an array
 * whose element count needs a multi-byte varint (split across the state machine).
 *
 * SPDX-License-Identifier: MIT
 */

using Xunit;
using SofaBuffers.Tests.Common;

namespace SofaBuffers.Tests;

public class StreamingEdgeTests
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

    [Fact]
    public void EmptyStringEmitsOnce()
    {
        var v = new RecordingVisitor();
        new IStream().Feed(Bytes(0x02, 0x02), v); // fixlen id 0, STRING length 0
        Assert.Equal(new[] { "str:0=" }, v.Events);
    }

    [Fact]
    public void EmptyBlobEmitsOnce()
    {
        var v = new RecordingVisitor();
        new IStream().Feed(Bytes(0x02, 0x03), v); // fixlen id 0, BLOB length 0
        Assert.Equal(new[] { "blob:0=" }, v.Events);
    }

    [Fact]
    public void ArrayWithMultiByteCount()
    {
        // 200 elements -> the count varint is two bytes (0xC8 0x01), exercising the
        // "need more bytes" path of the array-count state.
        const int n = 200;
        var src = new ulong[n];
        for (int i = 0; i < n; i++)
        {
            src[i] = (ulong)i;
        }
        var buf = new byte[n * 2 + 16];
        var os = new OStream(buf);
        os.WriteArrayUnsigned(1, src);

        var v = new MultiByteCountVisitor();
        new IStream().Feed(buf, 0, os.BytesUsed, v);
        Assert.Equal(n, v.Begin);
        Assert.Equal(n, v.Count);
        Assert.Equal((long)n * (n - 1) / 2, v.Sum); // 0+1+...+199
    }

    private sealed class MultiByteCountVisitor : IVisitor
    {
        public int Begin = -1;
        public long Count;
        public long Sum;

        public void ArrayBegin(int id, ArrayKind kind, int c) { Begin = c; }
        public void Unsigned(int id, ulong value)
        {
            Count++;
            Sum += (long)value;
        }
    }
}
