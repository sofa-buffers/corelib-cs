/*
 * SofaBuffers C# - OStream.Reset: one encoder reused across messages behaves
 * exactly like a freshly constructed one, and the reuse allocates nothing.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using Xunit;
using SofaBuffers.Tests.Common;

namespace SofaBuffers.Tests;

public class OStreamResetTests
{
    private static void Message(OStream os)
    {
        os.WriteUnsigned(0, 42);
        os.WriteSequenceBeginLazy(1);
        os.WriteSequenceBeginLazy(4);
        os.WriteSigned(2, -42);
        os.WriteSequenceEnd();
        os.WriteSequenceEnd();
        os.WriteSequenceBeginLazy(7);   // all-default: omitted by lazy framing
        os.WriteSequenceEnd();
        os.WriteString(3, "Hello");
    }

    private static byte[] Fresh()
    {
        var buf = new byte[64];
        var os = new OStream(buf);
        Message(os);
        return buf.AsSpan(0, os.BytesUsed).ToArray();
    }

    [Fact]
    public void AResetEncoderWritesTheSameBytesAsAFreshOne()
    {
        var buf = new byte[64];
        var os = new OStream(buf);
        Message(os);
        os.Reset(buf, 0);
        Message(os);
        Assert.Equal(Fresh(), buf.AsSpan(0, os.BytesUsed).ToArray());
    }

    [Fact]
    public void ResetDropsAMessageAbandonedMidSequence()
    {
        var buf = new byte[64];
        var os = new OStream(buf);
        // Abandoned with two open sequences, one of them still held back.
        os.WriteSequenceBeginLazy(1);
        os.WriteUnsigned(0, 1);
        os.WriteSequenceBeginLazy(2);
        os.Reset(buf, 0);
        Message(os);
        Assert.Equal(Fresh(), buf.AsSpan(0, os.BytesUsed).ToArray());
        // The abandoned depth is gone too: the full MAX_DEPTH (255) still opens,
        // which a stale depth of 2 would cut short, and the 256th is refused.
        // Held-back begins write nothing, so the buffer size plays no part.
        for (int i = 0; i < 255; i++)
        {
            os.WriteSequenceBeginLazy(1);
        }
        var e = Assert.Throws<SofabException>(() => os.WriteSequenceBeginLazy(1));
        Assert.Equal(SofabError.Argument, e.Error);
    }

    [Fact]
    public void ResetDropsAMessageAbandonedByBufferFull()
    {
        var small = new byte[4];
        var os = new OStream(small);
        os.WriteSequenceBeginLazy(1);
        Assert.Throws<SofabException>(() => os.WriteString(0, "far too long for four bytes"));
        var buf = new byte[64];
        os.Reset(buf, 0);
        Message(os);
        Assert.Equal(Fresh(), buf.AsSpan(0, os.BytesUsed).ToArray());
    }

    [Fact]
    public void ResetHonoursTheStartOffsetAndValidatesTheBuffer()
    {
        var buf = new byte[64];
        var os = new OStream(new byte[1]);
        os.Reset(buf, 3);
        Message(os);
        Assert.Equal(Fresh(), buf.AsSpan(3, os.BytesUsed - 3).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => os.Reset(buf, 65));
        Assert.Throws<ArgumentException>(() => os.Reset(null!, 0));
    }

    [Fact]
    public void AStreamingEncoderKeepsItsSinkAndItsMinimumAcrossAReset()
    {
        int flushed = 0;
        var os = new OStream(new byte[Sofab.MinOutputBuffer], 0, (b, o, l) => flushed += l);
        Assert.Throws<ArgumentOutOfRangeException>(() => os.Reset(new byte[Sofab.MinOutputBuffer - 1], 0));
        os.Reset(new byte[Sofab.MinOutputBuffer], 0);
        Message(os);
        os.Flush();
        Assert.Equal(Fresh().Length, flushed);
    }

    [Fact]
    public void ReusingAnEncoderAllocatesNothing()
    {
        var buf = new byte[64];
        Allocation.AssertNone(() => new OStream(buf), os =>
        {
            for (int i = 0; i < 4; i++)
            {
                os.Reset(buf, 0);
                Message(os);
            }
        });
    }
}
