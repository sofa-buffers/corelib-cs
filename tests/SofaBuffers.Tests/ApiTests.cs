/*
 * SofaBuffers C# - API behaviour: offset reserve, flush-sink streaming, and
 * decoding fed in arbitrarily small chunks.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace SofaBuffers.Tests;

public class ApiTests
{
    private sealed class CollectVisitor : IVisitor
    {
        public readonly List<ulong> Values = new();
        private readonly int? _expectId;

        public CollectVisitor(int? expectId = null)
        {
            _expectId = expectId;
        }

        public void Unsigned(int id, ulong v)
        {
            if (_expectId.HasValue)
            {
                Assert.Equal(_expectId.Value, id);
            }
            Values.Add(v);
        }
    }

    [Fact]
    public void OffsetReservesHeaderRoom()
    {
        var buf = new byte[64];
        var os = new OStream(buf, 4); // reserve 4 bytes up front
        os.WriteUnsigned(7, 99);
        // The reserved prefix is left for the caller to fill in later.
        Assert.Equal(4 + 2, os.BytesUsed); // header(1) + value(1) for id 7, value 99
        // Decoding only the payload (skipping the reserved prefix) yields the field.
        var v = new CollectVisitor(7);
        new IStream().Feed(buf, 4, os.BytesUsed - 4, v);
        Assert.Equal(new ulong[] { 99UL }, v.Values);
    }

    /// <summary>
    /// Stream a message far larger than the output buffer: an 8-byte buffer with
    /// a flush sink that collects the bytes must produce exactly the same wire
    /// image as encoding into one large buffer.
    /// </summary>
    [Fact]
    public void StreamLargerThanBuffer()
    {
        const int n = 1000;

        // Reference: encode into a single big buffer.
        var big = new byte[n * 11 + 16];
        var reff = new OStream(big);
        for (int i = 0; i < n; i++)
        {
            reff.WriteUnsigned(i % int.MaxValue, (ulong)((long)i * 0x9E37_79B9L));
        }
        var reference = new byte[reff.BytesUsed];
        Array.Copy(big, reference, reff.BytesUsed);

        // Streamed: tiny 8-byte buffer + collecting sink.
        var collected = new MemoryStream();
        FlushSink sink = (data, off, len) => collected.Write(data, off, len);
        var os = new OStream(new byte[8], 0, sink);
        for (int i = 0; i < n; i++)
        {
            os.WriteUnsigned(i % int.MaxValue, (ulong)((long)i * 0x9E37_79B9L));
        }
        os.Flush(); // push the tail

        Assert.Equal(reference, collected.ToArray());
    }

    /// <summary>
    /// The same streamed bytes must decode back to the original values, proving
    /// the chunked-output path is wire-identical end to end.
    /// </summary>
    [Fact]
    public void StreamedMessageDecodes()
    {
        const int n = 500;
        var collected = new MemoryStream();
        FlushSink sink = (data, off, len) => collected.Write(data, off, len);
        var os = new OStream(new byte[8], 0, sink);
        for (int i = 0; i < n; i++)
        {
            os.WriteUnsigned(1, (ulong)i);
        }
        os.Flush();

        var v = new CollectVisitor();
        new IStream().Feed(collected.ToArray(), v);
        Assert.Equal(n, v.Values.Count);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal((ulong)i, v.Values[i]);
        }
    }

    /// <summary>A large blob fed to the decoder in 3-byte chunks reassembles correctly.</summary>
    [Fact]
    public void LargeBlobChunkedDecode()
    {
        var blob = new byte[1000];
        for (int i = 0; i < blob.Length; i++)
        {
            blob[i] = (byte)(i * 31 + 7);
        }
        var buf = new byte[2048];
        var os = new OStream(buf);
        os.WriteBlob(42, blob);
        int used = os.BytesUsed;

        var v = new ChunkedBlobVisitor(blob.Length);
        var iss = new IStream();
        for (int i = 0; i < used; i += 3)
        {
            iss.Feed(buf, i, Math.Min(3, used - i), v);
        }
        Assert.Equal(blob.Length, v.Seen);
        Assert.Equal(blob, v.Reassembled);
    }

    private sealed class ChunkedBlobVisitor : IVisitor
    {
        public readonly byte[] Reassembled;
        public int Seen;
        private readonly int _expectedTotal;

        public ChunkedBlobVisitor(int expectedTotal)
        {
            _expectedTotal = expectedTotal;
            Reassembled = new byte[expectedTotal];
        }

        public void Blob(int id, int total, int offset, byte[] d, int o, int l)
        {
            Assert.Equal(42, id);
            Assert.Equal(_expectedTotal, total);
            Array.Copy(d, o, Reassembled, offset, l);
            Seen += l;
        }
    }
}
