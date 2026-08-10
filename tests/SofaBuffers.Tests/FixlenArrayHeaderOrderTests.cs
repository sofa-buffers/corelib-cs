/*
 * SofaBuffers C# - fixlen-array header order (issue #45, Crucible F-0042).
 *
 * CORELIB_PLAN §4.8 fixes the decode order for wire type 0b101 (ARRAY_FIXLEN):
 *
 *   1. read `element_count`, enforcing only the FORMAT ceiling ARRAY_MAX and
 *      allocating nothing on the strength of it;
 *   2. read the `fixlen_word` (element subtype + per-element length);
 *   3. if the subtype CONTRADICTS the declared element type, skip the field per
 *      MESSAGE_SPEC §7.3 -- and the schema `count` bound MUST NOT be applied,
 *      because the field was never this array's value;
 *   4. only a field that survives step 3 gets the schema bound.
 *
 * The corelib is schema-agnostic, so its share of that rule is purely the
 * ORDER and the INFORMATION it hands the receiver: IVisitor.ArrayBegin fires
 * only after the `fixlen_word` has been read and validated, and its ArrayKind
 * names the element subtype (Fp32 / Fp64) rather than a collapsed "fixlen".
 * Before this, the hook fired between the two words with a kind that could not
 * tell fp32 from fp64, so no generated code could satisfy §4.8 at all.
 *
 * The tests below come in two layers:
 *   - corelib-level: where the hook fires, and with which kind;
 *   - the measured F-0042 vectors, replayed through a visitor that MODELS the
 *     generated code for `arrays{100}.nested{10}.fp32 : array<fp32, count 5>`.
 *     The controls (rows 3, 5, 6) are the point: the schema bound is being
 *     reordered, not weakened.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using Xunit;
using SofaBuffers.Tests.Common;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class FixlenArrayHeaderOrderTests
{
    /// <summary>Concatenate <paramref name="prefix"/>, <paramref name="zeros"/> NUL payload bytes, and <paramref name="suffix"/>.</summary>
    private static byte[] Vector(byte[] prefix, int zeros, byte[] suffix)
    {
        var outp = new byte[prefix.Length + zeros + suffix.Length];
        Array.Copy(prefix, 0, outp, 0, prefix.Length);
        Array.Copy(suffix, 0, outp, prefix.Length + zeros, suffix.Length);
        return outp;
    }

    // --- corelib-level: when the hook fires, and with which kind -------------

    [Fact]
    public void FixlenArrayHookDeferredUntilAfterTheFixlenWord()
    {
        // `05` = ARRAY_FIXLEN at id 0, `08` = count 8. The subtype is still
        // unknown, so nothing may be announced yet -- this is the whole of the
        // fix: a receiver holding a schema bound must not be asked about the
        // field until it can see the element subtype.
        var v = new RecordingVisitor();
        var iss = new IStream();
        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(Bytes(0x05, 0x08), v));
        Assert.Empty(v.Events);

        // The word arrives: now, and only now, the array is announced -- once.
        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(Bytes(0x20), v));
        Assert.Equal(new[] { "arr:0:FP32:8" }, v.Events);
    }

    [Fact]
    public void FixlenArrayHookDeferredByteAtATime()
    {
        // Same thing through the byte machine rather than the fast path.
        var v = new RecordingVisitor();
        var iss = new IStream();
        iss.Feed(Bytes(0x05), v);
        Assert.Empty(v.Events);
        iss.Feed(Bytes(0x08), v);
        Assert.Empty(v.Events); // count word complete, subtype still unknown
        iss.Feed(Bytes(0x41), v);
        Assert.Equal(new[] { "arr:0:FP64:8" }, v.Events);
    }

    [Fact]
    public void FixlenArrayKindNamesTheElementSubtype()
    {
        // fp32 (word 0x20, elem_len 4) and fp64 (word 0x41, elem_len 8) must be
        // distinguishable from the header hook alone.
        var fp32 = new RecordingVisitor();
        new IStream().Feed(Bytes(0x05, 0x01, 0x20, 0x00, 0x00, 0x00, 0x00), fp32);
        Assert.Equal("arr:0:FP32:1", fp32.Events[0]);

        var fp64 = new RecordingVisitor();
        new IStream().Feed(
            Bytes(0x05, 0x01, 0x41, 0, 0, 0, 0, 0, 0, 0, 0), fp64);
        Assert.Equal("arr:0:FP64:1", fp64.Events[0]);
    }

    [Fact]
    public void HookFiresExactlyOncePerArrayNotPerElement()
    {
        // Cost invariant: one ArrayBegin for a 4-element array, not four.
        var v = new CountingSink();
        new IStream().Feed(
            Vector(Bytes(0x05, 0x04, 0x20), 16, Array.Empty<byte>()), v);
        Assert.Equal(1, v.Begins);
        Assert.Equal(4, v.Elements);
        Assert.Equal(ArrayKind.Fp32, v.LastKind);
        Assert.Equal(4, v.LastCount);
    }

    [Fact]
    public void IntegerArrayHookStillFiresOnTheCountWord()
    {
        // Wire types 0b011 / 0b100 carry no second word: their element kind is
        // fixed by the header, so their hook position is unchanged.
        var uns = new RecordingVisitor();
        Assert.Equal(DecodeStatus.Incomplete, new IStream().Feed(Bytes(0x03, 0x02), uns));
        Assert.Equal(new[] { "arr:0:UNSIGNED:2" }, uns.Events);

        var sgn = new RecordingVisitor();
        Assert.Equal(DecodeStatus.Incomplete, new IStream().Feed(Bytes(0x04, 0x02), sgn));
        Assert.Equal(new[] { "arr:0:SIGNED:2" }, sgn.Events);
    }

    [Fact]
    public void ArrayMaxCeilingStillFiresOnTheCountWord()
    {
        // §4.8 step 1: the FORMAT ceiling (2^31-1) is judged on the count word,
        // before the fixlen_word and before the hook -- so an absurd count is
        // INVALID whatever the subtype would have been, and nothing is announced
        // or allocated. Moving the hook must not drag the ceiling with it.
        // 0x80 0x80 0x80 0x80 0x08 = 2^31, one past ARRAY_MAX.
        byte[] bad = Bytes(0x05, 0x80, 0x80, 0x80, 0x80, 0x08, 0x20);
        var v = new RecordingVisitor();
        var ex = Assert.Throws<SofabException>(() => new IStream().Feed(bad, v));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
        Assert.Empty(v.Events);

        // Byte-at-a-time hits StepArrayCount rather than the fast path.
        var v2 = new RecordingVisitor();
        var iss = new IStream();
        var ex2 = Assert.Throws<SofabException>(() =>
        {
            foreach (byte b in bad)
            {
                iss.Feed(new[] { b }, v2);
            }
        });
        Assert.Equal(SofabError.InvalidMessage, ex2.Error);
        Assert.Empty(v2.Events);
    }

    [Theory]
    // A dynamic subtype in a fixlen array is a FORMAT violation (§4.8 allows
    // only fixed-width subtypes), judged BEFORE the hook fires -- it must never
    // be routed to the §7.3 skip path just because the subtype also contradicts
    // whatever the receiver declared.
    [InlineData(0x22)] // subtype 2 (string), elem_len 4
    [InlineData(0x23)] // subtype 3 (blob),   elem_len 4
    // A width mismatch is equally a format violation.
    [InlineData(0x28)] // fp32 with elem_len 5
    [InlineData(0x40)] // fp32 with elem_len 8
    [InlineData(0x21)] // fp64 with elem_len 4
    public void IllegalFixlenWordIsInvalidAndNeverAnnounced(int word)
    {
        byte[] bad = Vector(Bytes(0x05, 0x03, word), 24, Array.Empty<byte>());

        var v = new RecordingVisitor();
        var ex = Assert.Throws<SofabException>(() => new IStream().Feed(bad, v));
        Assert.Equal(SofabError.InvalidMessage, ex.Error);
        Assert.Empty(v.Events);

        var v2 = new RecordingVisitor();
        var iss = new IStream();
        var ex2 = Assert.Throws<SofabException>(() =>
        {
            foreach (byte b in bad)
            {
                iss.Feed(new[] { b }, v2);
            }
        });
        Assert.Equal(SofabError.InvalidMessage, ex2.Error);
        Assert.Empty(v2.Events);
    }

    // --- the F-0042 vectors, through a model of the generated code ----------

    // `a6 06` = SEQUENCE_START id 100 (`arrays`); `56` = SEQUENCE_START id 10
    // (`nested`); `05` = ARRAY_FIXLEN id 0, declared `array<fp32, count 5>`.
    private static readonly byte[] Slot = Bytes(0xA6, 0x06, 0x56, 0x05);
    private static readonly byte[] Close = Bytes(0x07, 0x07);

    private static byte[] Fixlen(int count, int word, int payload) =>
        Vector(Bytes(0xA6, 0x06, 0x56, 0x05, count, word), payload, Close);

    /// <summary>
    /// Row 1: count 3, fp64 word at the fp32 slot. §7.3 skip; the declared field
    /// keeps its default.
    /// </summary>
    [Fact]
    public void Row1_MistypedInCount_Skipped()
    {
        foreach (GeneratedFp32Slot g in Both(Fixlen(0x03, 0x41, 24)))
        {
            Assert.Equal(DecodeStatus.Complete, g.Status);
            Assert.Equal(1, g.Begins);
            Assert.Equal(ArrayKind.Fp64, g.LastKind);
            Assert.Equal(3, g.LastCount);
            Assert.Null(g.Value); // never assigned: skipped, not materialized
        }
    }

    /// <summary>
    /// Row 2 -- THE PRIMARY VECTOR. count 8 exceeds the declared count 5, but the
    /// fp64 word contradicts the declared fp32 FIRST, so the field is skipped and
    /// the schema bound MUST NOT be applied.
    /// </summary>
    [Fact]
    public void Row2_OvercountMistyped_SkippedNotRejected()
    {
        foreach (GeneratedFp32Slot g in Both(Fixlen(0x08, 0x41, 64)))
        {
            Assert.Equal(DecodeStatus.Complete, g.Status);
            Assert.Equal(1, g.Begins);
            Assert.Equal(ArrayKind.Fp64, g.LastKind);
            Assert.Equal(8, g.LastCount);
            Assert.Null(g.Value);
        }
    }

    /// <summary>
    /// Row 3 -- THE CONTROL. count 8 &gt; declared count 5 with a MATCHING fp32
    /// subtype stays INVALID. The bound is reordered, not weakened.
    /// </summary>
    [Fact]
    public void Row3_OvercountMatching_StaysInvalid()
    {
        byte[] msg = Fixlen(0x08, 0x20, 32);
        Assert.Equal(SofabError.InvalidMessage, FeedWholeExpectingThrow(msg).Error);
        Assert.Equal(SofabError.InvalidMessage, FeedByteAtATimeExpectingThrow(msg).Error);
    }

    /// <summary>
    /// Row 4 -- the second primary vector. EOF between the count word and the
    /// fixlen_word is INCOMPLETE: the decoder genuinely cannot yet know whether
    /// this is a field it must bound, so §5.2's precedence does not reach INVALID.
    /// </summary>
    [Fact]
    public void Row4_TruncatedBetweenWords_IsIncompleteNotInvalid()
    {
        byte[] msg = Vector(Slot, 0, Bytes(0x08));
        foreach (GeneratedFp32Slot g in Both(msg))
        {
            Assert.Equal(DecodeStatus.Incomplete, g.Status);
            Assert.Equal(0, g.Begins); // never asked about a field it cannot judge
            Assert.Null(g.Value);
        }
    }

    /// <summary>
    /// Row 5 -- THE CONTROL that kills the generator-only workarounds. Once the
    /// subtype has arrived and matches, an over-count is malformed regardless of
    /// what follows, so INVALID dominates the truncation (§5.2). No element ever
    /// arrives here, so a latched check would never fire.
    /// </summary>
    [Fact]
    public void Row5_OvercountMatchingNoPayload_IsInvalidNotIncomplete()
    {
        byte[] msg = Vector(Slot, 0, Bytes(0x08, 0x20));
        Assert.Equal(SofabError.InvalidMessage, FeedWholeExpectingThrow(msg).Error);
        Assert.Equal(SofabError.InvalidMessage, FeedByteAtATimeExpectingThrow(msg).Error);
    }

    /// <summary>
    /// Row 6 -- the happy-path control, and the only vector whose re-encode
    /// equals its input.
    /// </summary>
    [Fact]
    public void Row6_ValidControl_AcceptsAndRoundTripsByteIdentically()
    {
        byte[] msg = Fixlen(0x03, 0x20, 12);
        foreach (GeneratedFp32Slot g in Both(msg))
        {
            Assert.Equal(DecodeStatus.Complete, g.Status);
            Assert.Equal(1, g.Begins);
            Assert.Equal(ArrayKind.Fp32, g.LastKind);
            Assert.Equal(new float[] { 0f, 0f, 0f }, g.Value);
        }

        var buf = new byte[64];
        var os = new OStream(buf);
        os.WriteSequenceBeginLazy(100);
        os.WriteSequenceBeginLazy(10);
        os.WriteArrayFp32(0, new float[] { 0f, 0f, 0f });
        os.WriteSequenceEnd();
        os.WriteSequenceEnd();
        Assert.Equal(msg, buf[..os.BytesUsed]);
    }

    /// <summary>
    /// New regression vector: a zero-count fixlen array still carries its
    /// fixlen_word (§4.8), so the hook must still fire exactly once, with the
    /// correct kind, after the word -- the case a careless call-site move drops.
    /// </summary>
    [Fact]
    public void ZeroCountMistypedArrayStillAnnouncedOnceWithTheRightKind()
    {
        foreach (GeneratedFp32Slot g in Both(Bytes(0xA6, 0x06, 0x56, 0x05, 0x00, 0x41, 0x07, 0x07)))
        {
            Assert.Equal(DecodeStatus.Complete, g.Status);
            Assert.Equal(1, g.Begins);
            Assert.Equal(ArrayKind.Fp64, g.LastKind);
            Assert.Equal(0, g.LastCount);
            Assert.Null(g.Value); // mistyped: skipped, 0 payload bytes
        }

        // ... and a matching zero-count array still lands in the field.
        foreach (GeneratedFp32Slot g in Both(Bytes(0xA6, 0x06, 0x56, 0x05, 0x00, 0x20, 0x07, 0x07)))
        {
            Assert.Equal(DecodeStatus.Complete, g.Status);
            Assert.Equal(ArrayKind.Fp32, g.LastKind);
            Assert.Equal(Array.Empty<float>(), g.Value);
        }
    }

    /// <summary>
    /// New regression vector: a string subtype at the fp32 slot contradicts the
    /// schema AND is format-illegal. Format wins -- INVALID, not a §7.3 skip.
    /// This is the boundary the fix is most likely to over-correct.
    /// </summary>
    [Fact]
    public void IllegalSubtypeAtAMismatchedSlotIsInvalidNotSkipped()
    {
        byte[] msg = Fixlen(0x03, 0x22, 12);
        Assert.Equal(SofabError.InvalidMessage, FeedWholeExpectingThrow(msg).Error);
        Assert.Equal(SofabError.InvalidMessage, FeedByteAtATimeExpectingThrow(msg).Error);
    }

    /// <summary>
    /// New cross-check: the same reasoning one step earlier on the wire. An
    /// ARRAY_UNSIGNED header at a fixlen slot is a wire-type mismatch, so §7.3
    /// skips the field and the schema `count` bound does not apply even though
    /// count 8 &gt; 5. The corelib's share is reporting the kind faithfully.
    /// </summary>
    [Fact]
    public void IntegerArrayHeaderAtAFixlenSlotIsSkippedNotBounded()
    {
        byte[] msg = Vector(Bytes(0xA6, 0x06, 0x56, 0x03, 0x08), 8, Close);
        foreach (GeneratedFp32Slot g in Both(msg))
        {
            Assert.Equal(DecodeStatus.Complete, g.Status);
            Assert.Equal(1, g.Begins);
            Assert.Equal(ArrayKind.Unsigned, g.LastKind);
            Assert.Equal(8, g.LastCount);
            Assert.Null(g.Value);
        }
    }

    /// <summary>
    /// MESSAGE_SPEC §7.4: an occurrence skipped under §7.3 is not an occurrence,
    /// so a correctly typed earlier occurrence survives a mistyped later one at
    /// the same id.
    /// </summary>
    [Fact]
    public void SkippedLaterOccurrenceDoesNotClearAnEarlierValue()
    {
        var msg = new List<byte>();
        msg.AddRange(Bytes(0xA6, 0x06, 0x56));
        msg.AddRange(Fixlen(0x02, 0x20, 8)[3..^2]); // `05 02 20` + 8 payload bytes
        msg.AddRange(Fixlen(0x08, 0x41, 64)[3..^2]); // mistyped, over-count
        msg.AddRange(Close);

        foreach (GeneratedFp32Slot g in Both(msg.ToArray()))
        {
            Assert.Equal(DecodeStatus.Complete, g.Status);
            Assert.Equal(2, g.Begins);
            Assert.Equal(new float[] { 0f, 0f }, g.Value); // the fp32 occurrence survives
        }
    }

    // --- harness ------------------------------------------------------------

    /// <summary>Decode <paramref name="msg"/> whole and byte-at-a-time; both must agree.</summary>
    private static IEnumerable<GeneratedFp32Slot> Both(byte[] msg)
    {
        var whole = new GeneratedFp32Slot();
        whole.Status = new IStream().Feed(msg, whole);
        yield return whole;

        var split = new GeneratedFp32Slot();
        var iss = new IStream();
        foreach (byte b in msg)
        {
            split.Status = iss.Feed(new[] { b }, split);
        }
        yield return split;
    }

    private static SofabException FeedWholeExpectingThrow(byte[] msg) =>
        Assert.Throws<SofabException>(() => new IStream().Feed(msg, new GeneratedFp32Slot()));

    private static SofabException FeedByteAtATimeExpectingThrow(byte[] msg)
    {
        var sink = new GeneratedFp32Slot();
        var iss = new IStream();
        return Assert.Throws<SofabException>(() =>
        {
            foreach (byte b in msg)
            {
                iss.Feed(new[] { b }, sink);
            }
        });
    }

    /// <summary>Minimal sink for the once-per-array cost invariant.</summary>
    private sealed class CountingSink : IVisitor
    {
        public int Begins;
        public int Elements;
        public ArrayKind LastKind;
        public int LastCount;

        public void ArrayBegin(int id, ArrayKind kind, int count)
        {
            Begins++;
            LastKind = kind;
            LastCount = count;
        }

        public void Fp32(int id, float value) => Elements++;
        public void Fp64(int id, double value) => Elements++;
    }

    /// <summary>
    /// Models the code the generator emits for
    /// <c>arrays{100}.nested{10}.fp32 : array&lt;fp32, count 5&gt;</c>. The whole
    /// point of the ArrayBegin contract is that this shape is expressible: the
    /// subtype guard comes FIRST, and the schema `count` bound lives inside the
    /// matching arm.
    /// </summary>
    private sealed class GeneratedFp32Slot : IVisitor
    {
        private const int DeclaredCount = 5;

        private readonly List<int> _path = new();
        private List<float>? _pending;

        /// <summary>The decoded `arrays.nested.fp32`, or null while at its default.</summary>
        public float[]? Value;
        public DecodeStatus Status;
        public int Begins;
        public ArrayKind LastKind;
        public int LastCount;

        private bool AtSlot => _path.Count == 2 && _path[0] == 100 && _path[1] == 10;

        public void SequenceBegin(int id) => _path.Add(id);

        public void SequenceEnd()
        {
            if (_path.Count > 0)
            {
                _path.RemoveAt(_path.Count - 1);
            }
        }

        public void ArrayBegin(int id, ArrayKind kind, int count)
        {
            _pending = null;
            if (!AtSlot || id != 0)
            {
                return;
            }
            Begins++;
            LastKind = kind;
            LastCount = count;

            // §7.3 / §4.8 step 3: a header whose element kind contradicts the
            // declared element type is not this field's value. Skip it -- and do
            // NOT apply the schema `count` bound, and do NOT touch the field
            // (§7.4: a skipped occurrence is not an occurrence).
            if (kind != ArrayKind.Fp32)
            {
                return;
            }

            // §4.8 step 4: only a field that survives step 3 gets the bound.
            if (count > DeclaredCount)
            {
                throw new SofabException(SofabError.InvalidMessage, "count " + count + " > " + DeclaredCount);
            }

            _pending = new List<float>(count);
            if (count == 0)
            {
                Commit();
            }
        }

        public void Fp32(int id, float value)
        {
            if (_pending == null)
            {
                return;
            }
            _pending.Add(value);
            if (_pending.Count == LastCount)
            {
                Commit();
            }
        }

        private void Commit()
        {
            Value = _pending!.ToArray();
            _pending = null;
        }

        public void Fp64(int id, double value)
        {
            // Elements of a skipped fp64 header: discarded, exactly as generated
            // code discards them.
        }

        public void Unsigned(int id, ulong value)
        {
        }
    }
}
