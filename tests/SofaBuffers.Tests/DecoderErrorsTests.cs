/*
 * SofaBuffers C# - decoder rejects the remaining malformed-input branches.
 *
 * SPDX-License-Identifier: MIT
 */

using SofaBuffers.Tests.Common;
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

    // Feed the bytes one at a time so the byte-at-a-time state machine
    // (VarintPush) decodes the value, rather than the bulk fast path.
    private static SofabError ErrorOfByteAtATime(byte[] data)
    {
        var stream = new IStream();
        var visitor = new IgnoreVisitor();
        var ex = Assert.Throws<SofabException>(() =>
        {
            foreach (byte b in data)
            {
                stream.Feed(new[] { b }, visitor);
            }
        });
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

    // An overlong (>64-bit) varint is malformed and must be rejected as INVALID,
    // not silently truncated/wrapped (MESSAGE_SPEC §4.1/§6.3). id-6 u64 field
    // (header 0x30) carrying a 10-byte varint whose 10th byte sets bits above 63.
    // Bulk (fast-path) decode -- the whole message is fed in one call.
    [Fact]
    public void OverlongVarint65thBitRejected()
    {
        // ff*9 02: the 65th bit set -> > 2^64-1.
        Assert.Equal(
            SofabError.InvalidMessage,
            ErrorOf(Bytes(0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02)));
    }

    [Fact]
    public void OverlongVarintHighBitsRejected()
    {
        // ff*9 7f: bits 64..69 set -> a different wrong value if truncated.
        Assert.Equal(
            SofabError.InvalidMessage,
            ErrorOf(Bytes(0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F)));
    }

    [Fact]
    public void OverlongVarintElevenBytesRejected()
    {
        // A continuation byte past the 10th (11 bytes) is overlong regardless of payload.
        Assert.Equal(
            SofabError.InvalidMessage,
            ErrorOf(Bytes(0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01)));
    }

    // Same overlong inputs, but fed byte-at-a-time so the value is decoded by the
    // streaming state machine (VarintPush) instead of the bulk fast path.
    [Fact]
    public void OverlongVarint65thBitRejectedByteAtATime()
    {
        Assert.Equal(
            SofabError.InvalidMessage,
            ErrorOfByteAtATime(Bytes(0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02)));
    }

    [Fact]
    public void OverlongVarintHighBitsRejectedByteAtATime()
    {
        Assert.Equal(
            SofabError.InvalidMessage,
            ErrorOfByteAtATime(Bytes(0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F)));
    }

    // Control: the valid maximum (ff*9 01 == 2^64-1) still decodes, on both paths.
    [Fact]
    public void MaxU64Accepted()
    {
        var bulk = new RecordingVisitor();
        new IStream().Feed(
            Bytes(0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01), bulk);
        Assert.Equal(new[] { "u:6=18446744073709551615" }, bulk.Events);

        var streamed = new RecordingVisitor();
        var stream = new IStream();
        foreach (byte b in Bytes(0x30, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01))
        {
            stream.Feed(new[] { b }, streamed);
        }
        Assert.Equal(new[] { "u:6=18446744073709551615" }, streamed.Events);
    }
}
