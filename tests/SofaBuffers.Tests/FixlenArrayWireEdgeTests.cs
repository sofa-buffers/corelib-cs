/*
 * SofaBuffers C# - fixlen-array framing at the edges of its three varints.
 *
 * A fixlen array (CORELIB_PLAN §4.8) is the only field carrying three varints
 * before its payload -- field header, element count, then the fixlen_word that
 * names the element subtype -- and each of them can be multi-byte, can be cut by
 * a feed boundary, and can be out of range. The existing suite covers the
 * one-byte-count, one-byte-word shape that an encoder produces for a small array;
 * this file covers the rest:
 *
 *   * a count that needs more than one byte, whole and cut in half;
 *   * a fixlen_word naming a RESERVED subtype (0x4-0x7, §4.6) -- INVALID, and
 *     judged before ArrayBegin fires, so the receiver never sees the field;
 *   * a fixlen_word whose length is past FIXLEN_MAX (§6.2) -- INVALID.
 *
 * The tolerance twin -- a non-minimal fixlen_word, which §4.1 requires be
 * accepted -- lives in WireToleranceTests.
 *
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Generic;
using SofaBuffers.Tests.Common;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class FixlenArrayWireEdgeTests
{
    /// <summary>Records the array header and every element, so a long array stays assertable.</summary>
    private sealed class ArrayVisitor : IVisitor
    {
        public int Count = -1;
        public ArrayKind Kind = ArrayKind.Unsigned;
        public readonly List<double> Elements = new();

        public void ArrayBegin(int id, ArrayKind kind, int count)
        {
            Count = count;
            Kind = kind;
        }

        public void Fp32(int id, float value) => Elements.Add(value);

        public void Fp64(int id, double value) => Elements.Add(value);
    }

    /// <summary>200 elements: 0.5, 1.5, 2.5 ... -- exact in both float widths.</summary>
    private static float[] Sample()
    {
        var v = new float[200];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = i + 0.5f;
        }
        return v;
    }

    private static void AssertSample(ArrayVisitor visitor)
    {
        float[] expected = Sample();
        Assert.Equal(expected.Length, visitor.Count);
        Assert.Equal(ArrayKind.Fp32, visitor.Kind);
        Assert.Equal(expected.Length, visitor.Elements.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], visitor.Elements[i]);
        }
    }

    [Fact]
    public void MultiByteElementCountDecodesWhole()
    {
        // 200 elements -> the count word is 0xC8 0x01, so the decoder's array path
        // reads a multi-byte count with the whole message in hand.
        byte[] wire = Encode(1024, os => os.WriteArrayFp32(7, Sample()));
        Assert.Equal(Bytes(0x3D, 0xC8, 0x01, 0x20), new[] { wire[0], wire[1], wire[2], wire[3] });

        var visitor = new ArrayVisitor();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, visitor));
        AssertSample(visitor);
    }

    [Fact]
    public void MultiByteElementCountCutInHalfResumes()
    {
        // The same array, with the feed boundary INSIDE the count word: the fast
        // path cannot finish the field, so the byte machine re-reads it from the
        // header and the two halves must decode to exactly the whole-message
        // result. The declared count is announced once, after the fixlen_word.
        byte[] wire = Encode(1024, os => os.WriteArrayFp32(7, Sample()));

        var visitor = new ArrayVisitor();
        var iss = new IStream();
        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(wire, 0, 2, visitor)); // header + first count byte
        Assert.Equal(-1, visitor.Count);                                      // nothing announced yet
        Assert.Equal(DecodeStatus.Complete, iss.Feed(wire, 2, wire.Length - 2, visitor));
        AssertSample(visitor);
    }

    [Fact]
    public void ReservedSubtypeInAFixlenArrayWordRejected()
    {
        // fixlen array id 1, count 1, fixlen_word 0x04 -> subtype 4, reserved by
        // §4.6. INVALID, and -- per §4.8's ordering -- decided before ArrayBegin,
        // so the receiver is never handed a field it would have to un-announce.
        byte[] wire = Bytes(0x0D, 0x01, 0x04, 0x00, 0x00, 0x00, 0x00);

        var visitor = new ArrayVisitor();
        var ex = Assert.Throws<SofabException>(() => new IStream().Feed(wire, visitor));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
        Assert.Equal(-1, visitor.Count);
    }

    [Fact]
    public void FixlenArrayElementLengthAboveMaxRejected()
    {
        // The word's length sub-field is bounded by FIXLEN_MAX = INT32_MAX (§6.2).
        // 0x80 0x80 0x80 0x80 0x40 is (2^31 << 3) | FP32: one past the ceiling, so
        // INVALID -- and rejected on the word rather than after trying to size a
        // 2 GiB element.
        byte[] wire = Bytes(0x0D, 0x01, 0x80, 0x80, 0x80, 0x80, 0x40);

        var visitor = new ArrayVisitor();
        var ex = Assert.Throws<SofabException>(() => new IStream().Feed(wire, visitor));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
        Assert.Equal(-1, visitor.Count);
    }
}
