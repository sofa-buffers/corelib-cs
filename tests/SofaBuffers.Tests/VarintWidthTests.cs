/*
 * SofaBuffers C# - varint width and buffer-boundary coverage.
 *
 * The encoder assembles a varint of three to eight bytes as one 64-bit word and
 * the decoder takes it apart the same way, with separate paths for one and two
 * bytes and for the nine- and ten-byte encodings that spill past that word. The
 * length-dependent boundaries are therefore worth pinning down directly: every
 * width, from both directions, and split across a Feed boundary at every byte so
 * that the word-at-a-time path, the byte-at-a-time tail and the resumable state
 * machine all decode the same values.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class VarintWidthTests
{
    /// <summary>
    /// One value per varint width 1..10: the largest that still fits the width,
    /// and the smallest that needs it.
    /// </summary>
    public static IEnumerable<object[]> WidthBoundaries()
    {
        ulong v = 0;
        for (int width = 1; width <= 10; width++)
        {
            yield return new object[] { v };                      // smallest of this width
            ulong max = width == 10 ? ulong.MaxValue : (1UL << (7 * width)) - 1;
            yield return new object[] { max };                    // largest of this width
            v = max + 1;
        }
    }

    private sealed class Collector : IVisitor
    {
        public readonly List<(int Id, ulong Value)> Unsigneds = new();
        public readonly List<(int Id, long Value)> Signeds = new();

        public void Unsigned(int id, ulong value) => Unsigneds.Add((id, value));

        public void Signed(int id, long value) => Signeds.Add((id, value));
    }

    [Theory]
    [MemberData(nameof(WidthBoundaries))]
    public void UnsignedRoundTripsAtEveryWidth(ulong value)
    {
        byte[] wire = Encode(os => os.WriteUnsigned(3, value));

        var sink = new Collector();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, sink));
        Assert.Equal((3, value), Assert.Single(sink.Unsigneds));
    }

    [Theory]
    [MemberData(nameof(WidthBoundaries))]
    public void SignedRoundTripsAtEveryWidth(ulong raw)
    {
        // ZigZag maps the two halves of the signed range onto the same encodings,
        // so run the value both ways round.
        long value = unchecked((long)raw);
        byte[] wire = Encode(os => os.WriteSigned(4, value));

        var sink = new Collector();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, sink));
        Assert.Equal((4, value), Assert.Single(sink.Signeds));
    }

    [Theory]
    [MemberData(nameof(WidthBoundaries))]
    public void MinimalEncodingIsUsed(ulong value)
    {
        // §4.1: an encoder MUST emit the fewest bytes that represent the value.
        byte[] wire = Encode(os => os.WriteUnsigned(0, value));
        int payload = wire.Length - 1; // one header byte for id 0
        int expected = 1;
        for (ulong rest = value >> 7; rest != 0; rest >>= 7)
        {
            expected++;
        }
        Assert.Equal(expected, payload);
        Assert.Equal(0, wire[^1] & 0x80); // the last byte never continues
        if (payload > 1)
        {
            // A trailing 0x00 would be a continuation byte contributing only zero
            // high bits; only the single-byte encoding of 0 may end in 0x00.
            Assert.NotEqual(0, wire[^1]);
        }
    }

    [Theory]
    [MemberData(nameof(WidthBoundaries))]
    public void SplitAtEveryByteDecodesTheSame(ulong value)
    {
        byte[] wire = Encode(os => os.WriteUnsigned(9, value));

        for (int cut = 0; cut <= wire.Length; cut++)
        {
            var sink = new Collector();
            var istream = new IStream();
            istream.Feed(wire, 0, cut, sink);
            Assert.Equal(DecodeStatus.Complete, istream.Feed(wire, cut, wire.Length - cut, sink));
            Assert.Equal((9, value), Assert.Single(sink.Unsigneds));
        }
    }

    [Theory]
    [MemberData(nameof(WidthBoundaries))]
    public void ArrayElementRoundTripsAtEveryWidth(ulong value)
    {
        // Array elements are decoded by a loop of their own, separate from the
        // field path above, so they get the same width sweep.
        var elements = new ulong[] { 0, value, 1, value };
        byte[] wire = Encode(os => os.WriteArrayUnsigned(2, elements));

        for (int cut = 0; cut <= wire.Length; cut++)
        {
            var sink = new Collector();
            var istream = new IStream();
            istream.Feed(wire, 0, cut, sink);
            Assert.Equal(DecodeStatus.Complete, istream.Feed(wire, cut, wire.Length - cut, sink));
            Assert.Equal(elements.Length, sink.Unsigneds.Count);
            for (int i = 0; i < elements.Length; i++)
            {
                Assert.Equal((2, elements[i]), sink.Unsigneds[i]);
            }
        }
    }

    [Fact]
    public void FieldEndingExactlyAtTheBufferEndDecodes()
    {
        // The decoder only takes its word-at-a-time path when ten bytes are
        // readable, so a message whose last fields sit inside that margin exercises
        // the byte-at-a-time tail. Feed each prefix length that ends on a field.
        byte[] wire = Encode(os =>
        {
            os.WriteUnsigned(1, ulong.MaxValue);
            os.WriteUnsigned(2, 300);
            os.WriteUnsigned(3, 1);
        });

        var sink = new Collector();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, sink));
        Assert.Equal(3, sink.Unsigneds.Count);
        Assert.Equal((1, ulong.MaxValue), sink.Unsigneds[0]);
        Assert.Equal((2, 300UL), sink.Unsigneds[1]);
        Assert.Equal((3, 1UL), sink.Unsigneds[2]);
    }

    [Theory]
    [InlineData(2, 3)]   // runs past the end
    [InlineData(-1, 1)]  // negative offset
    [InlineData(0, -1)]  // negative length
    [InlineData(5, 0)]   // offset past the end
    [InlineData(0, 5)]   // length past the end
    public void FeedRejectsASliceOutsideTheArray(int off, int len)
    {
        // The decode loops read the slice without a per-byte bounds check, so the
        // slice itself is validated once on entry.
        var sink = new Collector();
        var data = new byte[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => new IStream().Feed(data, off, len, sink));
    }

    [Fact]
    public void FeedRejectsANullBuffer()
    {
        Assert.Throws<ArgumentNullException>(() => new IStream().Feed(null!, 0, 0, new Collector()));
    }

    [Fact]
    public void FeedAcceptsAnInteriorSlice()
    {
        // The framing counterpart of the rejections above: a valid sub-range is
        // decoded, and only that range.
        byte[] wire = Encode(os => os.WriteUnsigned(1, 300));
        var padded = new byte[wire.Length + 4];
        Array.Copy(wire, 0, padded, 2, wire.Length);

        var sink = new Collector();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(padded, 2, wire.Length, sink));
        Assert.Equal((1, 300UL), Assert.Single(sink.Unsigneds));
    }
}
