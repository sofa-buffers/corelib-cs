/*
 * SofaBuffers C# - the finish-less three-valued decode outcome (MESSAGE_SPEC §7):
 * COMPLETE (ends at a field boundary), INCOMPLETE (ends inside a field / with an
 * open sequence -- a truncation, not an error), and INVALID (malformed, thrown).
 *
 * SPDX-License-Identifier: MIT
 */

using Xunit;
using SofaBuffers.Tests.Common;

namespace SofaBuffers.Tests;

public class IncompleteOutcomeTests
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

    // --- the three focused outcomes -----------------------------------------

    [Fact]
    public void CompleteMessageReportsComplete()
    {
        // A whole unsigned scalar (id 0, value 42) ends exactly at a boundary.
        var v = new RecordingVisitor();
        DecodeStatus status = new IStream().Feed(Bytes(0x00, 0x2A), v);
        Assert.Equal(DecodeStatus.Complete, status);
        Assert.Equal(new[] { "u:0=42" }, v.Events);
    }

    [Fact]
    public void LoneContinuationByteReportsIncomplete()
    {
        // A single 0x80: a header varint whose continuation bit is set with no
        // terminating byte. This is neither a clean COMPLETE return nor a
        // SofabException -- it is INCOMPLETE (more bytes could finish it).
        var v = new RecordingVisitor();
        var iss = new IStream();
        DecodeStatus status = iss.Feed(Bytes(0x80), v);
        Assert.Equal(DecodeStatus.Incomplete, status);
        Assert.Equal(DecodeStatus.Incomplete, iss.Status);
        Assert.Empty(v.Events); // nothing decoded yet, but nothing rejected
    }

    [Fact]
    public void VarintOverflowStillThrowsInvalid()
    {
        // 10 continuation bytes overflow the 64-bit value: malformed regardless of
        // what follows -> INVALID, thrown (never returned as a DecodeStatus).
        byte[] bad = Bytes(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        var ex = Assert.Throws<SofabException>(() => new IStream().Feed(bad, new RecordingVisitor()));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
    }

    // --- INCOMPLETE covers every mid-field / open-sequence shape ------------

    [Fact]
    public void TruncatedValueVarintReportsIncomplete()
    {
        // Unsigned scalar header (0x00) then a value byte with the continuation
        // bit set (0x80) and no terminator: ends inside the value varint.
        var v = new RecordingVisitor();
        DecodeStatus status = new IStream().Feed(Bytes(0x00, 0x80), v);
        Assert.Equal(DecodeStatus.Incomplete, status);
        Assert.Empty(v.Events);
    }

    [Fact]
    public void TruncatedFixlenPayloadReportsIncomplete()
    {
        // fp32 field (0x02, 0x20) declares 4 payload bytes but only 2 are present.
        var v = new RecordingVisitor();
        DecodeStatus status = new IStream().Feed(Bytes(0x02, 0x20, 0x56, 0x0E), v);
        Assert.Equal(DecodeStatus.Incomplete, status);
    }

    [Fact]
    public void TruncatedStringPayloadReportsIncomplete()
    {
        // String id 0, length 3 ("Hel...") but only 2 bytes delivered.
        var v = new RecordingVisitor();
        DecodeStatus status = new IStream().Feed(Bytes(0x02, 0x1A, 0x48, 0x65), v);
        Assert.Equal(DecodeStatus.Incomplete, status);
    }

    [Fact]
    public void ArrayMissingElementsReportsIncomplete()
    {
        // Unsigned array id 0, count 3, but only one element present.
        var v = new RecordingVisitor();
        DecodeStatus status = new IStream().Feed(Bytes(0x03, 0x03, 0x01), v);
        Assert.Equal(DecodeStatus.Incomplete, status);
    }

    [Fact]
    public void OpenSequenceReportsIncomplete()
    {
        // A SEQUENCE_START (0x06) with no matching SEQUENCE_END: the message stops
        // inside an open sequence -> INCOMPLETE, not COMPLETE and not a throw.
        var v = new RecordingVisitor();
        DecodeStatus status = new IStream().Feed(Bytes(0x06), v);
        Assert.Equal(DecodeStatus.Incomplete, status);
        Assert.Equal(new[] { "seq{:0" }, v.Events); // begin was emitted
    }

    [Fact]
    public void ClosedSequenceReportsComplete()
    {
        // SEQUENCE_START then SEQUENCE_END: balanced, so back at a boundary.
        var v = new RecordingVisitor();
        DecodeStatus status = new IStream().Feed(Bytes(0x06, 0x07), v);
        Assert.Equal(DecodeStatus.Complete, status);
    }

    [Fact]
    public void IncompleteThenRemainderReportsComplete()
    {
        // Split a whole message across two Feeds: the first chunk is INCOMPLETE,
        // the second finishes it -> COMPLETE. Proves INCOMPLETE is resumable, not
        // a rejection.
        var v = new RecordingVisitor();
        var iss = new IStream();
        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(Bytes(0x00, 0x80), v));
        Assert.Equal(DecodeStatus.Complete, iss.Feed(Bytes(0x01), v)); // value = 128
        Assert.Equal(new[] { "u:0=128" }, v.Events);
    }

    [Fact]
    public void StatusOnFreshDecoderIsComplete()
    {
        // A decoder that has consumed nothing rests at a boundary (the empty
        // message is COMPLETE, §7).
        Assert.Equal(DecodeStatus.Complete, new IStream().Status);
    }
}
