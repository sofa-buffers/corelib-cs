/*
 * SofaBuffers C# - a sequence-end marker's id is discarded, never validated.
 *
 * CORELIB_PLAN §4.9 makes the two directions asymmetric: an encoder MUST emit a
 * sequence end as exactly 0x07, while a decoder MUST accept a sequence-end
 * header (wire type 0b111) carrying *any* id, discard it, and re-encode the
 * marker as 0x07. §6.2 scopes the ID_MAX ceiling to value-bearing headers
 * (unsigned, signed, fixlen, the array types and sequence start) and names the
 * end marker as the exclusion, so an over-ID_MAX id on a sequence end is
 * normalized away rather than rejected -- test class 5b, tolerance (§7.2).
 *
 * SPDX-License-Identifier: MIT
 */

using SofaBuffers.Tests.Common;
using Xunit;

namespace SofaBuffers.Tests;

public class SequenceEndIdTests
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

    // The whole message in one Feed: the contiguous fast path (FastField).
    private static RecordingVisitor Decode(byte[] data)
    {
        var visitor = new RecordingVisitor();
        var stream = new IStream();
        Assert.Equal(DecodeStatus.Complete, stream.Feed(data, visitor));
        return visitor;
    }

    // One byte per Feed: the resumable state machine (StepIdle). Both surfaces
    // decode headers independently, so every case below is asserted on each --
    // §6.5's recurring defect is a guard fixed on one surface but not the other.
    private static RecordingVisitor DecodeByteAtATime(byte[] data)
    {
        var visitor = new RecordingVisitor();
        var stream = new IStream();
        foreach (byte b in data)
        {
            stream.Feed(new[] { b }, visitor);
        }
        Assert.Equal(DecodeStatus.Complete, stream.Status);
        return visitor;
    }

    private static SofabError ErrorOf(byte[] data)
    {
        var ex = Assert.Throws<SofabException>(() => new IStream().Feed(data, new RecordingVisitor()));
        return ex.Error;
    }

    private static SofabError ErrorOfByteAtATime(byte[] data)
    {
        var stream = new IStream();
        var visitor = new RecordingVisitor();
        var ex = Assert.Throws<SofabException>(() =>
        {
            foreach (byte b in data)
            {
                stream.Feed(new[] { b }, visitor);
            }
        });
        return ex.Error;
    }

    /// <summary>Re-encodes what it decodes, so the normalized bytes can be asserted.</summary>
    private sealed class ReEncodingVisitor : IVisitor
    {
        private readonly byte[] _buffer = new byte[64];

        public readonly OStream Out;

        public ReEncodingVisitor()
        {
            Out = new OStream(_buffer);
        }

        public void SequenceBegin(int id)
        {
            Out.WriteSequenceBeginLazy(id);
        }

        public void SequenceEnd()
        {
            // Keep: the frame is on the wire, so it must survive the round trip.
            Out.WriteSequenceEndKeep();
        }

        public byte[] Encoded()
        {
            var bytes = new byte[Out.BytesUsed];
            System.Array.Copy(_buffer, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }

    // The F-0054 isolate: id 14 opened as a sequence (0x76), closed by an end
    // marker whose id is 2^31 -- one past ID_MAX. The id is discarded, so this
    // is an ordinary balanced sequence.
    private static readonly byte[] OverIdMaxSeqEnd = Bytes(0x76, 0x87, 0x80, 0x80, 0x80, 0x40);

    [Fact]
    public void SeqEndIdAboveIdMaxAccepted()
    {
        Assert.Equal(new[] { "seq{:14", "seq}" }, Decode(OverIdMaxSeqEnd).Events);
    }

    [Fact]
    public void SeqEndIdAboveIdMaxAcceptedByteAtATime()
    {
        Assert.Equal(new[] { "seq{:14", "seq}" }, DecodeByteAtATime(OverIdMaxSeqEnd).Events);
    }

    [Fact]
    public void SeqEndIdAboveIdMaxReEncodesAsSingleByteMarker()
    {
        var visitor = new ReEncodingVisitor();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(OverIdMaxSeqEnd, visitor));
        // The five-byte end header collapses to the canonical 0x07 (§4.9).
        Assert.Equal(Bytes(0x76, 0x07), visitor.Encoded());
    }

    // The largest id a 64-bit header varint can carry on an end marker:
    // ff*9 01 is 2^64-1, whose low three bits are the sequence-end wire type.
    // §4.1's varint bound is what stops here; ID_MAX plays no part.
    [Fact]
    public void SeqEndIdAtVarintCeilingAccepted()
    {
        byte[] data = Bytes(0x76, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01);
        Assert.Equal(new[] { "seq{:14", "seq}" }, Decode(data).Events);
        Assert.Equal(new[] { "seq{:14", "seq}" }, DecodeByteAtATime(data).Events);
    }

    // Controls that already passed and must keep passing: id 0 (canonical),
    // a small non-zero id, and exactly ID_MAX.
    [Theory]
    [InlineData(new[] { 0x76, 0x07 })]                                     // id 0
    [InlineData(new[] { 0x76, 0x1F })]                                     // id 3
    [InlineData(new[] { 0x76, 0xFF, 0xFF, 0xFF, 0xFF, 0x3F })]             // id ID_MAX (2^31-1)
    public void SeqEndIdBelowCeilingStillAccepted(int[] values)
    {
        byte[] data = Bytes(values);
        Assert.Equal(new[] { "seq{:14", "seq}" }, Decode(data).Events);
        Assert.Equal(new[] { "seq{:14", "seq}" }, DecodeByteAtATime(data).Events);
    }

    // The other side of the carve-out: ID_MAX still binds a value-bearing
    // header. An id of 2^31 on an unsigned field is INVALID at the top level...
    [Fact]
    public void ValueBearingIdAboveIdMaxStillRejected()
    {
        byte[] data = Bytes(0x80, 0x80, 0x80, 0x80, 0x40);
        Assert.Equal(SofabError.InvalidMessage, ErrorOf(data));
        Assert.Equal(SofabError.InvalidMessage, ErrorOfByteAtATime(data));
    }

    // ...and equally inside a sequence being skipped, which is where the
    // F-0054 isolate puts its end marker.
    [Fact]
    public void ValueBearingIdAboveIdMaxStillRejectedInsideSequence()
    {
        byte[] data = Bytes(0x76, 0x80, 0x80, 0x80, 0x80, 0x40);
        Assert.Equal(SofabError.InvalidMessage, ErrorOf(data));
        Assert.Equal(SofabError.InvalidMessage, ErrorOfByteAtATime(data));
    }

    // A sequence start is value-bearing too (§6.2 lists it): its id reaches the
    // visitor, so the ceiling applies there as before.
    [Fact]
    public void SequenceStartIdAboveIdMaxStillRejected()
    {
        byte[] data = Bytes(0x86, 0x80, 0x80, 0x80, 0x40);
        Assert.Equal(SofabError.InvalidMessage, ErrorOf(data));
        Assert.Equal(SofabError.InvalidMessage, ErrorOfByteAtATime(data));
    }

    // An end marker with no open sequence stays INVALID whatever its id --
    // that is the one sequence-end condition §5.2 does enumerate.
    [Fact]
    public void DanglingSeqEndWithOverIdMaxIdStillRejected()
    {
        byte[] data = Bytes(0x87, 0x80, 0x80, 0x80, 0x40);
        Assert.Equal(SofabError.InvalidMessage, ErrorOf(data));
        Assert.Equal(SofabError.InvalidMessage, ErrorOfByteAtATime(data));
    }
}
