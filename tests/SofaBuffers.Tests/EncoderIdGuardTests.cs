/*
 * SofaBuffers C# - the field-id contract, at both ends of every writer.
 *
 * A field id is `0 .. ID_MAX` (CORELIB_PLAN §6.2) and an out-of-range one is a
 * caller mistake, so it belongs in the `InvalidArgument` bucket (§6.3) -- not
 * `InvalidMessage`, which is about received bytes. The guard has to sit on every
 * public writer: they do not share one entry point (the scalar, float, string and
 * array writers each open their own inlined fast path), so a guard added to one
 * says nothing about the next. The first test below is therefore a table over the
 * whole writer surface, and it also pins the second half of the contract -- the
 * refusal is atomic: no byte reaches the buffer.
 *
 * The second test is the same surface from the other side: a *valid* id large
 * enough (>= 16) that its `(id << 3) | type` header no longer fits in one varint
 * byte. Every writer's fast path has a separate multi-byte-header branch, and the
 * suite's byte-exact fragments are almost all written with single-digit ids, so
 * those branches are asserted here against the bytes §4.3 requires.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using SofaBuffers.Tests.Common;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class EncoderIdGuardTests
{
    private static readonly byte[] Payload = { 0x11, 0x22 };

    /// <summary>Every public writer, invoked with an out-of-range field id.</summary>
    private static Dictionary<string, Action<OStream>> NegativeIdWriters() =>
        new()
        {
            ["WriteUnsigned"] = os => os.WriteUnsigned(-1, 5),
            ["WriteSigned"] = os => os.WriteSigned(-1, -5),
            ["WriteBoolean"] = os => os.WriteBoolean(-1, true),
            ["WriteFixlen"] = os => os.WriteFixlen(-1, Payload, 0, Payload.Length, FixlenType.Blob),
            ["WriteBlob"] = os => os.WriteBlob(-1, Payload),
            ["WriteFp32"] = os => os.WriteFp32(-1, 1.5f),
            ["WriteFp64"] = os => os.WriteFp64(-1, 1.5),
            ["WriteString(ascii)"] = os => os.WriteString(-1, "hi"),
            ["WriteString(non-ascii)"] = os => os.WriteString(-1, "grüße"),
            ["WriteArrayUnsigned(byte[])"] = os => os.WriteArrayUnsigned(-1, new byte[] { 1 }),
            ["WriteArrayUnsigned(ushort[])"] = os => os.WriteArrayUnsigned(-1, new ushort[] { 1 }),
            ["WriteArrayUnsigned(uint[])"] = os => os.WriteArrayUnsigned(-1, new uint[] { 1 }),
            ["WriteArrayUnsigned(ulong[])"] = os => os.WriteArrayUnsigned(-1, new ulong[] { 1 }),
            ["WriteArraySigned(sbyte[])"] = os => os.WriteArraySigned(-1, new sbyte[] { 1 }),
            ["WriteArraySigned(short[])"] = os => os.WriteArraySigned(-1, new short[] { 1 }),
            ["WriteArraySigned(int[])"] = os => os.WriteArraySigned(-1, new[] { 1 }),
            ["WriteArraySigned(long[])"] = os => os.WriteArraySigned(-1, new[] { 1L }),
            ["WriteArrayFp32"] = os => os.WriteArrayFp32(-1, new[] { 1f }),
            ["WriteArrayFp64"] = os => os.WriteArrayFp64(-1, new[] { 1.0 }),
            ["WriteSequenceBeginLazy"] = os => os.WriteSequenceBeginLazy(-1),
        };

    [Fact]
    public void NegativeIdRefusedAtomicallyByEveryWriter()
    {
        foreach (KeyValuePair<string, Action<OStream>> writer in NegativeIdWriters())
        {
            var os = new OStream(new byte[64]);
            Exception? raised = Record.Exception(() => writer.Value(os));

            Assert.True(
                raised is SofabException se && se.Error == SofabError.Argument,
                writer.Key + " accepted the id -1 (or raised the wrong error): "
                    + (raised?.ToString() ?? "no exception"));
            Assert.True(
                raised!.Message.Contains("-1", StringComparison.Ordinal),
                writer.Key + " does not name the offending id: " + raised.Message);
            Assert.True(os.BytesUsed == 0, writer.Key + " wrote bytes before refusing");
        }
    }

    [Fact]
    public void MultiByteFieldHeaderIsWrittenOnTheFastPath()
    {
        // id 16 makes every `(id << 3) | type` header exceed one varint byte:
        // 0x80 | type, then 0x01. Each writer below assembles its header on its
        // own inlined fast path, so each is asserted byte-exactly against §4.3.
        Assert.Equal(Bytes(0x80, 0x01, 0x05), Encode(os => os.WriteUnsigned(16, 5)));
        Assert.Equal(Bytes(0x81, 0x01, 0x05), Encode(os => os.WriteSigned(16, -3)));
        Assert.Equal(
            Bytes(0x82, 0x01, 0x20, 0x00, 0x00, 0x80, 0x3F),
            Encode(os => os.WriteFp32(16, 1f)));
        Assert.Equal(
            Bytes(0x82, 0x01, 0x41, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F),
            Encode(os => os.WriteFp64(16, 1.0)));
        Assert.Equal(Bytes(0x82, 0x01, 0x12, 0x68, 0x69), Encode(os => os.WriteString(16, "hi")));
        Assert.Equal(
            Bytes(0x82, 0x01, 0x13, 0x11, 0x22),
            Encode(os => os.WriteBlob(16, Payload)));
        Assert.Equal(
            Bytes(0x83, 0x01, 0x02, 0x01, 0x02),
            Encode(os => os.WriteArrayUnsigned(16, new byte[] { 1, 2 })));
        Assert.Equal(
            Bytes(0x84, 0x01, 0x02, 0x02, 0x03),
            Encode(os => os.WriteArraySigned(16, new[] { 1, -2 })));
        Assert.Equal(
            Bytes(0x85, 0x01, 0x01, 0x20, 0x00, 0x00, 0x80, 0x3F),
            Encode(os => os.WriteArrayFp32(16, new[] { 1f })));

        // ... and the ids survive a round trip, which the fragments alone cannot
        // show (a header byte pair is only right if it reads back as id 16).
        var visitor = new RecordingVisitor();
        byte[] wire = Encode(os =>
        {
            os.WriteUnsigned(16, 5);
            os.WriteSigned(129, -3);
            os.WriteString(1000, "hi");
        });
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, visitor));
        Assert.Equal(new[] { "u:16=5", "s:129=-3", "str:1000=hi" }, visitor.Events);
    }

    [Fact]
    public void WriteStringRejectsNull()
    {
        // A null value is a caller mistake of a different shape from an unpaired
        // surrogate (§6.4): there is nothing to transcode at all, so it is the
        // platform's ArgumentNullException rather than a SofabException.
        var os = new OStream(new byte[32]);
        Assert.Throws<ArgumentNullException>(() => os.WriteString(1, null!));
        Assert.Equal(0, os.BytesUsed);
    }
}
