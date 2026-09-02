/*
 * SofaBuffers C# - INVALID is terminal: once the decoder has rejected the bytes
 * it has seen, that verdict is latched (CORELIB_PLAN §5.2, issue #57).
 *
 * The §5.2 outcome table marks INVALID "no -- terminal", and the precedence
 * paragraph forbids reporting INCOMPLETE (or, a fortiori, COMPLETE) for input
 * already determined to be malformed. So after a Feed has thrown
 * SofabException(InvalidMessage):
 *
 *   - Status reports DecodeStatus.Invalid, not Complete / Incomplete;
 *   - any further Feed throws again, consumes nothing and emits no callback.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using SofaBuffers.Tests.Common;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class InvalidLatchTests
{
    /// <summary>A whole, well-formed message: unsigned field id 0 = 42.</summary>
    private static byte[] Good() => Bytes(0x00, 0x2A);

    /// <summary>
    /// Feed <paramref name="malformed"/>, assert it is rejected, then assert the
    /// verdict is terminal: a following Feed of a perfectly good message throws
    /// the same InvalidMessage again without emitting anything. The throw is the
    /// whole report — there is no status accessor to cross-check it against.
    /// </summary>
    private static void AssertLatched(byte[] malformed)
    {
        var visitor = new RecordingVisitor();
        var iss = new IStream();

        var first = Assert.Throws<SofabException>(() => iss.Feed(malformed, visitor));
        Assert.Equal(SofabError.InvalidMessage, first.Error);

        int seen = visitor.Events.Count;
        var again = Assert.Throws<SofabException>(() => iss.Feed(Good(), visitor));
        Assert.Equal(SofabError.InvalidMessage, again.Error);
        Assert.Equal(seen, visitor.Events.Count); // no callback from the resumed feed

        // Still latched after the second rejection, and on the slice overload too.
        var third = Assert.Throws<SofabException>(
            () => iss.Feed(Good(), 0, 2, visitor));
        Assert.Equal(SofabError.InvalidMessage, third.Error);
        Assert.Equal(seen, visitor.Events.Count);
    }

    /// <summary>Same, but the malformed bytes arrive one at a time (byte machine).</summary>
    private static void AssertLatchedByteAtATime(byte[] malformed)
    {
        var visitor = new RecordingVisitor();
        var iss = new IStream();

        var first = Assert.Throws<SofabException>(() =>
        {
            foreach (byte b in malformed)
            {
                iss.Feed(new[] { b }, visitor);
            }
        });
        Assert.Equal(SofabError.InvalidMessage, first.Error);

        int seen = visitor.Events.Count;
        var again = Assert.Throws<SofabException>(() => iss.Feed(Good(), visitor));
        Assert.Equal(SofabError.InvalidMessage, again.Error);
        Assert.Equal(seen, visitor.Events.Count);
    }

    // --- one case per malformed-input branch of DecoderErrorsTests ------------

    [Fact]
    public void IdAboveMaxLatches() =>
        AssertLatched(Bytes(0x80, 0x80, 0x80, 0x80, 0x40));

    [Fact]
    public void ReservedFixlenTypeLatches() =>
        AssertLatched(Bytes(0x02, 0x04));

    [Fact]
    public void Fp32WrongLengthLatches() =>
        AssertLatched(Bytes(0x02, 0x28));

    [Fact]
    public void Fp64WrongLengthLatches() =>
        AssertLatched(Bytes(0x02, 0x21));

    [Fact]
    public void StringAsFixlenArrayElementLatches() =>
        AssertLatched(Bytes(0x05, 0x01, 0x0A));

    [Fact]
    public void FixlenLengthAboveMaxLatches() =>
        AssertLatched(Bytes(0x02, 0x82, 0x80, 0x80, 0x80, 0x40));

    [Fact]
    public void ArrayCountAboveMaxLatches() =>
        AssertLatched(Bytes(0x03, 0x80, 0x80, 0x80, 0x80, 0x40));

    [Fact]
    public void OverlongVarintLatches() =>
        AssertLatched(Bytes(0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02));

    [Fact]
    public void DanglingSequenceEndLatches() =>
        AssertLatched(Bytes(0x07));

    [Fact]
    public void OverlongVarintLatchesByteAtATime() =>
        AssertLatchedByteAtATime(
            Bytes(0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02));

    [Fact]
    public void ReservedFixlenTypeLatchesByteAtATime() =>
        AssertLatchedByteAtATime(Bytes(0x02, 0x04));

    [Fact]
    public void DanglingSequenceEndLatchesByteAtATime() =>
        // 87 00: a (non-minimal, §4.1-legal) two-byte header varint = 7, so the
        // header is split across the two feeds and the byte machine decodes it.
        AssertLatchedByteAtATime(Bytes(0x87, 0x00));

    // --- the malformed bytes may be malformed AND truncated (§5.2 precedence) --

    [Fact]
    public void MalformedAndTruncatedStaysInvalidNotIncomplete()
    {
        // Nested fp64 whose fixlen_word declares length 11 != 8, then truncates:
        // the word alone proves the message malformed, so the verdict is INVALID
        // and stays INVALID -- never re-reported as INCOMPLETE. The second Feed is
        // the check that matters: a decoder that had downgraded the verdict to
        // "truncated" would return Incomplete there instead of throwing.
        var visitor = new RecordingVisitor();
        var iss = new IStream();
        var first = Assert.Throws<SofabException>(
            () => iss.Feed(Bytes(0x56, 0x0A, 0x59), visitor));
        Assert.Equal(SofabError.InvalidMessage, first.Error);
        var again = Assert.Throws<SofabException>(
            () => iss.Feed(Bytes(0x59), visitor));
        Assert.Equal(SofabError.InvalidMessage, again.Error);
    }

    // --- the latch survives a partially decoded prefix ------------------------

    [Fact]
    public void FieldsBeforeTheBadOneAreKeptButTheStreamIsDead()
    {
        var visitor = new RecordingVisitor();
        var iss = new IStream();

        // A good field, then a reserved fixlen subtype.
        Assert.Throws<SofabException>(
            () => iss.Feed(Bytes(0x00, 0x2A, 0x02, 0x04), visitor));
        Assert.Equal(new[] { "u:0=42" }, visitor.Events);

        Assert.Throws<SofabException>(() => iss.Feed(Good(), visitor));
        Assert.Equal(new[] { "u:0=42" }, visitor.Events);
    }

    // --- a visitor that raises the INVALID outcome latches too ----------------

    [Fact]
    public void VisitorRaisedInvalidMessageLatches()
    {
        // Generated decode code raises InvalidMessage from a callback -- e.g. a
        // strict-UTF-8 string payload (§6.4) or a schema bound (MESSAGE_SPEC
        // §7.1). That is the same terminal INVALID outcome, so it latches.
        var iss = new IStream();
        var visitor = new ThrowingVisitor();
        Assert.Throws<SofabException>(() => iss.Feed(Good(), visitor));
        Assert.Throws<SofabException>(() => iss.Feed(Good(), new RecordingVisitor()));
    }

    private sealed class ThrowingVisitor : IVisitor
    {
        public void Unsigned(int id, ulong value) =>
            throw new SofabException(SofabError.InvalidMessage, "schema bound");
    }

    // --- what does NOT latch --------------------------------------------------

    [Fact]
    public void IncompleteDoesNotLatch()
    {
        // A truncated (but well-formed) prefix keeps the stream alive: the next
        // chunk completes it. INCOMPLETE is not an error (§5.2).
        var visitor = new RecordingVisitor();
        var iss = new IStream();
        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(Bytes(0x00), visitor));
        Assert.Equal(DecodeStatus.Complete, iss.Feed(Bytes(0x2A), visitor));
        Assert.Equal(new[] { "u:0=42" }, visitor.Events);
    }

    [Fact]
    public void ArgumentErrorsDoNotLatch()
    {
        // A bad slice is a caller mistake about this call, not a verdict on the
        // stream: the decoder's own state is untouched and decoding continues.
        var visitor = new RecordingVisitor();
        var iss = new IStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => iss.Feed(Good(), 1, 5, visitor));
        Assert.Throws<ArgumentNullException>(() => iss.Feed(null!, 0, 0, visitor));
        Assert.Equal(DecodeStatus.Complete, iss.Feed(Good(), visitor));
        Assert.Equal(new[] { "u:0=42" }, visitor.Events);
    }

    [Fact]
    public void LimitExceededIsTerminalButNotInvalid()
    {
        // §6.2.1 / §6.3: a receiver-side cap is a terminal policy rejection, but
        // the bytes are well-formed -- it must NOT be folded into the INVALID
        // decode outcome. So the stream is closed to further feeds, and the
        // refusal reaches the caller as LimitExceeded on the error channel --
        // never as InvalidMessage, and never as the Invalid outcome.
        var iss = new IStream();
        var ex = Assert.Throws<SofabException>(() => iss.Feed(Good(), new LimitVisitor()));
        Assert.Equal(SofabError.LimitExceeded, ex.Error);
        Assert.NotEqual(SofabError.InvalidMessage, ex.Error);

        var again = Assert.Throws<SofabException>(() => iss.Feed(Good(), new RecordingVisitor()));
        Assert.Equal(SofabError.LimitExceeded, again.Error);
        Assert.NotEqual(SofabError.InvalidMessage, again.Error);
    }

    private sealed class LimitVisitor : IVisitor
    {
        public void Unsigned(int id, ulong value) =>
            throw new SofabException(SofabError.LimitExceeded, "max_dyn_array_count");
    }
}
