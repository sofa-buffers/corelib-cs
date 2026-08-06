/*
 * SofaBuffers C# - fixlen header announcement (issue #53).
 *
 * CORELIB_PLAN §5.2 makes INVALID dominate INCOMPLETE: once the bytes seen are
 * already malformed, running out of input cannot downgrade the verdict. A schema
 * `maxlen` violation is fully established by the fixlen LENGTH WORD -- the number
 * that exceeds the bound is on the wire, and no later byte can make it legal.
 *
 * Generated code could not latch that, because it never saw the length word on
 * its own: the only callback carrying `total` sat in the payload loop, so a
 * message ending exactly at its length word delivered NO visitor event at all and
 * degraded to INCOMPLETE -- while the same bytes with one payload byte appended
 * were INVALID. A chunk-boundary-dependent outcome, which §6.4 and §7.2 forbid.
 *
 * IVisitor.FixlenBegin closes that: announced once per fixlen field, after the
 * word is read and validated, before any payload byte. It is the ArrayBegin of
 * the fixlen world (see FixlenArrayHeaderOrderTests for that one).
 *
 * The tests come in two layers:
 *   - corelib-level: where the hook fires, how often, and with what;
 *   - a visitor that MODELS the generated code for `note : string maxlen 8`,
 *     where the CONTROLS are the point: an in-bound field truncated at the same
 *     byte must stay INCOMPLETE. This is an ORDERING fix, not a blanket reject.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using Xunit;

namespace SofaBuffers.Tests;

public class FixlenHeaderOrderTests
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

    /// <summary>A fixlen field at id 3: header `1a`, then the given length word.</summary>
    private static byte[] Field(int word, int payloadBytes) =>
        Concat(Bytes(0x1A, word), new byte[payloadBytes]);

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var outp = new byte[a.Length + b.Length];
        Array.Copy(a, 0, outp, 0, a.Length);
        Array.Copy(b, 0, outp, a.Length, b.Length);
        return outp;
    }

    // --- corelib-level: where the hook fires, and with what ------------------

    /// <summary>
    /// THE PRIMARY VECTOR. A message whose only field is a string truncated to
    /// end exactly at its length word must still announce the field. Before the
    /// fix this recorded nothing at all -- whole or one byte at a time.
    /// </summary>
    [Fact]
    public void FieldEndingAtItsLengthWordIsStillAnnounced()
    {
        // `1a` = FIXLEN id 3, `52` = string (subtype 2), length 10. No payload.
        byte[] msg = Bytes(0x1A, 0x52);

        foreach (Recorder r in Both(msg))
        {
            Assert.Equal(DecodeStatus.Incomplete, r.Status);
            Assert.Equal(new[] { "begin:3:String:10" }, r.Events);
        }
    }

    /// <summary>
    /// The hook fires before the payload, not with it: feeding the word and then
    /// the payload shows the announcement landing on the first Feed.
    /// </summary>
    [Fact]
    public void HookPrecedesEveryPayloadByte()
    {
        var r = new Recorder();
        var iss = new IStream();

        Assert.Equal(DecodeStatus.Incomplete, iss.Feed(Bytes(0x1A, 0x1A), r)); // string, length 3
        Assert.Equal(new[] { "begin:3:String:3" }, r.Events);

        Assert.Equal(DecodeStatus.Complete, iss.Feed(Bytes(0x61, 0x62, 0x63), r));
        Assert.Equal(new[] { "begin:3:String:3", "str:3:total=3:off=0:len=3" }, r.Events);
    }

    /// <summary>
    /// Once per field, never per chunk: a payload split across three Feeds is
    /// announced once and delivered three times.
    /// </summary>
    [Fact]
    public void HookFiresOncePerFieldNotPerChunk()
    {
        var r = new Recorder();
        var iss = new IStream();
        iss.Feed(Bytes(0x1A, 0x2B), r); // blob (subtype 3), length 5
        iss.Feed(Bytes(0x01, 0x02), r);
        iss.Feed(Bytes(0x03), r);
        Assert.Equal(DecodeStatus.Complete, iss.Feed(Bytes(0x04, 0x05), r));

        Assert.Equal(
            new[]
            {
                "begin:3:Blob:5",
                "blob:3:total=5:off=0:len=2",
                "blob:3:total=5:off=2:len=1",
                "blob:3:total=5:off=3:len=2",
            },
            r.Events);
    }

    /// <summary>
    /// <c>total == 0</c> is announced too -- the empty string/blob arm already
    /// emitted a payload callback without payload bytes, and the header hook must
    /// not be the one case that goes missing.
    /// </summary>
    [Theory]
    [InlineData(0x02, "String")] // subtype 2, length 0
    [InlineData(0x03, "Blob")]   // subtype 3, length 0
    public void ZeroLengthFieldIsAnnouncedBeforeItsEmptyChunk(int word, string subtype)
    {
        foreach (Recorder r in Both(Bytes(0x1A, word)))
        {
            Assert.Equal(DecodeStatus.Complete, r.Status);
            Assert.Equal(2, r.Events.Count);
            Assert.Equal("begin:3:" + subtype + ":0", r.Events[0]);
            Assert.StartsWith(subtype == "String" ? "str:" : "blob:", r.Events[1]);
        }
    }

    /// <summary>
    /// Floats are fixlen fields too, and their word is announced on the same
    /// terms: subtype and the 4 / 8 byte length the format fixes for them.
    /// </summary>
    [Fact]
    public void FloatFieldsAreAnnouncedWithTheirWidth()
    {
        foreach (Recorder r in Both(Field(0x20, 4))) // fp32 (subtype 0), length 4
        {
            Assert.Equal(DecodeStatus.Complete, r.Status);
            Assert.Equal(new[] { "begin:3:Fp32:4", "f32:3=0" }, r.Events);
        }

        foreach (Recorder r in Both(Field(0x41, 8))) // fp64 (subtype 1), length 8
        {
            Assert.Equal(DecodeStatus.Complete, r.Status);
            Assert.Equal(new[] { "begin:3:Fp64:8", "f64:3=0" }, r.Events);
        }
    }

    /// <summary>
    /// A float truncated after its word is announced as well: §4.6 already
    /// requires the width to be judged at the word, so the receiver must be able
    /// to see the word there too.
    /// </summary>
    [Fact]
    public void FloatEndingAtItsLengthWordIsStillAnnounced()
    {
        foreach (Recorder r in Both(Bytes(0x1A, 0x20)))
        {
            Assert.Equal(DecodeStatus.Incomplete, r.Status);
            Assert.Equal(new[] { "begin:3:Fp32:4" }, r.Events);
        }
    }

    /// <summary>
    /// What the FORMAT rejects is judged before the hook, exactly as for
    /// ArrayBegin: a reserved subtype (§4.6) and a wrong-width float are INVALID,
    /// and nothing is announced -- these are never routed to a §7.3 skip the
    /// receiver gets a say in.
    /// </summary>
    [Theory]
    [InlineData(0x24)] // subtype 4, reserved
    [InlineData(0x25)] // subtype 5, reserved
    [InlineData(0x26)] // subtype 6, reserved
    [InlineData(0x27)] // subtype 7, reserved
    [InlineData(0x28)] // fp32 with length 5
    [InlineData(0x40)] // fp32 with length 8
    [InlineData(0x21)] // fp64 with length 4
    public void IllegalWordIsInvalidAndNeverAnnounced(int word)
    {
        byte[] msg = Field(word, 8);

        var whole = new Recorder();
        Assert.Equal(
            SofabError.InvalidMessage,
            Assert.Throws<SofabException>(() => new IStream().Feed(msg, whole)).Error);
        Assert.Empty(whole.Events);

        var split = new Recorder();
        var iss = new IStream();
        Assert.Equal(
            SofabError.InvalidMessage,
            Assert.Throws<SofabException>(() =>
            {
                foreach (byte b in msg)
                {
                    iss.Feed(new[] { b }, split);
                }
            }).Error);
        Assert.Empty(split.Events);
    }

    /// <summary>
    /// The FORMAT ceiling on the length (§4.6, 2^31-1) still fires on the word,
    /// ahead of the hook: an absurd length is INVALID with nothing announced and
    /// nothing allocated. Moving the announcement must not drag the ceiling
    /// with it.
    /// </summary>
    [Fact]
    public void FixlenMaxCeilingStillFiresAheadOfTheHook()
    {
        // length 2^31 (one past the ceiling), subtype 2: word = (2^31 << 3) | 2.
        byte[] msg = Bytes(0x1A, 0x82, 0x80, 0x80, 0x80, 0x40);

        var whole = new Recorder();
        Assert.Equal(
            SofabError.InvalidMessage,
            Assert.Throws<SofabException>(() => new IStream().Feed(msg, whole)).Error);
        Assert.Empty(whole.Events);

        var split = new Recorder();
        var iss = new IStream();
        Assert.Equal(
            SofabError.InvalidMessage,
            Assert.Throws<SofabException>(() =>
            {
                foreach (byte b in msg)
                {
                    iss.Feed(new[] { b }, split);
                }
            }).Error);
        Assert.Empty(split.Events);
    }

    /// <summary>
    /// A fixlen ARRAY keeps its own hook: its shared fixlen_word announces the
    /// array through ArrayBegin, whose kind already names the element subtype.
    /// One field gets one header hook, not two.
    /// </summary>
    [Fact]
    public void FixlenArrayIsAnnouncedByArrayBeginOnly()
    {
        // `1d` = ARRAY_FIXLEN id 3, count 2, fp32 word, two 4-byte elements.
        foreach (Recorder r in Both(Concat(Bytes(0x1D, 0x02, 0x20), new byte[8])))
        {
            Assert.Equal(DecodeStatus.Complete, r.Status);
            Assert.Equal(new[] { "arr:3:Fp32:2", "f32:3=0", "f32:3=0" }, r.Events);
        }
    }

    /// <summary>
    /// A field announced mid-stream is announced once even when its header, its
    /// word and its payload all land in different Feeds, and the id in the hook
    /// is the field's own -- not a neighbour's.
    /// </summary>
    [Fact]
    public void HookSurvivesAHeaderSplitAcrossFeeds()
    {
        // id 300 needs a two-byte header varint: (300 << 3) | 2 = 0x962 -> e2 12.
        var r = new Recorder();
        var iss = new IStream();
        iss.Feed(Bytes(0xE2), r);
        Assert.Empty(r.Events);
        iss.Feed(Bytes(0x12), r);
        Assert.Empty(r.Events); // header complete, the word has not arrived
        iss.Feed(Bytes(0x12), r); // string, length 2
        Assert.Equal(new[] { "begin:300:String:2" }, r.Events);
        Assert.Equal(DecodeStatus.Complete, iss.Feed(Bytes(0x68, 0x69), r));
        Assert.Equal(new[] { "begin:300:String:2", "str:300:total=2:off=0:len=2" }, r.Events);
    }

    // --- a model of the generated code for `note : string maxlen 8` ---------

    /// <summary>
    /// THE VECTOR THE ISSUE IS ABOUT. An over-maxlen string truncated to end
    /// exactly at its length word is INVALID, because the word alone settles it
    /// (§5.2). Before the fix the receiver was never called and the verdict
    /// degraded to INCOMPLETE.
    /// </summary>
    [Fact]
    public void OverMaxlenTruncatedAtTheWordIsInvalid()
    {
        // string, length 10 > maxlen 8, and not one payload byte on the wire.
        byte[] msg = Bytes(0x1A, 0x52);
        Assert.Equal(SofabError.InvalidMessage, FeedWholeExpectingThrow(msg).Error);
        Assert.Equal(SofabError.InvalidMessage, FeedByteAtATimeExpectingThrow(msg).Error);
    }

    /// <summary>
    /// THE CONTROL. The same truncation with an IN-BOUND length stays INCOMPLETE:
    /// this reorders the bound, it does not turn truncation into rejection.
    /// </summary>
    [Fact]
    public void InBoundTruncatedAtTheWordStaysIncomplete()
    {
        byte[] msg = Bytes(0x1A, 0x42); // string, length 8 == maxlen
        foreach (MaxlenSlot g in BothGenerated(msg))
        {
            Assert.Equal(DecodeStatus.Incomplete, g.Status);
            Assert.Equal(1, g.Begins); // asked, and it said yes
            Assert.Null(g.Value);      // no payload yet, so nothing materialized
        }
    }

    /// <summary>
    /// THE CHUNK-INDEPENDENCE CONTROL, and the reason the hook has to exist. The
    /// over-maxlen field is INVALID at every truncation of the same message --
    /// with zero payload bytes just as with all of them. Before the fix the
    /// verdict flipped on where the input happened to be cut (§6.4, §7.2).
    /// </summary>
    [Fact]
    public void VerdictDoesNotDependOnWhereTheInputWasCut()
    {
        byte[] full = Field(0x52, 10); // string, length 10 > maxlen 8

        for (int cut = 2; cut <= full.Length; cut++)
        {
            byte[] msg = full[..cut];
            Assert.Equal(SofabError.InvalidMessage, FeedWholeExpectingThrow(msg).Error);
            Assert.Equal(SofabError.InvalidMessage, FeedByteAtATimeExpectingThrow(msg).Error);
        }
    }

    /// <summary>
    /// The in-bound counterpart of the sweep: a legal field is accepted at every
    /// truncation, INCOMPLETE until its last payload byte and COMPLETE after it.
    /// </summary>
    [Fact]
    public void InBoundFieldIsAcceptedAtEveryTruncation()
    {
        byte[] full = Field(0x42, 8); // string, length 8 == maxlen

        for (int cut = 2; cut <= full.Length; cut++)
        {
            foreach (MaxlenSlot g in BothGenerated(full[..cut]))
            {
                Assert.Equal(
                    cut == full.Length ? DecodeStatus.Complete : DecodeStatus.Incomplete,
                    g.Status);
                Assert.Equal(1, g.Begins);
            }
        }
        foreach (MaxlenSlot g in BothGenerated(full))
        {
            Assert.Equal(new byte[8], g.Value);
        }
    }

    /// <summary>
    /// §7.3: the subtype that arrived is not the subtype that was declared, so
    /// the field is not this field's value -- skip it, and do NOT apply the
    /// maxlen bound to it, exactly as for a mistyped ArrayBegin. The corelib's
    /// share is reporting the arrived subtype faithfully so the receiver can tell.
    /// </summary>
    [Fact]
    public void MistypedOverMaxlenFieldIsSkippedNotRejected()
    {
        // blob (subtype 3), length 10 -- over the string field's maxlen 8, but it
        // is not a string, so the bound never applies.
        foreach (MaxlenSlot g in BothGenerated(Field(0x53, 10)))
        {
            Assert.Equal(DecodeStatus.Complete, g.Status);
            Assert.Equal(1, g.Begins);
            Assert.Equal(FixlenType.Blob, g.LastSubtype);
            Assert.Null(g.Value); // §7.4: a skipped occurrence is not an occurrence
        }
    }

    /// <summary>
    /// And the same mistyped field truncated at its word is INCOMPLETE, not
    /// INVALID: the bound that would have condemned it was never its bound.
    /// </summary>
    [Fact]
    public void MistypedOverMaxlenFieldTruncatedAtTheWordIsIncomplete()
    {
        foreach (MaxlenSlot g in BothGenerated(Bytes(0x1A, 0x53)))
        {
            Assert.Equal(DecodeStatus.Incomplete, g.Status);
            Assert.Equal(1, g.Begins);
            Assert.Null(g.Value);
        }
    }

    /// <summary>
    /// A skipped occurrence does not clear an earlier good one (§7.4), and the
    /// hook fires once per occurrence rather than once per id.
    /// </summary>
    [Fact]
    public void SkippedLaterOccurrenceDoesNotClearAnEarlierValue()
    {
        var msg = new List<byte>();
        msg.AddRange(Field(0x12, 2));  // string, length 2 -- lands in the field
        msg.AddRange(Field(0x53, 10)); // blob, length 10 -- skipped, bound not applied

        foreach (MaxlenSlot g in BothGenerated(msg.ToArray()))
        {
            Assert.Equal(DecodeStatus.Complete, g.Status);
            Assert.Equal(2, g.Begins);
            Assert.Equal(new byte[2], g.Value);
        }
    }

    // --- harness ------------------------------------------------------------

    /// <summary>Decode <paramref name="msg"/> whole and byte-at-a-time; both must agree.</summary>
    private static IEnumerable<Recorder> Both(byte[] msg)
    {
        var whole = new Recorder();
        whole.Status = new IStream().Feed(msg, whole);
        yield return whole;

        var split = new Recorder();
        var iss = new IStream();
        foreach (byte b in msg)
        {
            split.Status = iss.Feed(new[] { b }, split);
        }
        yield return split;
    }

    private static IEnumerable<MaxlenSlot> BothGenerated(byte[] msg)
    {
        var whole = new MaxlenSlot();
        whole.Status = new IStream().Feed(msg, whole);
        yield return whole;

        var split = new MaxlenSlot();
        var iss = new IStream();
        foreach (byte b in msg)
        {
            split.Status = iss.Feed(new[] { b }, split);
        }
        yield return split;
    }

    private static SofabException FeedWholeExpectingThrow(byte[] msg) =>
        Assert.Throws<SofabException>(() => new IStream().Feed(msg, new MaxlenSlot()));

    private static SofabException FeedByteAtATimeExpectingThrow(byte[] msg)
    {
        var sink = new MaxlenSlot();
        var iss = new IStream();
        return Assert.Throws<SofabException>(() =>
        {
            foreach (byte b in msg)
            {
                iss.Feed(new[] { b }, sink);
            }
        });
    }

    /// <summary>
    /// Records the header hook alongside the payload callbacks, without
    /// coalescing chunks — the ORDER and the CHUNK COUNT are what is under test.
    /// </summary>
    private sealed class Recorder : IVisitor
    {
        public readonly List<string> Events = new();
        public DecodeStatus Status;

        public void FixlenBegin(int id, FixlenType subtype, int total) =>
            Events.Add("begin:" + id + ":" + subtype + ":" + total);

        public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength) =>
            Events.Add("str:" + id + ":total=" + total + ":off=" + offset + ":len=" + chunkLength);

        public void Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength) =>
            Events.Add("blob:" + id + ":total=" + total + ":off=" + offset + ":len=" + chunkLength);

        public void Fp32(int id, float value) => Events.Add("f32:" + id + "=" + value);

        public void Fp64(int id, double value) => Events.Add("f64:" + id + "=" + value);

        public void ArrayBegin(int id, ArrayKind kind, int count) =>
            Events.Add("arr:" + id + ":" + kind + ":" + count);
    }

    /// <summary>
    /// Models the code the generator emits for <c>note{3} : string maxlen 8</c>.
    /// The whole point of the hook is that this shape is expressible: the subtype
    /// guard comes FIRST (§7.3), the maxlen bound lives inside the matching arm
    /// and is applied to <c>total</c> at the word (§5.2) — not to bytes as they
    /// trickle in.
    /// </summary>
    private sealed class MaxlenSlot : IVisitor
    {
        private const int Maxlen = 8;

        private byte[]? _pending;
        private int _filled;

        /// <summary>The decoded `note`, or null while at its default.</summary>
        public byte[]? Value;
        public DecodeStatus Status;
        public int Begins;
        public FixlenType LastSubtype;

        public void FixlenBegin(int id, FixlenType subtype, int total)
        {
            _pending = null;
            if (id != 3)
            {
                return;
            }
            Begins++;
            LastSubtype = subtype;

            // §7.3: a subtype that contradicts the declared one is not this
            // field's value. Skip it -- and do NOT apply the schema bound.
            if (subtype != FixlenType.String)
            {
                return;
            }

            // §5.2: the word settles the bound, so it is judged here, before a
            // single payload byte -- and therefore identically no matter where
            // the input was chunked.
            if (total > Maxlen)
            {
                throw new SofabException(SofabError.InvalidMessage, "maxlen " + total + " > " + Maxlen);
            }

            _pending = new byte[total];
            _filled = 0;
            if (total == 0)
            {
                Commit();
            }
        }

        public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            if (_pending == null)
            {
                return;
            }
            Array.Copy(data, chunkOffset, _pending, _filled, chunkLength);
            _filled += chunkLength;
            if (_filled == _pending.Length)
            {
                Commit();
            }
        }

        public void Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            // Payload of a skipped blob header: discarded, exactly as generated
            // code discards it.
        }

        private void Commit()
        {
            Value = _pending;
            _pending = null;
        }
    }
}
