/*
 * SofaBuffers C# - varints at the 64-bit bound, in the positions the existing
 * suite does not reach.
 *
 * CORELIB_PLAN §4.1 bounds a varint at 64 bits: ten bytes at most, and the tenth
 * carries a single payload bit, so anything above 1 there is INVALID. This
 * decoder reads varints through three different readers -- an eight-bytes-at-a-
 * time one for the interior of a chunk, a per-byte one for the last nine bytes of
 * a chunk, and the resumable state machine for a varint cut by a feed boundary --
 * and the bound has to hold identically in all three. DecoderErrorsTests covers a
 * scalar VALUE at the end of a message, which is the per-byte reader; this file
 * covers the other two positions:
 *
 *   * a nine-byte varint with more bytes behind it (the interior reader's
 *     nine/ten-byte tail, which the end-of-message shape never reaches);
 *   * an over-bound ELEMENT of a compact array, decoded by the interior reader's
 *     own inlined copy of that tail;
 *   * an eleven-byte varint fed one byte at a time, which overruns the state
 *     machine's shift rather than its per-byte payload room.
 *
 * SPDX-License-Identifier: MIT
 */

using SofaBuffers.Tests.Common;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class VarintBoundaryTests
{
    private sealed class IgnoreVisitor : IVisitor
    {
    }

    [Fact]
    public void NineByteVarintWithBytesBehindItDecodes()
    {
        // 2^62 needs nine varint bytes (eight all-continuation, then 0x40). Put a
        // second field behind it so the value is NOT the last thing in the buffer:
        // that is what routes it through the interior reader, whose nine-byte tail
        // is otherwise never taken -- a value at the end of a message always has
        // fewer than ten bytes left and goes to the per-byte reader instead.
        const ulong big = 1UL << 62;
        byte[] wire = Encode(os =>
        {
            os.WriteUnsigned(1, big);
            os.WriteUnsigned(2, 5);
        });
        Assert.Equal(12, wire.Length); // 1 + 9 header/value, then 1 + 1

        var visitor = new RecordingVisitor();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, visitor));
        Assert.Equal(new[] { "u:1=" + big, "u:2=5" }, visitor.Events);
    }

    [Fact]
    public void OverBoundArrayElementRejected()
    {
        // An array element is decoded by the element loop, which carries its own
        // copy of the nine/ten-byte tail -- so the §4.1 bound has to be enforced
        // there too, not only on scalar values. id 1 unsigned array, count 1, an
        // element of ff*9 02 (the 65th bit set), then a trailing u64 field so the
        // element sits in the loop's bulk range rather than in its tail.
        byte[] wire = Bytes(
            0x0B, 0x01,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02,
            0x08, 0x01);

        var ex = Assert.Throws<SofabException>(() => new IStream().Feed(wire, new IgnoreVisitor()));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
    }

    [Fact]
    public void InBoundArrayElementInTheSamePositionDecodes()
    {
        // The control for the case above: the identical shape with the tenth byte
        // at its largest legal value (0x01 -> bit 63) is 2^64-1 and must decode,
        // so the rejection above is the bound firing and not the position.
        byte[] wire = Bytes(
            0x0B, 0x01,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01,
            0x08, 0x01);

        var visitor = new RecordingVisitor();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, visitor));
        Assert.Equal(
            new[] { "arr:1:UNSIGNED:1", "u:1=18446744073709551615", "u:1=1" },
            visitor.Events);
    }

    [Fact]
    public void ElevenByteVarintRejectedByteAtATime()
    {
        // ff*9 81: the first nine bytes fill all 64 payload bits and the tenth
        // then sets the continuation flag again, so an eleventh byte is promised.
        // Its payload bit is legal in isolation (1 -> bit 63), which is what makes
        // this distinct from the ff*9 02 case: the state machine catches it on the
        // shift having run out, not on the byte having bits it cannot place.
        byte[] wire = Bytes(0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x81);

        var iss = new IStream();
        var visitor = new IgnoreVisitor();
        var ex = Assert.Throws<SofabException>(() =>
        {
            foreach (byte b in wire)
            {
                iss.Feed(new[] { b }, visitor);
            }
        });
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
        Assert.Equal(DecodeStatus.Invalid, iss.Status);
    }

    [Fact]
    public void ArrayCountCutByAFeedBoundaryResumes()
    {
        // The count word of a compact integer array, cut after its first byte: the
        // fast path cannot finish the field and must leave no trace behind (no
        // ArrayBegin, no consumed state), so the byte machine can re-read the
        // field from its header on the next chunk.
        const int n = 200;                     // count 200 -> the word is 0xC8 0x01
        var src = new ulong[n];
        for (int i = 0; i < n; i++)
        {
            src[i] = (ulong)i;
        }
        byte[] wire = Encode(1024, os => os.WriteArrayUnsigned(1, src));
        Assert.Equal(Bytes(0x0B, 0xC8, 0x01), new[] { wire[0], wire[1], wire[2] });

        var visitor = new RecordingVisitor();
        var iss = new IStream();
        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(wire, 0, 2, visitor));
        Assert.Empty(visitor.Events);           // nothing announced on half a count
        Assert.Equal(DecodeStatus.Complete, iss.Feed(wire, 2, wire.Length - 2, visitor));

        Assert.Equal(n + 1, visitor.Events.Count);
        Assert.Equal("arr:1:UNSIGNED:" + n, visitor.Events[0]);
        Assert.Equal("u:1=0", visitor.Events[1]);
        Assert.Equal("u:1=" + (n - 1), visitor.Events[n]);
    }
}
