/*
 * SofaBuffers C# - CORELIB_PLAN 5.1: MIN_OUTPUT_BUFFER.
 *
 * The declared constant is the smallest buffer accepted *for streaming*. It
 * binds a buffer installed with a flush sink - at construction and at every
 * mid-stream buffer-set - and binds nothing else: a buffer installed without a
 * sink is subject to no minimum at all, because no flush can occur.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.IO;
using Xunit;

namespace SofaBuffers.Tests;

public class MinOutputBufferTests
{
    private static FlushSink Sink(MemoryStream produced) =>
        (d, o, l) => produced.Write(d, o, l);

    /// <summary>
    /// CORELIB_PLAN §5.1: the constant must exist, must be at least 1 and must
    /// not exceed 20. This port splits every atomic unit, so it declares 1.
    /// </summary>
    [Fact]
    public void DeclaredValueIsWithinTheSpecCeiling()
    {
        Assert.InRange(Sofab.MinOutputBuffer, 1, 20);
        Assert.Equal(1, Sofab.MinOutputBuffer);
    }

    /// <summary>
    /// §7.2 item 4: encode into a buffer of exactly <c>MIN_OUTPUT_BUFFER</c>
    /// bytes, driving the sink repeatedly, including a payload run longer than
    /// the buffer; the streamed bytes must equal the one-shot encoding.
    /// </summary>
    [Fact]
    public void EncodeAtExactlyTheMinimumMatchesTheOneShotOutput()
    {
        static void Message(OStream os)
        {
            os.WriteUnsigned(1, ulong.MaxValue);        // a 10-byte varint
            os.WriteString(2, new string('x', 300));    // a divisible run >> buffer
            os.WriteBlob(3, new byte[200]);
            os.WriteSigned(4, -7);
        }

        var one = new byte[1024];
        var direct = new OStream(one);
        Message(direct);
        byte[] expected = one[..direct.BytesUsed];

        var produced = new MemoryStream();
        var streamed = new OStream(new byte[Sofab.MinOutputBuffer], 0, Sink(produced));
        Message(streamed);
        streamed.Flush();

        Assert.Equal(expected, produced.ToArray());
    }

    /// <summary>
    /// §7.2 item 4: a buffer installed <b>with a sink</b> whose
    /// <c>buflen - offset</c> is one byte short of the minimum is rejected where
    /// it is handed over - by the same mechanism as an out-of-range offset -
    /// never partway through a message. For a port declaring 1 that is the
    /// zero-length case.
    /// </summary>
    [Fact]
    public void SinkInstalledBufferBelowTheMinimumIsRejectedAtHandover()
    {
        var produced = new MemoryStream();
        int shortOfMinimum = Sofab.MinOutputBuffer - 1;

        // At construction: an empty buffer, and a non-empty one whose offset
        // leaves fewer than MIN_OUTPUT_BUFFER bytes.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OStream(new byte[shortOfMinimum], 0, Sink(produced)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OStream(new byte[8], 8 - shortOfMinimum, Sink(produced)));

        // At a mid-stream buffer-set, before any byte of the message is written.
        var os = new OStream(new byte[8], 0, Sink(produced));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => os.BufferSet(new byte[shortOfMinimum], 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => os.BufferSet(new byte[8], 8 - shortOfMinimum));
        Assert.Equal(0, produced.Length); // rejected there, not partway through
    }

    /// <summary>
    /// §5.1: "A buffer installed without a sink is subject to no minimum." The
    /// converse of the rejection above - the same undersized buffer is accepted
    /// with no sink, and a message that fits encodes into it exactly.
    /// </summary>
    [Fact]
    public void SinklessBufferIsSubjectToNoMinimum()
    {
        // Below any declaration: accepted, and the first write reports buffer-full.
        var empty = new OStream(new byte[Sofab.MinOutputBuffer - 1]);
        Assert.Equal(0, empty.BytesUsed);
        Assert.Equal(
            SofabError.BufferFull,
            Assert.Throws<SofabException>(() => empty.WriteUnsigned(1, 0)).Error);

        // A two-byte message encodes into a two-byte buffer, whatever the port
        // declares: sizing from a bounded schema's MAX_SIZE stays exact.
        var exact = new byte[2];
        var os = new OStream(exact);
        os.WriteUnsigned(1, 1);
        Assert.Equal(2, os.BytesUsed);
        Assert.Equal(new byte[] { 0x08, 0x01 }, exact);

        // A sinkless buffer-set is unbound too.
        var later = new OStream(new byte[8]);
        later.BufferSet(new byte[Sofab.MinOutputBuffer - 1], 0);
        Assert.Equal(0, later.BytesUsed);
    }

    /// <summary>
    /// An offset outside the buffer is still rejected on both paths, and a null
    /// buffer is still an <see cref="ArgumentNullException"/>-shaped failure.
    /// </summary>
    [Fact]
    public void OffsetAndNullChecksStillApply()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OStream(new byte[8], 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OStream(new byte[8], -1));
        Assert.Throws<ArgumentException>(() => new OStream(null!));

        var os = new OStream(new byte[8]);
        Assert.Throws<ArgumentOutOfRangeException>(() => os.BufferSet(new byte[8], 9));
        Assert.Throws<ArgumentException>(() => os.BufferSet(null!, 0));
    }
}
