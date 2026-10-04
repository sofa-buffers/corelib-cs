/*
 * SofaBuffers C# - sofab.FloatBits.BitsEqual: float arrays compare by IEEE-754
 * bit pattern, never by `==` (generator#636).
 *
 * The generated encoder omits a field iff it equals its default (MESSAGE_SPEC §2)
 * and floats round-trip bit for bit (CORELIB_PLAN §4.6), so [-0.0, 1.5] must NOT
 * equal the default [0.0, 1.5]. Enumerable.SequenceEqual, which generated code
 * used before, says they are equal and drops the -0.0 element.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SofaBuffers.Tests;

public class FloatBitsTests
{
    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);
    private static double D(ulong bits) => BitConverter.UInt64BitsToDouble(bits);

    // Reference: a plain integer-bit loop, written independently of the helper.
    private static bool Ref(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.SingleToUInt32Bits(a[i]) != BitConverter.SingleToUInt32Bits(b[i])) return false;
        return true;
    }

    private static bool Ref(double[] a, double[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.DoubleToUInt64Bits(a[i]) != BitConverter.DoubleToUInt64Bits(b[i])) return false;
        return true;
    }

    [Fact]
    public void EmptyArraysAreEqual()
    {
        Assert.True(FloatBits.BitsEqual(Array.Empty<float>(), Array.Empty<float>()));
        Assert.True(FloatBits.BitsEqual(Array.Empty<double>(), Array.Empty<double>()));
        Assert.True(FloatBits.BitsEqual(ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty));
    }

    [Fact]
    public void OneElement()
    {
        Assert.True(FloatBits.BitsEqual(new[] { 1.5f }, new[] { 1.5f }));
        Assert.False(FloatBits.BitsEqual(new[] { 1.5f }, new[] { 2.5f }));
        Assert.True(FloatBits.BitsEqual(new[] { 1.5 }, new[] { 1.5 }));
        Assert.False(FloatBits.BitsEqual(new[] { 1.5 }, new[] { 2.5 }));
    }

    [Fact]
    public void EqualArraysAreEqualAndSelfIsEqual()
    {
        var a = new[] { 0.0f, 1.5f, -3.25f };
        Assert.True(FloatBits.BitsEqual(a, new[] { 0.0f, 1.5f, -3.25f }));
        Assert.True(FloatBits.BitsEqual(a, a));
        var d = new[] { 0.0, 1.5, -3.25 };
        Assert.True(FloatBits.BitsEqual(d, new[] { 0.0, 1.5, -3.25 }));
        Assert.True(FloatBits.BitsEqual(d, d));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NegativeZeroDiffersFromPositiveZeroAtEveryIndex(int index)
    {
        var def32 = new[] { 0.0f, 0.0f, 0.0f };
        var got32 = new[] { 0.0f, 0.0f, 0.0f };
        got32[index] = -0.0f;
        Assert.False(FloatBits.BitsEqual(got32, def32));
        Assert.False(FloatBits.BitsEqual(def32, got32));
        // The bug this helper replaces: IEEE equality calls them equal.
        Assert.True(Enumerable.SequenceEqual(got32, def32));

        var def64 = new[] { 0.0, 0.0, 0.0 };
        var got64 = new[] { 0.0, 0.0, 0.0 };
        got64[index] = -0.0;
        Assert.False(FloatBits.BitsEqual(got64, def64));
        Assert.False(FloatBits.BitsEqual(def64, got64));
        Assert.True(Enumerable.SequenceEqual(got64, def64));
    }

    [Fact]
    public void TheGeneratedCallSiteShape()
    {
        // `[-0.0, 1.5]` against the default `[0.0, 1.5]`: must not be omitted.
        float[] field = { -0.0f, 1.5f };
        float[] def = { 0.0f, 1.5f };
        Assert.False(FloatBits.BitsEqual(field, def));
        double[] field64 = { -0.0, 1.5 };
        double[] def64 = { 0.0, 1.5 };
        Assert.False(FloatBits.BitsEqual(field64, def64));
    }

    [Fact]
    public void IdenticalNanBitPatternsAreEqual()
    {
        Assert.True(FloatBits.BitsEqual(new[] { float.NaN }, new[] { float.NaN }));
        Assert.True(FloatBits.BitsEqual(new[] { F(0x7FC00001) }, new[] { F(0x7FC00001) }));
        Assert.True(FloatBits.BitsEqual(new[] { double.NaN }, new[] { double.NaN }));
        Assert.True(FloatBits.BitsEqual(new[] { D(0x7FF8000000000001) }, new[] { D(0x7FF8000000000001) }));
    }

    [Fact]
    public void NanPayloadAndSignAndQuietnessDiffer()
    {
        Assert.False(FloatBits.BitsEqual(new[] { F(0x7FC00001) }, new[] { F(0x7FC00002) }));
        Assert.False(FloatBits.BitsEqual(new[] { F(0x7FC00000) }, new[] { F(0xFFC00000) }));
        // signaling vs quiet
        Assert.False(FloatBits.BitsEqual(new[] { F(0x7F800001) }, new[] { F(0x7FC00001) }));
        Assert.False(FloatBits.BitsEqual(new[] { D(0x7FF8000000000001) }, new[] { D(0x7FF8000000000002) }));
        Assert.False(FloatBits.BitsEqual(new[] { D(0x7FF8000000000000) }, new[] { D(0xFFF8000000000000) }));
        Assert.False(FloatBits.BitsEqual(new[] { D(0x7FF0000000000001) }, new[] { D(0x7FF8000000000001) }));
    }

    [Fact]
    public void InfinitiesAndSubnormals()
    {
        Assert.True(FloatBits.BitsEqual(new[] { float.PositiveInfinity, float.NegativeInfinity },
                                        new[] { float.PositiveInfinity, float.NegativeInfinity }));
        Assert.False(FloatBits.BitsEqual(new[] { float.PositiveInfinity }, new[] { float.NegativeInfinity }));
        Assert.True(FloatBits.BitsEqual(new[] { double.PositiveInfinity, double.NegativeInfinity },
                                        new[] { double.PositiveInfinity, double.NegativeInfinity }));
        Assert.False(FloatBits.BitsEqual(new[] { double.PositiveInfinity }, new[] { double.NegativeInfinity }));

        Assert.True(FloatBits.BitsEqual(new[] { F(1), F(0x007FFFFF) }, new[] { F(1), F(0x007FFFFF) }));
        Assert.False(FloatBits.BitsEqual(new[] { F(1) }, new[] { F(2) }));
        Assert.False(FloatBits.BitsEqual(new[] { F(1) }, new[] { 0.0f }));
        Assert.True(FloatBits.BitsEqual(new[] { D(1), D(0x000FFFFFFFFFFFFF) }, new[] { D(1), D(0x000FFFFFFFFFFFFF) }));
        Assert.False(FloatBits.BitsEqual(new[] { D(1) }, new[] { D(2) }));
        Assert.False(FloatBits.BitsEqual(new[] { D(1) }, new[] { 0.0 }));
    }

    [Fact]
    public void LengthMismatchInBothDirections()
    {
        Assert.False(FloatBits.BitsEqual(new[] { 1f, 2f }, new[] { 1f, 2f, 3f }));
        Assert.False(FloatBits.BitsEqual(new[] { 1f, 2f, 3f }, new[] { 1f, 2f }));
        Assert.False(FloatBits.BitsEqual(Array.Empty<float>(), new[] { 0f }));
        Assert.False(FloatBits.BitsEqual(new[] { 0f }, Array.Empty<float>()));
        Assert.False(FloatBits.BitsEqual(new[] { 1.0, 2.0 }, new[] { 1.0, 2.0, 3.0 }));
        Assert.False(FloatBits.BitsEqual(new[] { 1.0, 2.0, 3.0 }, new[] { 1.0, 2.0 }));
        Assert.False(FloatBits.BitsEqual(Array.Empty<double>(), new[] { 0.0 }));
        Assert.False(FloatBits.BitsEqual(new[] { 0.0 }, Array.Empty<double>()));
    }

    [Theory]
    [InlineData(65)]
    [InlineData(100)]
    [InlineData(4097)]
    public void LongArraysWithOneDifferingElement(int n)
    {
        foreach (int at in new[] { 0, n / 2, n - 1 })
        {
            var a = new float[n];
            var b = new float[n];
            for (int i = 0; i < n; i++) a[i] = b[i] = i * 0.5f;
            Assert.True(FloatBits.BitsEqual(a, b));
            a[at] = -a[at] == 0 ? -0.0f : a[at] + 1f;
            Assert.False(FloatBits.BitsEqual(a, b));
            Assert.False(FloatBits.BitsEqual(b, a));

            var c = new double[n];
            var d = new double[n];
            for (int i = 0; i < n; i++) c[i] = d[i] = i * 0.5;
            Assert.True(FloatBits.BitsEqual(c, d));
            c[at] = -c[at] == 0 ? -0.0 : c[at] + 1.0;
            Assert.False(FloatBits.BitsEqual(c, d));
            Assert.False(FloatBits.BitsEqual(d, c));
        }
    }

    [Fact]
    public void LongArrayOfZerosWithASingleNegativeZero()
    {
        foreach (int at in new[] { 0, 70, 127 })
        {
            var a = new float[128];
            var b = new float[128];
            a[at] = -0.0f;
            Assert.False(FloatBits.BitsEqual(a, b));
            var c = new double[128];
            var d = new double[128];
            c[at] = -0.0;
            Assert.False(FloatBits.BitsEqual(c, d));
        }
    }

    [Fact]
    public void PseudoRandomCrossCheckAgainstReferenceLoop()
    {
        var rng = new Random(636);
        for (int iter = 0; iter < 2000; iter++)
        {
            int n = rng.Next(0, 90);
            var a = new float[n];
            var b = new float[n];
            var c = new double[n];
            var d = new double[n];
            for (int i = 0; i < n; i++)
            {
                // Draw from a small pool of nasty patterns so equal elements are common.
                uint u = Pick32(rng);
                a[i] = b[i] = F(u);
                ulong w = Pick64(rng);
                c[i] = d[i] = D(w);
            }
            if (n > 0 && rng.Next(2) == 0)
            {
                int at = rng.Next(n);
                b[at] = F(Pick32(rng));
                d[at] = D(Pick64(rng));
            }
            Assert.Equal(Ref(a, b), FloatBits.BitsEqual(a, b));
            Assert.Equal(Ref(c, d), FloatBits.BitsEqual(c, d));
            // Different lengths too.
            var shorter = a.AsSpan(0, n / 2).ToArray();
            Assert.Equal(Ref(a, shorter), FloatBits.BitsEqual(a, shorter));
            var shorter64 = c.AsSpan(0, n / 2).ToArray();
            Assert.Equal(Ref(c, shorter64), FloatBits.BitsEqual(c, shorter64));
        }
    }

    private static readonly uint[] Pool32 =
    {
        0x00000000, 0x80000000, 0x3FC00000, 0x7F800000, 0xFF800000, 0x7FC00000,
        0x7FC00001, 0x7F800001, 0x00000001, 0x007FFFFF, 0xDEADBEEF,
    };
    private static readonly ulong[] Pool64 =
    {
        0UL, 0x8000000000000000, 0x3FF8000000000000, 0x7FF0000000000000, 0xFFF0000000000000,
        0x7FF8000000000000, 0x7FF8000000000001, 0x7FF0000000000001, 1UL,
        0x000FFFFFFFFFFFFF, 0xDEADBEEFCAFEF00D,
    };
    private static uint Pick32(Random r) => Pool32[r.Next(Pool32.Length)];
    private static ulong Pick64(Random r) => Pool64[r.Next(Pool64.Length)];

    [Fact]
    public void DoesNotMutateInputs()
    {
        var a = new[] { -0.0f, float.NaN, 1f };
        var b = new[] { 0.0f, float.NaN, 1f };
        var ac = (float[])a.Clone();
        var bc = (float[])b.Clone();
        FloatBits.BitsEqual(a, b);
        Assert.True(FloatBits.BitsEqual(a, ac));
        Assert.True(FloatBits.BitsEqual(b, bc));
    }

    [Fact]
    public void AcceptsSpansAndSubranges()
    {
        var a = new[] { 9f, 1f, 2f, 9f };
        var b = new[] { 1f, 2f };
        Assert.True(FloatBits.BitsEqual(a.AsSpan(1, 2), b));
        Assert.False(FloatBits.BitsEqual(a.AsSpan(0, 2), b));
        var c = new[] { 9.0, 1.0, 2.0, 9.0 };
        var d = new[] { 1.0, 2.0 };
        Assert.True(FloatBits.BitsEqual(c.AsSpan(1, 2), d));
        Assert.False(FloatBits.BitsEqual(c.AsSpan(0, 2), d));
    }
}
