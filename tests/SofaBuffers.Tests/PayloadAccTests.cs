/*
 * SofaBuffers C# - reassembly of a string/blob payload delivered in chunks:
 * sofab.PayloadAcc (CORELIB_PLAN §6.4 "cross-chunk semantics", normative).
 *
 * A payload is delivered wherever the input happened to be split, so the one
 * obligation that runs through this whole file is that the split is NOT
 * OBSERVABLE: for the same bytes the value -- and, for a string, the UTF-8 verdict
 * -- is identical whether they arrive in one piece, at every possible split, or one
 * byte at a time. So the tests below drive each payload at every offset 0..n rather
 * than at a couple of hand-picked ones.
 *
 * That also closes a gap the shared vectors cannot reach: their `invalid_utf8`
 * payloads are one to four bytes and defective from offset 0, so no vector splits
 * a defective payload at all (the same gap Utf8ChunkBoundaryTests documents from
 * the IStream side).
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class PayloadAccTests
{
    /// <summary>
    /// The receiver cap these tests state, high enough that it never fires: they
    /// are about reassembly, not about the cap. Stating one is not optional —
    /// CORELIB_PLAN §6.2.1 gives the argument no unset state and no "unlimited"
    /// spelling — so they state the largest length a payload can have. The cap's
    /// own behaviour is exercised in <see cref="PayloadAccCapTests"/>.
    /// </summary>
    private const long AnyLength = int.MaxValue;

    /// <summary>Two, three and four byte sequences, so a split can land inside one.</summary>
    private const string Sample = "héllo — wörld 😀 ünïcodé";

    private static byte[] Utf8Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>
    /// Feed <paramref name="payload"/> as the two chunks <c>[0, split)</c> and
    /// <c>[split, n)</c>, asserting the value appears exactly on the chunk that
    /// completes it and not before.
    /// </summary>
    private static string FeedString(byte[] payload, int split)
    {
        var acc = new PayloadAcc();
        int total = payload.Length;

        string? first = acc.String(total, 0, payload, 0, split, AnyLength);
        if (split >= total)
        {
            Assert.NotNull(first);
            return first!;
        }

        Assert.Null(first); // incomplete: no value, no verdict
        string? second = acc.String(total, split, payload, split, total - split, AnyLength);
        Assert.NotNull(second);
        return second!;
    }

    /// <summary><see cref="FeedString"/> for a blob payload.</summary>
    private static byte[] FeedBlob(byte[] payload, int split)
    {
        var acc = new PayloadAcc();
        int total = payload.Length;

        byte[]? first = acc.Blob(total, 0, payload, 0, split, AnyLength);
        if (split >= total)
        {
            Assert.NotNull(first);
            return first!;
        }

        Assert.Null(first);
        byte[]? second = acc.Blob(total, split, payload, split, total - split, AnyLength);
        Assert.NotNull(second);
        return second!;
    }

    // --- the value does not depend on the split ------------------------------

    [Fact]
    public void AStringSplitAtEveryOffsetDecodesToTheSameValue()
    {
        byte[] payload = Utf8Bytes(Sample);
        for (int split = 0; split <= payload.Length; split++)
        {
            Assert.Equal(Sample, FeedString(payload, split));
        }
    }

    [Fact]
    public void ABlobSplitAtEveryOffsetReassemblesTheSameBytes()
    {
        byte[] payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        for (int split = 0; split <= payload.Length; split++)
        {
            Assert.Equal(payload, FeedBlob(payload, split));
        }
    }

    [Fact]
    public void AStringArrivingOneByteAtATimeDecodesToTheSameValue()
    {
        // Every multi-byte sequence in the sample is therefore split across a
        // chunk boundary: a well-formed prefix is not a defect, so validation can
        // only run once, on the completed payload.
        byte[] payload = Utf8Bytes(Sample);
        var acc = new PayloadAcc();

        string? value = null;
        for (int i = 0; i < payload.Length; i++)
        {
            Assert.Null(value);
            value = acc.String(payload.Length, i, payload, i, 1, AnyLength);
        }

        Assert.Equal(Sample, value);
    }

    [Fact]
    public void APayloadDeliveredInUnevenChunksIsReassembled()
    {
        // Chunk sizes as the transport hands them over: 1, 2, 3, ... bytes.
        byte[] payload = Enumerable.Range(0, 500).Select(i => (byte)(i * 7)).ToArray();
        var acc = new PayloadAcc();

        byte[]? value = null;
        int offset = 0;
        for (int step = 1; offset < payload.Length; step++)
        {
            int take = Math.Min(step, payload.Length - offset);
            Assert.Null(value);
            value = acc.Blob(payload.Length, offset, payload, offset, take, AnyLength);
            offset += take;
        }

        Assert.Equal(payload, value);
    }

    // --- the whole-in-one-chunk fast path ------------------------------------

    [Fact]
    public void AWholePayloadIsAnsweredOutOfTheCallersBuffer()
    {
        // The chunk carries the payload plus what followed it in the input: only
        // `total` bytes belong to this field. The trailing byte here is a lone
        // continuation, so an accumulator reading one byte too far rejects.
        byte[] data = Bytes(0x68, 0x69, 0x80);
        Assert.Equal("hi", new PayloadAcc().String(2, 0, data, 0, data.Length, AnyLength));

        byte[]? blob = new PayloadAcc().Blob(2, 0, data, 0, data.Length, AnyLength);
        Assert.Equal(Bytes(0x68, 0x69), blob);
    }

    [Fact]
    public void AReturnedBlobIsTheCallersToKeep()
    {
        // IVisitor's `data` is only valid for the duration of the call, so a blob
        // must come back as a copy -- whole-in-one-chunk and reassembled alike.
        byte[] data = Bytes(1, 2, 3, 4);

        byte[]? whole = new PayloadAcc().Blob(4, 0, data, 0, 4, AnyLength);
        var acc = new PayloadAcc();
        Assert.Null(acc.Blob(4, 0, data, 0, 2, AnyLength));
        byte[]? split = acc.Blob(4, 2, data, 2, 2, AnyLength);

        data[0] = 0xFF; // the decoder moves on and reuses its input buffer
        Assert.Equal(Bytes(1, 2, 3, 4), whole);
        Assert.Equal(Bytes(1, 2, 3, 4), split);

        // ...and it is not a view onto the accumulator either: writing to one
        // returned payload cannot show up in the next.
        split![0] = 0x7F;
        Assert.Equal(Bytes(1, 2, 3, 4), whole);
    }

    [Fact]
    public void AnEmptyPayloadIsAnOrdinaryValue()
    {
        // total == 0 is delivered as one callback with an empty chunk.
        Assert.Equal(string.Empty, new PayloadAcc().String(0, 0, Array.Empty<byte>(), 0, 0, AnyLength));
        Assert.Equal(Array.Empty<byte>(), new PayloadAcc().Blob(0, 0, Array.Empty<byte>(), 0, 0, AnyLength));
    }

    [Fact]
    public void AChunkIsReadFromWhereItSitsInTheInputBuffer()
    {
        // chunkOffset is a position in the decoder's buffer and has nothing to do
        // with the payload offset: here the two halves arrive from opposite ends
        // of one array.
        byte[] data = Bytes(0x6F, 0x6F, 0x00, 0x00, 0x66, 0x66);
        var acc = new PayloadAcc();

        Assert.Null(acc.String(4, 0, data, 4, 2, AnyLength));
        Assert.Equal("ffoo", acc.String(4, 2, data, 0, 2, AnyLength));
    }

    // --- invalid UTF-8, wherever the split falls ------------------------------

    [Fact]
    public void AnInvalidStringIsRejectedAtEverySplit()
    {
        // A valid prefix, a lone surrogate, a valid suffix. Splitting inside the
        // defect, right before it and right after it must all end the same way --
        // including the split that puts the defect entirely in a later chunk, the
        // shape no shared vector reaches.
        byte[] payload = Utf8Bytes("hello ")
            .Concat(Bytes(0xED, 0xA0, 0x80))
            .Concat(Utf8Bytes(" world"))
            .ToArray();

        for (int split = 0; split <= payload.Length; split++)
        {
            var e = Assert.Throws<SofabException>(() => FeedString(payload, split));
            Assert.Equal(SofabError.InvalidMessage, e.Error);
        }
    }

    [Fact]
    public void ATruncatedSequenceIsADefectOnlyWhenThePayloadEnds()
    {
        // The same three bytes, judged twice. Inside a payload that continues they
        // are a well-formed prefix and the value decodes; as the payload's own last
        // bytes they are a truncated sequence and the message is malformed. The
        // verdict is a property of `total`, never of the chunking.
        byte[] whole = Utf8Bytes("a😀b");
        for (int split = 0; split <= whole.Length; split++)
        {
            Assert.Equal("a😀b", FeedString(whole, split));
        }

        byte[] truncated = whole.Take(whole.Length - 2).ToArray(); // "a" + F0 9F 98
        for (int split = 0; split <= truncated.Length; split++)
        {
            Assert.Equal(
                SofabError.InvalidMessage,
                Assert.Throws<SofabException>(() => FeedString(truncated, split)).Error);
        }
    }

    // --- state between payloads ----------------------------------------------

    [Fact]
    public void APayloadThatNeverCompletedIsNotPrefixedOntoTheNext()
    {
        // A stream that ended mid-field leaves bytes in the accumulator. The next
        // payload's first chunk arrives at offset 0, and that is where they go:
        // there is no re-arming call for a caller to forget.
        var acc = new PayloadAcc();
        Assert.Null(acc.String(9, 0, Utf8Bytes("abandoned"), 0, 5, AnyLength));

        byte[] next = Utf8Bytes("kept");
        Assert.Null(acc.String(next.Length, 0, next, 0, 2, AnyLength));
        Assert.Equal("kept", acc.String(next.Length, 2, next, 2, 2, AnyLength));
    }

    [Fact]
    public void OnePayloadFollowsAnotherThroughTheSameAccumulator()
    {
        // The visitor holds one accumulator for the whole message, so every field
        // of every kind runs through it in turn.
        var acc = new PayloadAcc();
        byte[] first = Utf8Bytes("first value");
        byte[] second = Utf8Bytes("second");

        Assert.Null(acc.String(first.Length, 0, first, 0, 4, AnyLength));
        Assert.Equal("first value", acc.String(first.Length, 4, first, 4, first.Length - 4, AnyLength));

        Assert.Null(acc.Blob(second.Length, 0, second, 0, 1, AnyLength));
        Assert.Equal(second, acc.Blob(second.Length, 1, second, 1, second.Length - 1, AnyLength));

        // A whole payload after a split one takes the fast path and is unaffected
        // by what the buffer still holds.
        Assert.Equal("third", acc.String(5, 0, Utf8Bytes("third"), 0, 5, AnyLength));
    }

    [Fact]
    public void ALargePayloadGrowsWithTheBytesThatArrive()
    {
        const int total = 200_000;
        byte[] payload = Enumerable.Range(0, total).Select(i => (byte)(i * 31)).ToArray();
        var acc = new PayloadAcc();

        byte[]? value = null;
        for (int offset = 0; offset < total; offset += 997)
        {
            int take = Math.Min(997, total - offset);
            value = acc.Blob(total, offset, payload, offset, take, AnyLength);
        }

        Assert.Equal(payload, value);
    }

    [Fact]
    public void AnAnnouncedTotalNearTwoToThe31AllocatesNothing()
    {
        // `total` is the wire's claim. A three-byte header announcing 2 GiB and
        // delivering eight bytes must cost eight bytes: the buffer doubles against
        // what arrived, and the payload itself is only sized once it is complete.
        var acc = new PayloadAcc();
        byte[] chunk = Utf8Bytes("12345678");

        // Warm the two paths up first, so what is measured is the allocation the
        // announcement causes and not the JIT's one-off work.
        var warmup = new PayloadAcc();
        Assert.Null(warmup.Blob(64, 0, chunk, 0, chunk.Length, AnyLength));
        Assert.Null(warmup.String(64, 0, chunk, 0, chunk.Length, AnyLength));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(acc.Blob(int.MaxValue, 0, chunk, 0, chunk.Length, AnyLength));
        Assert.Null(acc.String(int.MaxValue, 0, chunk, 0, chunk.Length, AnyLength));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 4096, "allocated " + allocated + " bytes for 16 delivered");
    }

    // --- through the decoder --------------------------------------------------

    /// <summary>
    /// What a generated visitor does with a string field, using nothing but the
    /// support layer: hold one accumulator, pass the callback through, route the
    /// value when it comes back.
    /// </summary>
    private sealed class StringFieldVisitor : IVisitor
    {
        private readonly PayloadAcc _acc = new();

        public readonly List<string> Values = new();

        public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            string? value = _acc.String(total, offset, data, chunkOffset, chunkLength, AnyLength);
            if (value is null)
            {
                return; // more chunks to come
            }
            Values.Add(value);
        }
    }

    [Fact]
    public void TheSupportLayerDecodesAStringFieldAtEveryChunkSize()
    {
        // End to end: a real message through IStream, fed in slices of every size.
        // The payload is far larger than most slices, so it reaches the visitor in
        // as many chunks as the transport dictates.
        string text = string.Concat(Enumerable.Repeat(Sample, 8));
        byte[] wire = Encode(2048, os => os.WriteString(1, text));

        for (int chunk = 1; chunk <= wire.Length; chunk++)
        {
            var visitor = new StringFieldVisitor();
            var iss = new IStream();
            DecodeStatus status = DecodeStatus.Incomplete;
            for (int i = 0; i < wire.Length; i += chunk)
            {
                status = iss.Feed(wire, i, Math.Min(chunk, wire.Length - i), visitor);
            }

            Assert.Equal(DecodeStatus.Complete, status);
            Assert.Equal(new[] { text }, visitor.Values);
        }
    }

    [Fact]
    public void AnInvalidStringRejectedByTheSupportLayerIsLatchedByTheDecoder()
    {
        // The rejection is raised inside a visitor callback, which is exactly where
        // generated code raises it. IStream latches it like its own verdicts: the
        // stream is rejected and stays rejected (§5.2), which every later Feed
        // reports by throwing the same InvalidMessage again.
        byte[] payload = Utf8Bytes("hello ").Concat(Bytes(0xED, 0xA0, 0x80)).ToArray();
        // Assembled by hand: the encoder refuses an invalid UTF-8 string payload
        // (§6.4.1), and this test needs a message that carries one.
        byte[] wire = RawStringField(1, payload);

        var iss = new IStream();
        var visitor = new StringFieldVisitor();
        var e = Assert.Throws<SofabException>(() => iss.Feed(wire, visitor));

        Assert.Equal(SofabError.InvalidMessage, e.Error);
        var again = Assert.Throws<SofabException>(() => iss.Feed(wire, visitor));
        Assert.Equal(SofabError.InvalidMessage, again.Error);
        Assert.Empty(visitor.Values);
    }
}
