/*
 * SofaBuffers C# - a field header cut by a feed boundary.
 *
 * The decoder has two readers for a field header: the fast path, which needs the
 * whole header in one chunk, and the resumable state machine, which takes it a
 * byte at a time. A one-byte header can never be split, so every existing test
 * that feeds byte-at-a-time still reaches the fast path for its headers -- the
 * state machine's header branches are only entered by a MULTI-byte header that a
 * chunk boundary actually cuts (id >= 16, or a non-minimal spelling).
 *
 * That matters because the header is where two of §5.2's INVALID conditions are
 * decided: an id past ID_MAX (§6.2) and nesting past MAX_DEPTH (§4.9). A decoder
 * that judges them on one path and not the other reports a different outcome for
 * the same bytes depending on how they were chunked, which §5.2 forbids and
 * §7 item 4 tests for ("feeding one byte at a time must be identical to feeding
 * it all at once").
 *
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Generic;
using SofaBuffers.Tests.Common;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class SplitHeaderTests
{
    /// <summary>255 sequence-start markers: the deepest legal nesting (§4.9).</summary>
    private static byte[] MaxDepthPrefix()
    {
        var b = new byte[255];
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = 0x06;
        }
        return b;
    }

    [Fact]
    public void MaxDepthOnASplitHeaderRejected()
    {
        // 255 opens are legal; the 256th is not (§4.9). Spell that 256th open with
        // a two-byte header (id 16, wire type 6) and cut it in half, so the depth
        // ceiling is judged by the state machine rather than the fast path.
        var wire = new List<byte>(MaxDepthPrefix()) { 0x86, 0x01 };

        var iss = new IStream();
        var visitor = new RecordingVisitor();
        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(MaxDepthPrefix(), visitor));
        Assert.Equal(255, visitor.Events.Count);          // 255 opens, all accepted
        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(new byte[] { 0x86 }, visitor));

        var ex = Assert.Throws<SofabException>(() => iss.Feed(new byte[] { 0x01 }, visitor));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
        Assert.Equal(255, visitor.Events.Count);          // the 256th never announced

        // Whole, the same bytes are rejected by the fast path.
        var whole = Assert.Throws<SofabException>(
            () => new IStream().Feed(wire.ToArray(), new RecordingVisitor()));
        Assert.Equal(SofabError.InvalidMessage, whole.Error);
    }

    [Fact]
    public void IdAboveMaxOnASplitHeaderRejected()
    {
        // (2^31 << 3) | 0: an id one past ID_MAX (§6.2), spelled over five bytes
        // and fed one at a time, so the state machine judges it.
        byte[] wire = Bytes(0x80, 0x80, 0x80, 0x80, 0x40);

        var iss = new IStream();
        var visitor = new RecordingVisitor();
        var ex = Assert.Throws<SofabException>(() =>
        {
            foreach (byte b in wire)
            {
                iss.Feed(new[] { b }, visitor);
            }
        });
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
        Assert.Empty(visitor.Events);
    }

    [Fact]
    public void FixlenWordCutAfterAReservedFirstByteIsIncomplete()
    {
        // §7 item 6: a fixlen_word cut after its first byte, with that byte
        // carrying a RESERVED subtype (0x4-0x7, §4.6) in its low three bits. The
        // subtype looks settled -- an implementation that evaluates the word's
        // sub-fields before the varint ends answers INVALID here -- but §4.1 gives
        // a varint no value before its final byte, so the only correct answer
        // while the word is unfinished is INCOMPLETE.
        var iss = new IStream();
        var visitor = new RecordingVisitor();
        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(Bytes(0x0A, 0x84), visitor));
        Assert.Empty(visitor.Events);

        // Completing the word with 0x00 makes it the value 4 -- subtype 4, which is
        // reserved -- and only now is it INVALID.
        var ex = Assert.Throws<SofabException>(() => iss.Feed(new byte[] { 0x00 }, visitor));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);

        // The control that keeps the case above honest: an equally unfinished word
        // whose first byte also has bits in the subtype slot, but which completes
        // to a LEGAL subtype, must decode. 0x9A 0x00 is the word 0x1A =
        // (3 << 3) | STRING.
        var ok = new RecordingVisitor();
        var stream = new IStream();
        Assert.Equal(DecodeStatus.Incomplete, stream.Feed(Bytes(0x0A, 0x9A), ok));
        Assert.Equal(DecodeStatus.Complete, stream.Feed(Bytes(0x00, 0x61, 0x62, 0x63), ok));
        Assert.Equal(new[] { "str:1=abc" }, ok.Events);
    }
}
