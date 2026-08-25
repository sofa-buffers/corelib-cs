/*
 * SofaBuffers C# - CORELIB_PLAN §7.2 item 4: the three checks that make the
 * memory contract a *checked* property rather than a stated one.
 *
 *   - "No foreign memory, ever" -- every byte range a flush sink is handed lies
 *     inside the buffer that is currently installed. Pass-through is forbidden
 *     (§5.1.6), so this holds on every flush of every message, with no flag to set.
 *
 *   - "Overwrite every chunk after feed returns" -- a fed chunk is borrowed only
 *     for the duration of the call (§6.0), so scrubbing it afterwards must not
 *     change the decoded message. Nothing else in the required list would notice a
 *     decoder that kept a slice into a fed chunk: it would still produce the right
 *     values for every test that reads them before the next feed.
 *
 *   - "Overwrite the one-shot buffer too" -- §6.7.1 gives the one-shot path no
 *     exemption, and a port that borrows from the buffer it was handed passes
 *     every other item on the list. `Feed(byte[], IVisitor)` is this port's
 *     one-shot form, so the item is a single feed followed by a scrub.
 *
 * The decode halves need a visitor that MATERIALIZES: one that merely records
 * (offset, length) would be unaffected by a scrub. So these use the same pieces
 * generated code uses -- PayloadAcc to reassemble and Utf8.Decode to validate --
 * and compare the materialized values against the ones a one-shot decode produced
 * before the scrub.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace SofaBuffers.Tests;

public class ChunkLifetimeTests
{
    private const byte Scrub = 0xEE;

    /// <summary>Materializes every value as it arrives, exactly as generated code does.</summary>
    private sealed class Materializer : IVisitor
    {
        private readonly PayloadAcc _acc = new();

        public readonly List<string> Values = new();

        public void Unsigned(int id, ulong value) => Values.Add($"u{id}={value}");

        public void Signed(int id, long value) => Values.Add($"s{id}={value}");

        public void Fp32(int id, float value) =>
            Values.Add($"f{id}={BitConverter.SingleToInt32Bits(value):x8}");

        public void Fp64(int id, double value) =>
            Values.Add($"d{id}={BitConverter.DoubleToInt64Bits(value):x16}");

        public void String(int id, int total, int offset, byte[] data, int co, int cl)
        {
            string? s = _acc.String(total, offset, data, co, cl);
            if (s != null)
            {
                Values.Add($"t{id}={s}");
            }
        }

        public void Blob(int id, int total, int offset, byte[] data, int co, int cl)
        {
            byte[]? b = _acc.Blob(total, offset, data, co, cl);
            if (b != null)
            {
                Values.Add($"b{id}={Convert.ToHexString(b)}");
            }
        }

        public void SequenceBegin(int id) => Values.Add($"[{id}");

        public void SequenceEnd() => Values.Add("]");
    }

    /// <summary>A message with a string and a blob far longer than any chunk under test.</summary>
    private static byte[] Message()
    {
        var buf = new byte[8192];
        var os = new OStream(buf);
        var blob = new byte[1024];
        for (int i = 0; i < blob.Length; i++)
        {
            blob[i] = (byte)(i * 7);
        }

        os.WriteUnsigned(1, 300000);
        os.WriteSigned(2, -300000);
        os.WriteSequenceBeginLazy(3);
        os.WriteString(4, string.Concat(Enumerable.Repeat("héllo \U0001F600 ", 60)));
        os.WriteBlob(5, blob);
        os.WriteSequenceEnd();
        os.WriteFp32(6, 1.25f);
        os.WriteFp64(7, -3.5);
        return buf[..os.BytesUsed];
    }

    // --- "No foreign memory, ever" ------------------------------------------

    /// <summary>
    /// A blob twenty times the buffer size, and on every flush the sink asserts
    /// that the array it was handed IS the installed buffer and that the range
    /// lies inside it. Pass-through would show up as a foreign array here.
    /// </summary>
    [Fact]
    public void EveryFlushHandsBackARangeOfTheInstalledBuffer()
    {
        var buffer = new byte[64];
        var payload = new byte[64 * 20];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        int flushes = 0;
        var produced = new List<byte>();
        FlushSink sink = (data, offset, length) =>
        {
            flushes++;
            Assert.True(
                ReferenceEquals(data, buffer),
                "the sink was handed an array that is not the installed buffer " +
                "(CORELIB_PLAN §5.1.6: pass-through is forbidden)");
            Assert.InRange(offset, 0, buffer.Length);
            Assert.InRange(offset + length, offset, buffer.Length);
            produced.AddRange(data[offset..(offset + length)]);
        };

        var os = new OStream(buffer, 0, sink);
        os.WriteBlob(9, payload);
        os.Flush();

        Assert.True(flushes >= 20, $"expected many flushes, got {flushes}");

        // ... and the bytes are still the one-shot encoding.
        var oneShotBuf = new byte[payload.Length + 32];
        var oneShot = new OStream(oneShotBuf);
        oneShot.WriteBlob(9, payload);
        Assert.Equal(oneShotBuf[..oneShot.BytesUsed], produced.ToArray());
    }

    /// <summary>
    /// The same for a <c>string</c>, whose payload is transcoded rather than
    /// copied and is therefore the one that could plausibly be handed over from
    /// somewhere else.
    /// </summary>
    [Fact]
    public void EveryFlushOfATranscodedStringStaysInTheInstalledBuffer()
    {
        var buffer = new byte[16];
        string text = string.Concat(Enumerable.Repeat("abc éß €中 \U0001F600 ", 500));

        int flushes = 0;
        FlushSink sink = (data, offset, length) =>
        {
            flushes++;
            Assert.True(ReferenceEquals(data, buffer), "sink handed a foreign array");
            Assert.InRange(offset + length, offset, buffer.Length);
        };

        var os = new OStream(buffer, 0, sink);
        os.WriteString(1, text);
        os.Flush();

        Assert.True(flushes > 100, $"expected many flushes, got {flushes}");
    }

    // --- "Overwrite every chunk after feed returns" -------------------------

    /// <summary>
    /// Feed the message in n-byte pieces out of a scratch array that is scrubbed
    /// with <c>0xEE</c> the moment each <c>Feed</c> returns; the materialized
    /// values must equal the ones a one-shot decode produced.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(333)]
    public void ScrubbingEveryChunkAfterFeedChangesNothing(int chunk)
    {
        byte[] wire = Message();
        List<string> expected = OneShotValues(wire);

        var scratch = new byte[chunk];
        var istream = new IStream();
        var visitor = new Materializer();
        DecodeStatus status = DecodeStatus.Incomplete;

        for (int off = 0; off < wire.Length; off += chunk)
        {
            int n = Math.Min(chunk, wire.Length - off);
            Array.Copy(wire, off, scratch, 0, n);
            status = istream.Feed(scratch, 0, n, visitor);
            // The chunk is the caller's again the moment Feed returns (§6.0).
            scratch.AsSpan().Fill(Scrub);
        }

        Assert.Equal(DecodeStatus.Complete, status);
        Assert.Equal(expected, visitor.Values);
    }

    // --- "Overwrite the one-shot buffer too" --------------------------------

    /// <summary>
    /// One <c>Feed</c> of the whole message - this port's one-shot decode - then
    /// the buffer is scrubbed. §6.7.1 gives that path no exemption, so the values
    /// materialized during the call must survive it.
    /// </summary>
    [Fact]
    public void ScrubbingTheOneShotBufferAfterFeedChangesNothing()
    {
        byte[] wire = Message();
        List<string> expected = OneShotValues(wire);

        byte[] scratch = (byte[])wire.Clone();
        var visitor = new Materializer();
        DecodeStatus status = new IStream().Feed(scratch, visitor);
        scratch.AsSpan().Fill(Scrub);

        Assert.Equal(DecodeStatus.Complete, status);
        Assert.Equal(expected, visitor.Values);

        // The values really are the message's, not an artefact of comparing two
        // equally-broken decodes.
        Assert.Contains(visitor.Values, v => v.StartsWith("t4=héllo", StringComparison.Ordinal));
        Assert.Contains("u1=300000", visitor.Values);
    }

    /// <summary>Materialized values of a decode whose input is never touched.</summary>
    private static List<string> OneShotValues(byte[] wire)
    {
        var visitor = new Materializer();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, visitor));
        return visitor.Values;
    }
}
