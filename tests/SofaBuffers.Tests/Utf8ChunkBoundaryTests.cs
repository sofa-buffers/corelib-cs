/*
 * SofaBuffers C# - UTF-8 validity is a property of the WHOLE string payload, and
 * a chunk boundary must not change the verdict (CORELIB_PLAN §6.4,
 * "Cross-chunk semantics", normative).
 *
 * The shared negative vectors (assets/test_vectors.json -> "invalid_utf8", replayed
 * by StrictUtf8Tests) all carry a payload of one to four bytes whose invalid
 * sequence starts at payload offset 0. So none of them can exercise the case this
 * file is about: an invalid sequence that starts at a payload offset at or beyond
 * everything delivered so far, i.e. one that only arrives in a LATER chunk, after
 * the consumer has already accepted a valid prefix. A consumer that validates each
 * chunk in isolation, or that decides the verdict on the first chunk, passes every
 * shared vector and still gets this wrong.
 *
 * This port itself never transcodes: IStream hands the raw wire bytes to the
 * visitor and a field nobody reads is skipped without being looked at (§6.4,
 * "skipped fields are never validated"). The strict/fatal materialization is done
 * by GENERATED code, so the visitors below model exactly that generated step --
 * the same framing StrictUtf8Tests uses -- and the obligations under test are:
 *
 *   1. the invalid sequence is found wherever it lands in the payload, including
 *      past the first chunk (the gap above);
 *   2. the verdict is identical for every possible chunking, including one-shot;
 *   3. a multi-byte sequence split at an end-of-CHUNK is a well-formed prefix ->
 *      INCOMPLETE, never INVALID; the same sequence truncated at end-of-PAYLOAD
 *      is INVALID;
 *   4. a byte that can never be valid is still reported only at payload
 *      completion, not at the byte;
 *   5. a payload nobody materializes is never validated at all.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class Utf8ChunkBoundaryTests
{
    /// <summary>A lone UTF-16 surrogate code point U+D800: never valid UTF-8.</summary>
    private static readonly byte[] LoneSurrogate = { 0xED, 0xA0, 0x80 };

    /// <summary>U+1F600 GRINNING FACE, a well-formed four-byte sequence.</summary>
    private static readonly byte[] Grinning = { 0xF0, 0x9F, 0x98, 0x80 };

    /// <summary>Payload offset at which every payload below plants its defect.</summary>
    private const int DefectOffset = 32;

    /// <summary>ASCII filler, so the interesting bytes sit at a known offset.</summary>
    private static byte[] Filler(int n) => Enumerable.Repeat((byte)'a', n).ToArray();

    private static byte[] Payload(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Wire message: one string field, id 1, carrying <paramref name="payload"/> verbatim.</summary>
    /// <remarks>
    /// Assembled by hand rather than by the encoder, because the whole point is a
    /// payload no C# string can hold: <c>WriteString</c> refuses it, and so does
    /// <c>WriteFixlen(..., FixlenType.String)</c> (correctly, §6.4.1). The framing
    /// is ordinary and legal; only the payload is malformed.
    /// </remarks>
    private static byte[] StringMessage(byte[] payload) => RawStringField(1, payload);

    // --- the models of generated code --------------------------------------

    /// <summary>
    /// Models the generated decode step for a <c>string</c> field: collect the
    /// declared payload, then materialize it once with the strict/fatal decoder.
    /// Materializing at payload completion is what §6.4 requires -- a byte that
    /// cannot begin or continue a sequence is malformed regardless of what
    /// follows, but the verdict is reported when the payload ends, not at the byte.
    /// </summary>
    private sealed class StrictStringVisitor : IVisitor
    {
        private static readonly UTF8Encoding Strict =
            new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly MemoryStream _acc = new();

        /// <summary>(payload offset, chunk length, declared total) per callback.</summary>
        public readonly List<(int Offset, int Length, int Total)> Chunks = new();

        /// <summary>The materialized value, once the payload completed and was valid.</summary>
        public string? Value;

        /// <summary>How many payloads reached completion (valid or not).</summary>
        public int Completions;

        public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            Chunks.Add((offset, chunkLength, total));
            _acc.Write(data, chunkOffset, chunkLength);
            if (_acc.Length < total)
            {
                return; // more of this payload is still to come
            }
            Completions++;
            byte[] full = _acc.ToArray();
            _acc.SetLength(0);
            try
            {
                Value = Strict.GetString(full);
            }
            catch (DecoderFallbackException e)
            {
                // What generated code raises: the §5.2 INVALID outcome (§6.4).
                throw new SofabException(SofabError.InvalidMessage, "invalid utf-8: " + e.Message);
            }
        }
    }

    /// <summary>A consumer that materializes nothing: every field is skipped.</summary>
    private sealed class SkipEverythingVisitor : IVisitor
    {
    }

    // --- driving helpers ----------------------------------------------------

    /// <summary>
    /// Feed <paramref name="wire"/> in <paramref name="chunk"/>-sized slices and
    /// return the verdict as a string: the materialized value, or "invalid", or
    /// "incomplete" when the bytes ran out mid-message.
    /// </summary>
    private static string Verdict(byte[] wire, int chunk, StrictStringVisitor visitor)
    {
        var iss = new IStream();
        DecodeStatus status = DecodeStatus.Complete;
        for (int i = 0; i < wire.Length; i += chunk)
        {
            int n = Math.Min(chunk, wire.Length - i);
            try
            {
                status = iss.Feed(wire, i, n, visitor);
            }
            catch (SofabException e) when (e.Error == SofabError.InvalidMessage)
            {
                // Terminal, and latched: the next Feed re-reports the same verdict
                // rather than resuming, even fed nothing at all.
                var again = Assert.Throws<SofabException>(
                    () => iss.Feed(Array.Empty<byte>(), visitor));
                Assert.Equal(SofabError.InvalidMessage, again.Error);
                return "invalid";
            }
        }
        return status == DecodeStatus.Complete ? "ok:" + visitor.Value : "incomplete";
    }

    private static string Verdict(byte[] wire, int chunk) =>
        Verdict(wire, chunk, new StrictStringVisitor());

    // --- 1. the gap: an invalid sequence that only arrives in a later chunk ---

    [Fact]
    public void InvalidSequenceStartingBeyondTheDeliveredPrefixIsStillRejected()
    {
        // 32 valid bytes, then the lone surrogate, then 32 more valid bytes: the
        // defect starts at payload offset 32, so with 8-byte chunks the consumer
        // has already accepted four chunks of perfectly good UTF-8 before it
        // arrives. No shared vector reaches this shape (they are 1-4 bytes long
        // and defective from offset 0).
        byte[] payload = Payload(Filler(DefectOffset), LoneSurrogate, Filler(32));
        byte[] wire = StringMessage(payload);

        var visitor = new StrictStringVisitor();
        Assert.Equal("invalid", Verdict(wire, 8, visitor));

        // Prove the case really is the one the vectors miss: the chunk carrying
        // the first defective byte began at a payload offset that is at or past
        // everything delivered before it, and that "everything" was not nothing.
        int delivered = 0;
        bool found = false;
        foreach ((int offset, int length, int total) in visitor.Chunks)
        {
            Assert.Equal(payload.Length, total);   // the declared length never moves
            Assert.Equal(delivered, offset);       // offset is the running payload offset
            if (offset <= DefectOffset && DefectOffset < offset + length)
            {
                Assert.True(delivered > 0, "the defect must not be in the first chunk");
                Assert.True(DefectOffset >= delivered, "defect starts at or past the fed total");
                found = true;
                break;
            }
            delivered += length;
        }
        Assert.True(found, "no chunk carried the defective byte");
    }

    [Fact]
    public void SkippedInvalidPayloadIsNeverValidated()
    {
        // §6.4: validation runs only where a string is MATERIALIZED. A consumer
        // that reads no string at all sees a COMPLETE message, defect or not.
        byte[] wire = StringMessage(Payload(Filler(DefectOffset), LoneSurrogate, Filler(32)));

        var iss = new IStream();
        Assert.Equal(DecodeStatus.Complete, iss.Feed(wire, new SkipEverythingVisitor()));
    }

    // --- 2. the verdict is chunking-independent ------------------------------

    [Fact]
    public void EveryChunkingGivesTheSameVerdictAsOneShot()
    {
        // §6.4: "a chunk boundary MUST NOT affect the outcome". Checked at every
        // chunk size from one byte up to the whole message, for a valid payload
        // and for one whose defect sits deep inside it -- so every possible
        // boundary, including one that lands inside the defective sequence, is
        // exercised on both sides of the accept/reject line.
        var cases = new (string Name, byte[] Payload, string Expected)[]
        {
            ("valid", Payload(Filler(DefectOffset), Grinning, Filler(8)),
                "ok:" + Encoding.UTF8.GetString(Payload(Filler(DefectOffset), Grinning, Filler(8)))),
            ("surrogate at 32", Payload(Filler(DefectOffset), LoneSurrogate, Filler(8)), "invalid"),
            ("0xFF at 32", Payload(Filler(DefectOffset), new byte[] { 0xFF }, Filler(8)), "invalid"),
            ("bare continuation at 32", Payload(Filler(DefectOffset), new byte[] { 0x80 }, Filler(8)), "invalid"),
            ("overlong NUL at 32", Payload(Filler(DefectOffset), new byte[] { 0xC0, 0x80 }, Filler(8)), "invalid"),
        };

        foreach ((string name, byte[] payload, string expected) in cases)
        {
            byte[] wire = StringMessage(payload);
            for (int chunk = 1; chunk <= wire.Length; chunk++)
            {
                Assert.Equal(expected, Verdict(wire, chunk));
            }
        }
    }

    // --- 3. split at end-of-chunk vs truncated at end-of-payload -------------

    [Fact]
    public void SequenceSplitAtEndOfChunkIsIncompleteNotInvalid()
    {
        // A four-byte sequence cut by a chunk boundary is a well-formed prefix:
        // INCOMPLETE, and the consumer must not have judged anything yet. Folding
        // this into INVALID is the §5.2 anti-folding violation §6.4 names.
        byte[] payload = Payload(Filler(2), Grinning);
        byte[] wire = StringMessage(payload);

        var visitor = new StrictStringVisitor();
        var iss = new IStream();

        // Stop two bytes into the emoji.
        int cut = wire.Length - 2;
        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(wire, 0, cut, visitor));
        Assert.Equal(0, visitor.Completions);
        Assert.Null(visitor.Value);

        Assert.Equal(DecodeStatus.Complete, iss.Feed(wire, cut, wire.Length - cut, visitor));
        Assert.Equal(1, visitor.Completions);
        Assert.Equal("aa\U0001F600", visitor.Value);
    }

    [Fact]
    public void SequenceTruncatedAtEndOfPayloadIsInvalid()
    {
        // The same emoji, but the declared length stops one byte short of it: no
        // further byte belongs to this string, so the sequence can never complete
        // and the payload is INVALID (§6.4) -- not INCOMPLETE. The wire itself is
        // well-formed; it is the materialization that fails.
        byte[] payload = Payload(Filler(2), Grinning.Take(3).ToArray());
        byte[] wire = StringMessage(payload);

        Assert.Equal("invalid", Verdict(wire, wire.Length)); // one shot
        Assert.Equal("invalid", Verdict(wire, 1));           // byte at a time
    }

    // --- 4. the verdict lands at payload completion, not at the byte ---------

    [Fact]
    public void MalformedByteIsNotReportedBeforePayloadCompletion()
    {
        // 0xFF can never begin or continue a sequence, but §6.4 pins the TIMING:
        // "the verdict is still reported at payload completion, not before ... a
        // decoder MUST NOT report INVALID mid-payload for such a byte while the
        // declared length has not been reached". Fed one byte at a time, every
        // feed before the payload's last byte must be INCOMPLETE and silent.
        byte[] payload = Payload(Filler(4), new byte[] { 0xFF }, Filler(11));
        byte[] wire = StringMessage(payload);

        var visitor = new StrictStringVisitor();
        var iss = new IStream();
        int rejectedAt = -1;
        for (int i = 0; i < wire.Length; i++)
        {
            try
            {
                DecodeStatus status = iss.Feed(wire, i, 1, visitor);
                Assert.Equal(i == wire.Length - 1 ? DecodeStatus.Complete : DecodeStatus.Incomplete, status);
            }
            catch (SofabException e) when (e.Error == SofabError.InvalidMessage)
            {
                rejectedAt = i;
                break;
            }
        }

        // The rejection lands exactly on the byte that completes the payload --
        // the last byte of the message -- and not on the 0xFF eleven bytes earlier.
        Assert.Equal(wire.Length - 1, rejectedAt);
        Assert.Equal(1, visitor.Completions);
    }
}
