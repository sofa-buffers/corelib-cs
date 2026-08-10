/*
 * SofaBuffers C# - WriteFixlen argument validation (issue #59).
 *
 * CORELIB_PLAN §4.6 fixes two properties of a `fixlen_word`:
 *   - for `fp32` / `fp64` the payload length is EXACTLY 4 / 8 bytes; a word
 *     declaring any other length for those subtypes is malformed (INVALID, §5.2);
 *   - subtypes 0x4..0x7 are RESERVED and a decoder must reject them (INVALID).
 *
 * The decoder in this repo enforces both. The public raw writer must therefore
 * never be able to emit such a word: an encoder that produces bytes its own
 * decoder rejects, and reports nothing to the writer, is the defect. §6.3 puts
 * a bad caller argument in the `InvalidArgument` (`SofabError.Argument`) bucket,
 * not `InvalidMessage`.
 *
 * The controls matter as much as the rejections: the legal widths, every legal
 * subtype, and the string/blob subtypes at arbitrary lengths must still encode
 * byte-for-byte as before.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using sofab;
using Xunit;

namespace SofaBuffers.Tests;

public class WriteFixlenValidationTests
{
    private static SofabException Rejects(Action write)
    {
        var ex = Assert.Throws<SofabException>(write);
        Assert.Equal(SofabError.Argument, ex.Error);
        return ex;
    }

    // --- fp32 / fp64 must declare exactly 4 / 8 bytes -----------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void Fp32RejectsAnyWidthButFour(int length)
    {
        var buf = new byte[64];
        Rejects(() => new OStream(buf).WriteFixlen(0, new byte[16], 0, length, FixlenType.Fp32));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(16)]
    public void Fp64RejectsAnyWidthButEight(int length)
    {
        var buf = new byte[64];
        Rejects(() => new OStream(buf).WriteFixlen(0, new byte[16], 0, length, FixlenType.Fp64));
    }

    /// <summary>Nothing may be written before the rejection.</summary>
    [Fact]
    public void RejectedWriteLeavesTheStreamUntouched()
    {
        var buf = new byte[64];
        var os = new OStream(buf);
        os.WriteUnsigned(1, 7);
        int used = os.BytesUsed;

        Rejects(() => os.WriteFixlen(2, new byte[5], 0, 5, FixlenType.Fp32));

        Assert.Equal(used, os.BytesUsed);
        os.WriteUnsigned(3, 9);
        Assert.Equal(new byte[] { 0x08, 0x07, 0x18, 0x09 }, buf[..os.BytesUsed]);
    }

    // --- reserved subtypes 0x4..0x7 -----------------------------------------

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ReservedSubtypesAreRejected(int raw)
    {
        var buf = new byte[64];
        Rejects(() => new OStream(buf).WriteFixlen(0, new byte[1], 0, 1, (FixlenType)raw));
    }

    /// <summary>
    /// A subtype outside the 3-bit tag space would otherwise corrupt the length
    /// it is OR-ed with, silently retagging the field.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(0x13)]
    [InlineData(-1)]
    public void OutOfRangeSubtypesAreRejected(int raw)
    {
        var buf = new byte[64];
        Rejects(() => new OStream(buf).WriteFixlen(0, new byte[1], 0, 1, (FixlenType)raw));
    }

    // --- controls: the legal cases still encode unchanged --------------------

    [Fact]
    public void Fp32AtFourBytesStillEncodes()
    {
        var buf = new byte[64];
        var os = new OStream(buf);
        os.WriteFixlen(0, new byte[] { 0x00, 0x00, 0x80, 0x3F }, 0, 4, FixlenType.Fp32);
        Assert.Equal(new byte[] { 0x02, 0x20, 0x00, 0x00, 0x80, 0x3F }, buf[..os.BytesUsed]);
    }

    [Fact]
    public void Fp64AtEightBytesStillEncodes()
    {
        var buf = new byte[64];
        var os = new OStream(buf);
        os.WriteFixlen(1, new byte[8], 0, 8, FixlenType.Fp64);
        Assert.Equal(
            new byte[] { 0x0A, 0x41, 0, 0, 0, 0, 0, 0, 0, 0 },
            buf[..os.BytesUsed]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(300)]
    public void StringAndBlobKeepEveryLength(int length)
    {
        foreach (var subtype in new[] { FixlenType.String, FixlenType.Blob })
        {
            var buf = new byte[512];
            var os = new OStream(buf);
            os.WriteFixlen(2, new byte[length], 0, length, subtype);

            Assert.Equal(0x12, buf[0]);
            Assert.Equal(1 + VarintLen(((ulong)length << 3) | (uint)subtype) + length, os.BytesUsed);
        }
    }

    private static int VarintLen(ulong v)
    {
        int n = 1;
        while (v >= 0x80)
        {
            v >>= 7;
            n++;
        }
        return n;
    }

    /// <summary>
    /// The end-to-end point of the issue: whatever the raw writer accepts, this
    /// port's own decoder must accept. Feeding back every legal combination is
    /// the invariant the two rejected classes above were breaking.
    /// </summary>
    [Fact]
    public void EverythingTheWriterAcceptsDecodes()
    {
        (FixlenType Subtype, int Length)[] cases =
        {
            (FixlenType.Fp32, 4),
            (FixlenType.Fp64, 8),
            (FixlenType.String, 0),
            (FixlenType.String, 5),
            (FixlenType.Blob, 0),
            (FixlenType.Blob, 9),
        };

        foreach (var (subtype, length) in cases)
        {
            var buf = new byte[128];
            var os = new OStream(buf);
            os.WriteFixlen(4, new byte[length], 0, length, subtype);
            var wire = buf[..os.BytesUsed];

            var istream = new IStream();
            var status = istream.Feed(wire, new CountingVisitor());
            Assert.Equal(DecodeStatus.Complete, status);
        }
    }

    private sealed class CountingVisitor : IVisitor
    {
    }
}
