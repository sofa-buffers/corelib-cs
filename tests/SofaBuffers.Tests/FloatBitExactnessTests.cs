/*
 * SofaBuffers C# - CORELIB_PLAN §6.5: every fp32 payload, signaling NaN included,
 * round-trips bit-for-bit.
 *
 * §6.5 puts C# in the "native fp32 type" row -- there is nothing to *do*, because
 * `float` holds the payload end-to-end and nothing in this port widens to double
 * on the way. What §6.5 does require of every port is the *test*: JSON vectors
 * cannot carry a NaN (§4.6, §7.1), so the shared suite is structurally blind to
 * this and an implementation-level suite has to assert it. A single gratuitous
 * widening -- an fp32 carried through a double in a re-encode path, an array
 * element materialized as `double` -- sets the quiet bit and destroys a signaling
 * NaN's payload silently:
 *
 *     0x7F800001 (sNaN)  --widen to double-->  0x7FC00001 (qNaN)
 *
 * So the assertions below are on the 32 BITS, never on the float value: `NaN` is
 * not equal to itself, and every comparison that would pass for one NaN would pass
 * for another. They are not vacuous either: the signaling and the quiet cases carry
 * different expected bits, so a path that quieted a NaN would fail the first and
 * pass the second.
 *
 * A source-level `(float)(double)x` is NOT the way to demonstrate the hazard on
 * .NET - the JIT folds the pair away and the pattern survives - which is precisely
 * why this is asserted end to end, over the encoder and the decoder, rather than
 * over a hand-built widening.
 *
 * Covered, as §6.5 names them: a signaling, a quiet and a negative NaN, at a
 * scalar and at an array position, through decode -> re-encode and through a
 * materialized walk, on both of this port's decode paths -- the bulk one and the
 * byte-at-a-time accumulator, which are different code.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class FloatBitExactnessTests
{
    /// <summary>
    /// The NaN bit patterns §6.5 names: signaling (quiet bit clear, payload
    /// non-zero), quiet, and both with the sign bit set. The last is a signaling
    /// NaN whose payload is all ones.
    /// </summary>
    public static IEnumerable<object[]> NanBits => new List<object[]>
    {
        new object[] { 0x7F800001u },   // sNaN
        new object[] { 0x7FC00001u },   // qNaN
        new object[] { 0xFF800001u },   // negative sNaN
        new object[] { 0xFFC00000u },   // negative qNaN
        new object[] { 0x7FBFFFFFu },   // sNaN, maximal payload
    };

    /// <summary>Records the fp32 values it is handed, as their 32 bits.</summary>
    private sealed class Fp32Bits : IVisitor
    {
        public readonly List<uint> Bits = new();

        public void Fp32(int id, float value) =>
            Bits.Add((uint)BitConverter.SingleToInt32Bits(value));
    }

    private static float FromBits(uint bits) => BitConverter.Int32BitsToSingle((int)bits);

    /// <summary>Feed <paramref name="wire"/> one byte at a time, so the split-scalar path runs.</summary>
    private static Fp32Bits DecodeByteAtATime(byte[] wire)
    {
        var visitor = new Fp32Bits();
        var istream = new IStream();
        DecodeStatus status = DecodeStatus.Incomplete;
        for (int i = 0; i < wire.Length; i++)
        {
            status = istream.Feed(wire, i, 1, visitor);
        }
        Assert.Equal(DecodeStatus.Complete, status);
        return visitor;
    }

    private static Fp32Bits DecodeWhole(byte[] wire)
    {
        var visitor = new Fp32Bits();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, visitor));
        return visitor;
    }

    /// <summary>
    /// A scalar fp32: the wire bytes carry the pattern verbatim, the visitor is
    /// handed exactly those bits, and re-encoding what the visitor received
    /// reproduces the same bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(NanBits))]
    public void AScalarNanRoundTripsBitForBit(uint bits)
    {
        byte[] wire = Encode(os => os.WriteFp32(1, FromBits(bits)));

        // The payload is on the wire little-endian, untouched.
        Assert.Equal(bits, BitConverter.ToUInt32(wire, wire.Length - 4));

        foreach (Fp32Bits decoded in new[] { DecodeWhole(wire), DecodeByteAtATime(wire) })
        {
            Assert.Equal(new[] { bits }, decoded.Bits);

            // ... and decode -> re-encode reproduces the wire exactly.
            byte[] again = Encode(os => os.WriteFp32(1, FromBits(decoded.Bits[0])));
            Assert.Equal(wire, again);
        }
    }

    /// <summary>
    /// The same at an array position: §6.5 requires "each element of an fp32
    /// array", and the array element loop is separate code from the scalar path in
    /// both directions.
    /// </summary>
    [Theory]
    [MemberData(nameof(NanBits))]
    public void AnArrayElementNanRoundTripsBitForBit(uint bits)
    {
        float value = FromBits(bits);
        byte[] wire = Encode(os => os.WriteArrayFp32(2, new[] { value, 1.5f, value }));

        foreach (Fp32Bits decoded in new[] { DecodeWhole(wire), DecodeByteAtATime(wire) })
        {
            Assert.Equal(
                new[] { bits, (uint)BitConverter.SingleToInt32Bits(1.5f), bits },
                decoded.Bits);

            byte[] again = Encode(os => os.WriteArrayFp32(
                2,
                new[]
                {
                    FromBits(decoded.Bits[0]),
                    FromBits(decoded.Bits[1]),
                    FromBits(decoded.Bits[2]),
                }));
            Assert.Equal(wire, again);
        }
    }

    /// <summary>
    /// The byte-container writer takes the four payload bytes directly, so a
    /// signaling NaN can also enter the encoder without ever being a
    /// <c>float</c>; it must come out of the decoder with the same bits.
    /// </summary>
    [Theory]
    [MemberData(nameof(NanBits))]
    public void ANanWrittenAsRawBytesDecodesToTheSameBits(uint bits)
    {
        byte[] payload = BitConverter.GetBytes(bits);
        byte[] wire = Encode(os => os.WriteFixlen(3, payload, 0, 4, FixlenType.Fp32));

        Assert.Equal(new[] { bits }, DecodeWhole(wire).Bits);
        Assert.Equal(new[] { bits }, DecodeByteAtATime(wire).Bits);
    }

    /// <summary>fp64 NaN patterns survive too - free, but nothing pinned it.</summary>
    [Theory]
    [InlineData(0x7FF0000000000001UL)]
    [InlineData(0x7FF8000000000001UL)]
    [InlineData(0xFFF0000000000001UL)]
    public void AnFp64NanRoundTripsBitForBit(ulong bits)
    {
        double value = BitConverter.Int64BitsToDouble((long)bits);
        byte[] wire = Encode(os => os.WriteFp64(1, value));
        Assert.Equal(bits, BitConverter.ToUInt64(wire, wire.Length - 8));

        var visitor = new Fp64Bits();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, visitor));
        Assert.Equal(new[] { bits }, visitor.Bits);
        Assert.Equal(
            wire,
            Encode(os => os.WriteFp64(1, BitConverter.Int64BitsToDouble((long)visitor.Bits[0]))));
    }

    private sealed class Fp64Bits : IVisitor
    {
        public readonly List<ulong> Bits = new();

        public void Fp64(int id, double value) =>
            Bits.Add((ulong)BitConverter.DoubleToInt64Bits(value));
    }
}
