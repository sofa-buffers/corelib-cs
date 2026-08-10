/*
 * SofaBuffers C# - the encoder and decoder must not change what they produce
 * near a buffer end.
 *
 * Both codecs keep a wide "there is room for anything" fast path and a narrow
 * fallback, and which one a given field takes depends only on how much of the
 * caller's buffer happens to be left. That is a property of the buffer, never of
 * the message, so the two paths have to agree byte for byte and event for event
 * at every position the switch can happen at -- including the exactly-sized
 * buffer the generator's one-shot Encode() allocates, where the tail of every
 * message is written by the fallback.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using Xunit;

using SofaBuffers.Tests.Common;

namespace SofaBuffers.Tests;

public class BufferBoundaryTests
{
    /// <summary>
    /// A message exercising every varint width (1..10 bytes), both float widths,
    /// a string, a blob, both array kinds and a nested sequence, so that whatever
    /// field lands against the buffer end is a different one for each size below.
    /// </summary>
    private static void Message(OStream os)
    {
        os.WriteUnsigned(1, 0);                       // 1-byte varint
        os.WriteUnsigned(2, 0x7F);
        os.WriteUnsigned(3, 0x80);                    // 2-byte
        os.WriteUnsigned(4, 0x3FFF_FFFF);             // 5-byte
        os.WriteUnsigned(5, ulong.MaxValue);          // 10-byte
        os.WriteSigned(6, -1);
        os.WriteSigned(7, long.MinValue);             // 10-byte zigzag
        os.WriteBoolean(8, true);
        os.WriteFp32(9, 3.5f);
        os.WriteFp64(10, -2.25);
        os.WriteString(11, "sofab");
        os.WriteBlob(12, new byte[] { 1, 2, 3, 0xFF });
        os.WriteArrayUnsigned(13, new ushort[] { 1, 200, 40000 });
        os.WriteArraySigned(14, new long[] { -1, long.MaxValue });
        os.WriteArrayFp32(15, new[] { 1.5f, -0.5f });
        os.WriteSequenceBeginLazy(16);
        os.WriteUnsigned(1, 99);
        os.WriteSigned(2, -7);
        os.WriteSequenceEnd();
    }

    private static byte[] Reference()
    {
        var buf = new byte[512];
        var os = new OStream(buf);
        Message(os);
        return buf[..os.BytesUsed];
    }

    /// <summary>
    /// The encoding must not depend on how much slack the output buffer has.
    /// A buffer sized to exactly the message -- what a bounded schema's
    /// <c>MaxSize</c> gives the generated one-shot <c>Encode()</c> -- leaves the
    /// last fields with less headroom than the wide fast path wants, so each
    /// size below hands a different field to the fallback writer.
    /// </summary>
    [Fact]
    public void EncodingIsIdenticalAtEveryBufferSizeFromTheExactFitUp()
    {
        byte[] expected = Reference();
        for (int size = expected.Length; size <= expected.Length + 40; size++)
        {
            var buf = new byte[size];
            var os = new OStream(buf);
            Message(os);
            Assert.Equal(expected.Length, os.BytesUsed);
            Assert.Equal(expected, buf[..os.BytesUsed]);
        }
    }

    /// <summary>
    /// One byte short of the message, with no sink, is <c>BufferFull</c> -- not a
    /// silent truncation and not any other error, at any shortfall.
    /// </summary>
    [Fact]
    public void ABufferShorterThanTheMessageReportsBufferFull()
    {
        int need = Reference().Length;
        for (int size = 0; size < need; size++)
        {
            var os = new OStream(new byte[size]);
            var e = Assert.Throws<SofabException>(() => Message(os));
            Assert.Equal(SofabError.BufferFull, e.Error);
        }
    }

    /// <summary>
    /// Decoding must not depend on where the input was cut. Splitting the same
    /// message at every byte offset moves the fast path's "a whole field is
    /// present" boundary across every field in turn, and each split must produce
    /// the same events and end <c>Complete</c>.
    /// </summary>
    [Fact]
    public void DecodingIsIdenticalAtEverySplitPoint()
    {
        byte[] wire = Reference();

        var whole = new RecordingVisitor();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, whole));
        List<string> expected = whole.Events;
        Assert.NotEmpty(expected);

        for (int split = 0; split <= wire.Length; split++)
        {
            var v = new RecordingVisitor();
            var istream = new IStream();
            istream.Feed(wire, 0, split, v);
            Assert.Equal(DecodeStatus.Complete, istream.Feed(wire, split, wire.Length - split, v));
            Assert.Equal(expected, v.Events);
        }
    }

    /// <summary>
    /// The same, one byte per <c>Feed</c>: every field header, value word and
    /// payload run is then split, which is the only way the byte-at-a-time
    /// machine decodes a whole message on its own.
    /// </summary>
    [Fact]
    public void DecodingOneByteAtATimeYieldsTheSameEvents()
    {
        byte[] wire = Reference();

        var whole = new RecordingVisitor();
        new IStream().Feed(wire, whole);

        var v = new RecordingVisitor();
        var istream = new IStream();
        DecodeStatus status = DecodeStatus.Complete;
        for (int i = 0; i < wire.Length; i++)
        {
            status = istream.Feed(wire, i, 1, v);
        }
        Assert.Equal(DecodeStatus.Complete, status);
        Assert.Equal(whole.Events, v.Events);
    }
}
