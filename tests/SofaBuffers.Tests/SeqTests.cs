/*
 * SofaBuffers C# - the array-growth policy generated decode destinations are
 * filled through: sofab.Seq (MESSAGE_SPEC §7.1, CORELIB_PLAN §6.2.1).
 *
 * An array's element count arrives from the wire. Until a schema `count` or a
 * receiver limit bounds it, it is bounded by nothing: sizing the destination from
 * it hands a peer a multi-gigabyte allocation for a three-byte header. So the
 * destination starts at Seq.ArrayInitCap and grows against elements that have
 * ACTUALLY ARRIVED, with the count acting only as the ceiling.
 *
 * That policy was never testable while it lived in generated code -- it was
 * asserted as a substring of the emitted text, and could not be called with an
 * index near int.MaxValue at all. These are the cases that were out of reach:
 * growth from empty, the doubling step, growth stopping exactly at an honest
 * count, and an announced count near 2^31 allocating nothing.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using Xunit;

namespace SofaBuffers.Tests;

public class SeqTests
{
    [Fact]
    public void ArrayInitCapIsTheBoundedFirstReservation()
    {
        // The value generated code reserves with: `new T[Math.Min(count, cap)]`.
        Assert.Equal(16, Seq.ArrayInitCap);
    }

    [Fact]
    public void AnIndexThatFitsLeavesTheArrayUntouched()
    {
        var a = new int[4];
        // Identity, not just equality: the call is on the hot path of every array
        // element, so the common case must not copy.
        Assert.Same(a, Seq.EnsureCap(a, 0, 4));
        Assert.Same(a, Seq.EnsureCap(a, 3, 4));
    }

    [Fact]
    public void GrowthDoublesAndKeepsWhatWasThere()
    {
        var a = new int[] { 1, 2, 3, 4 };
        int[] grown = Seq.EnsureCap(a, 4, 1000);

        Assert.Equal(8, grown.Length);
        Assert.Equal(new[] { 1, 2, 3, 4, 0, 0, 0, 0 }, grown);
    }

    [Fact]
    public void GrowthStartsFromAnEmptyArray()
    {
        // Doubling zero is zero, so the index decides the first length. Generated
        // code reserves ArrayInitCap up front, but a zero-length destination (an
        // array field whose declared count is 0, or a shared empty initializer) is
        // a legitimate starting point and must not spin.
        int[] a = Array.Empty<int>();
        a = Seq.EnsureCap(a, 0, 8);
        Assert.Single(a);
        a = Seq.EnsureCap(a, 1, 8);
        Assert.Equal(2, a.Length);
        a = Seq.EnsureCap(a, 2, 8);
        Assert.Equal(4, a.Length);
    }

    [Fact]
    public void GrowthJumpsToTheIndexWhenDoublingIsNotEnough()
    {
        // A gap in the element ids (an interior element equal to the element
        // default is omitted, MESSAGE_SPEC §2) lands an index far past the current
        // length: one growth step has to cover it.
        int[] grown = Seq.EnsureCap(new int[4], 100, int.MaxValue);
        Assert.Equal(101, grown.Length);
    }

    [Fact]
    public void GrowthStopsExactlyAtTheAnnouncedCount()
    {
        // A single step never overshoots the ceiling...
        Assert.Equal(20, Seq.EnsureCap(new int[16], 16, 20).Length);

        // ...and a full fill of an honest array ends exactly right-sized, rather
        // than at the next power of two: 100 elements announced, 100 delivered.
        const int count = 100;
        var a = new int[Math.Min(count, Seq.ArrayInitCap)];
        for (int i = 0; i < count; i++)
        {
            a = Seq.EnsureCap(a, i, count);
            a[i] = i;
        }

        Assert.Equal(count, a.Length);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, a[i]);
        }
    }

    [Fact]
    public void AnAnnouncedCountNearTwoToThe31AllocatesNothing()
    {
        // The adversarial case: a header announcing int.MaxValue elements and
        // delivering three. The destination is the bounded reservation, and the
        // count only ever bounds growth from above -- so what is allocated tracks
        // what arrived, and stays at 16 entries.
        const int announced = int.MaxValue;
        var a = new int[Math.Min(announced, Seq.ArrayInitCap)];
        Assert.Equal(Seq.ArrayInitCap, a.Length);

        for (int i = 0; i < 3; i++)
        {
            a = Seq.EnsureCap(a, i, announced);
        }
        Assert.Equal(Seq.ArrayInitCap, a.Length);

        // And the first real growth step is one doubling, not the announcement.
        Assert.Equal(2 * Seq.ArrayInitCap, Seq.EnsureCap(a, Seq.ArrayInitCap, announced).Length);
    }

    [Fact]
    public void AnIndexNearTwoToThe31IsClampedRatherThanOverflowing()
    {
        // index + 1 is int.MaxValue here: computed in int it would be fine, but a
        // doubling of a large length computed in int would not -- it would come
        // back negative and hand out an array SHORTER than the one passed in.
        // Everything is done in long, so the ceiling is what decides.
        int[] grown = Seq.EnsureCap(new int[16], int.MaxValue - 1, 32);
        Assert.Equal(32, grown.Length);
    }

    [Fact]
    public void TheCapIsACeilingOnTheResult()
    {
        // A cap below the index asked for is not a shape generated code produces:
        // an element past the announced count is not delivered (the fill is armed
        // with that count) and an element past a schema capacity is rejected at
        // ArrayBegin (§7.1). Pinned anyway, because it is what "ceiling" means --
        // the ceiling wins, and the caller is the one holding the bound.
        Assert.Equal(6, Seq.EnsureCap(new int[4], 10, 6).Length);
    }

    [Fact]
    public void GrowthWorksForEveryElementType()
    {
        // The element type is a type parameter, which is the whole reason this is
        // one helper rather than one per width: value types, reference types and
        // the byte destinations of a blob array all take the same path.
        byte[] bytes = Seq.EnsureCap(new byte[] { 7 }, 1, 4);
        Assert.Equal(new byte[] { 7, 0 }, bytes);

        double[] doubles = Seq.EnsureCap(new double[] { 1.5 }, 1, 4);
        Assert.Equal(new[] { 1.5, 0.0 }, doubles);

        string[] strings = Seq.EnsureCap(new[] { "a" }, 1, 4);
        Assert.Equal(2, strings.Length);
        Assert.Equal("a", strings[0]);
        Assert.Null(strings[1]);
    }
}
