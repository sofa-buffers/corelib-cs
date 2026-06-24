/*
 * SofaBuffers C# - decoder rejects the remaining malformed-input branches.
 *
 * SPDX-License-Identifier: MIT
 */

using Xunit;

namespace SofaBuffers.Tests;

public class DecoderErrorsTests
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

    private sealed class IgnoreVisitor : IVisitor
    {
    }

    private static SofabError ErrorOf(byte[] data)
    {
        var ex = Assert.Throws<SofabException>(() => new IStream().Feed(data, new IgnoreVisitor()));
        return ex.Error;
    }

    [Fact]
    public void IdAboveMaxRejected()
    {
        // Header varint 0x400000000: (id = 2^31) << 3 | type 0; id exceeds ID_MAX.
        Assert.Equal(SofabError.InvalidMessage, ErrorOf(Bytes(0x80, 0x80, 0x80, 0x80, 0x40)));
    }

    [Fact]
    public void ReservedFixlenTypeRejected()
    {
        // fixlen field (id 0), fixlen header 0x04 -> reserved subtype 4.
        Assert.Equal(SofabError.InvalidMessage, ErrorOf(Bytes(0x02, 0x04)));
    }

    [Fact]
    public void Fp64WrongLengthRejected()
    {
        // fixlen field (id 0), header (4 << 3) | FP64(1) = 0x21 -> fp64 length 4 (must be 8).
        Assert.Equal(SofabError.InvalidMessage, ErrorOf(Bytes(0x02, 0x21)));
    }

    [Fact]
    public void Fp32WrongLengthRejected()
    {
        // fixlen field (id 0), header (5 << 3) | FP32(0) = 0x28 -> fp32 length 5 (must be 4).
        Assert.Equal(SofabError.InvalidMessage, ErrorOf(Bytes(0x02, 0x28)));
    }

    [Fact]
    public void StringAsFixlenArrayElementRejected()
    {
        // fixlen-array (id 0), count 1, element header (1 << 3) | STRING(2) = 0x0A.
        // String/blob are not valid as fixlen-array elements.
        Assert.Equal(SofabError.InvalidMessage, ErrorOf(Bytes(0x05, 0x01, 0x0A)));
    }

    [Fact]
    public void FixlenLengthAboveMaxRejected()
    {
        // fixlen string (id 0) with length 2^31 (> ARRAY_MAX): header (2^31 << 3) | STRING(2).
        Assert.Equal(SofabError.InvalidMessage, ErrorOf(Bytes(0x02, 0x82, 0x80, 0x80, 0x80, 0x40)));
    }
}
