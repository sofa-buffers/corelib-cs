/*
 * SofaBuffers C# - the shared `header_limits` block (CORELIB_PLAN §6.2.1, §6.3).
 *
 * The block carries the TRUNCATED OVER-CEILING HEADER: bytes that *declare* a
 * length or a count and then END, with not one payload byte behind them.
 *
 *   02 a2 06   then EOF
 *   ^^ id 0, wire type 2 (fixlen)
 *      ^^^^^ the length word (100 << 3) | 2 -- a 100-byte STRING is declared
 *              ... and the message ends.
 *
 * A conformant decoder answers AT THAT WORD, before the payload is asked for, so
 * the answer is the ceiling's and it is TERMINAL. INCOMPLETE is the wrong answer:
 * MESSAGE_SPEC §5.2.1 defines it as the outcome more bytes CAN change, §5.2.4 has
 * a streaming caller read it as "feed me the next chunk", and after a ceiling has
 * fired both are false statements about the state -- three bytes claiming a
 * hundred would hold the connection open, which is the amplification the ceilings
 * exist to close. CORELIB_PLAN §6.3 makes the refusal terminal.
 *
 * WHICH CEILING SPEAKS IS THE SUBJECT, and the two give opposite answers on the
 * same word. A case carries `schema` or `limits`, never both, because §6.2.1
 * forbids applying a receiver cap to a field the schema already bounds:
 *
 *   "schema": { "maxlen": N }    -> a breach is INVALID         (MESSAGE_SPEC §7.1)
 *   "limits": { "max_dyn_...": N } -> a breach is LIMIT_EXCEEDED (CORELIB_PLAN §6.2.1)
 *
 * header_string_schema_bounded and header_string_over_cap carry the IDENTICAL
 * bytes and differ only in which ceiling the case configures; that pair is what
 * keeps the two categories apart, and TheBlockCarriesEveryCaseKind pins that it is
 * still in the block.
 *
 * WHERE THE CEILING LIVES IN THIS PORT, and what these cases therefore prove. The
 * corelib holds no limit -- no field, no default, no fallback constant (§6.2.1) --
 * so the numbers are generated code's and HeaderDest below stands in for that
 * layer, exactly as GrowthDest does in SequenceGrowthTests. What it does NOT
 * restate is the comparison itself for the two payload kinds: it routes the
 * declared length through PayloadAcc.CheckStringLength / CheckBlobLength, the
 * §6.2.1 comparison the library offers at the length word, from the
 * IVisitor.FixlenBegin hook the decoder raises there. So the string and blob cases
 * exercise this library's own guard and the decoder's header hook for real; the
 * array-count cap and the schema bounds are the destination's, because the corelib
 * has no call for either (see SofabError.LimitExceeded, "each rule is enforced in
 * exactly one of the two places").
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class HeaderLimitsTests
{
    private readonly ITestOutputHelper _out;

    public HeaderLimitsTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// This port's answer to the <c>receiver_caps</c> capability the block gates
    /// on.
    /// </summary>
    /// <remarks>
    /// Like <c>dynamic_arrays</c> on the growth block, and unlike the
    /// wire-construct tags a vector carries, this is a PROFILE capability: a port
    /// declares it when its generated code carries §6.2.1 receiver caps DISTINCT
    /// from schema bounds. This one does. A cap is a required argument that is
    /// never held and never defaulted (<see cref="PayloadAcc"/>), a schema bound is
    /// generated code's own number, the two are mutually exclusive per field, and a
    /// breach of one is <see cref="SofabError.LimitExceeded"/> where a breach of the
    /// other is <see cref="SofabError.InvalidMessage"/>.
    /// </remarks>
    private const bool CarriesReceiverCaps = true;

    /// <summary>
    /// Whether this port satisfies one <c>requires</c> tag of the block.
    /// </summary>
    /// <remarks>
    /// IN THIS BLOCK AN UNSATISFIED TAG MEANS SKIP, FOR EVERY TAG -- which is not
    /// how a <em>vector</em> treats one. A vector's unsatisfied wire-construct tag
    /// turns the vector into a negative case, because a build compiled without the
    /// construct rejects any message carrying it. These cases already assert a
    /// rejection WITH A SPECIFIC CATEGORY, so such a build would reject for an
    /// unrelated reason and appear to pass while testing nothing.
    /// <para>
    /// This is a full-wire-format implementation, so the construct tags
    /// (<c>fixlen</c>, <c>array</c>, <c>int64</c>) are all satisfied and only the
    /// profile tag is a real question here.
    /// </para>
    /// </remarks>
    private static bool Satisfies(string tag) => tag switch
    {
        "receiver_caps" => CarriesReceiverCaps,
        "fixlen" or "array" or "sequence" or "fp64" or "int64" => true,
        _ => false,
    };

    private static bool Runs(Case c) => c.Requires.TrueForAll(Satisfies);

    // --- the block's shape (test_vectors_README.md) --------------------------

    /// <summary>Which ceiling the case configures: exactly one of the two.</summary>
    private enum Ceiling
    {
        /// <summary>A schema <c>maxlen</c> / <c>count</c>; a breach is INVALID.</summary>
        Schema,

        /// <summary>A §6.2.1 receiver cap; a breach is LIMIT_EXCEEDED.</summary>
        Cap,
    }

    /// <summary>The construct the case's bytes open, read off the bytes themselves.</summary>
    private enum Construct
    {
        String,
        Blob,
        Array,
    }

    private sealed record Case(
        string Name,
        string Group,
        List<string> Requires,
        int FieldId,
        long Declared,
        Ceiling Kind,
        string CeilingName,
        long Bound,
        string Serialized,
        string[] Chunks,
        string Outcome,
        bool Terminal);

    /// <summary>
    /// Read the single ceiling a case states, under the key naming the bound.
    /// </summary>
    /// <remarks>
    /// A case carries <c>schema</c> or <c>limits</c> and never both -- §6.2.1
    /// forbids applying a receiver cap to a field the schema already bounds -- and
    /// the object names exactly one bound. Both are checked here rather than
    /// assumed, because a case that stated two ceilings would silently test
    /// whichever this file happened to read first.
    /// </remarks>
    private static (Ceiling Kind, string Name, long Bound) ReadCeiling(JsonElement c, string name)
    {
        bool hasSchema = c.TryGetProperty("schema", out JsonElement schema);
        bool hasLimits = c.TryGetProperty("limits", out JsonElement limits);
        if (hasSchema == hasLimits)
        {
            throw new InvalidOperationException(
                $"{name}: a case carries exactly one of `schema` and `limits` (§6.2.1)");
        }

        JsonElement owner = hasSchema ? schema : limits;
        JsonProperty[] bounds = owner.EnumerateObject().ToArray();
        if (bounds.Length != 1)
        {
            throw new InvalidOperationException(
                $"{name}: the ceiling names {bounds.Length} bounds, want exactly one");
        }
        return (hasSchema ? Ceiling.Schema : Ceiling.Cap, bounds[0].Name, bounds[0].Value.GetInt64());
    }

    private static List<Case> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "test_vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("header_limits", out JsonElement block))
        {
            throw new InvalidOperationException(
                "assets/test_vectors.json carries no header_limits block: §6.2.1/§6.3 has no corpus to run");
        }

        var cases = new List<Case>();
        foreach (JsonElement c in block.EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;

            var requires = new List<string>();
            if (c.TryGetProperty("requires", out JsonElement req))
            {
                foreach (JsonElement r in req.EnumerateArray()) { requires.Add(r.GetString()!); }
            }

            string[] chunks = c.TryGetProperty("chunks", out JsonElement ch)
                ? ch.EnumerateArray().Select(x => x.GetString()!).ToArray()
                : Array.Empty<string>();

            (Ceiling kind, string ceiling, long bound) = ReadCeiling(c, name);
            JsonElement e = c.GetProperty("expect");

            cases.Add(new Case(
                name,
                c.GetProperty("group").GetString()!,
                requires,
                c.GetProperty("field_id").GetInt32(),
                c.GetProperty("declared").GetInt64(),
                kind,
                ceiling,
                bound,
                c.GetProperty("serialized").GetString()!,
                chunks,
                e.GetProperty("outcome").GetString()!,
                e.TryGetProperty("terminal", out JsonElement tm) && tm.GetBoolean()));
        }
        if (cases.Count == 0)
        {
            throw new InvalidOperationException("header_limits block is empty");
        }
        return cases;
    }

    private static readonly List<Case> Cases = Load();

    public static TheoryData<string> CaseNames
    {
        get
        {
            var d = new TheoryData<string>();
            foreach (Case c in Cases) { d.Add(c.Name); }
            return d;
        }
    }

    private static Case ByName(string name) => Cases.Find(c => c.Name == name)!;

    // --- reading the header the case is made of ------------------------------

    /// <summary>
    /// The construct the case's bytes declare, together with the id and the
    /// length-or-count word they carry.
    /// </summary>
    /// <remarks>
    /// The bounds in this block are ABSOLUTE, not cap-relative as in
    /// <c>sequence_growth</c>: the case IS a fixed byte string, so the declared
    /// number is baked into the varint and the case instead TELLS the port which
    /// ceiling to configure. What the case does not spell out is which CONSTRUCT
    /// the header opens, so it is read off the bytes -- the one description that
    /// cannot drift from them -- and cross-checked against <c>field_id</c> and
    /// <c>declared</c>.
    /// </remarks>
    private static (Construct Kind, int Id, long Declared) ReadShape(Case c)
    {
        byte[] raw = Convert.FromHexString(c.Serialized);
        int at = 0;
        ulong header = Varint(c.Name, raw, ref at);
        int id = checked((int)(header >> 3));
        Construct kind;
        long declared;

        switch (header & 0x07)
        {
            case 0x2: // T_FIXLEN
                ulong word = Varint(c.Name, raw, ref at);
                declared = checked((long)(word >> 3));
                kind = (word & 0x07) switch
                {
                    0x2 => Construct.String,
                    0x3 => Construct.Blob,
                    _ => throw new InvalidOperationException(
                        $"{c.Name}: fixlen subtype {word & 0x07} carries no ceiling of its own"),
                };
                break;
            case 0x3: // T_VARINTARRAY_UNSIGNED
            case 0x4: // T_VARINTARRAY_SIGNED
                declared = checked((long)Varint(c.Name, raw, ref at));
                kind = Construct.Array;
                break;
            default:
                throw new InvalidOperationException(
                    $"{c.Name}: wire type {header & 0x07} opens no length or count header");
        }

        // The case states the id and the number its bytes declare; a disagreement
        // means the block was hand-edited rather than copied verbatim (§7.1, §8).
        if (id != c.FieldId)
        {
            throw new InvalidOperationException($"{c.Name}: bytes carry id {id}, the case says {c.FieldId}");
        }
        if (declared != c.Declared)
        {
            throw new InvalidOperationException(
                $"{c.Name}: bytes declare {declared}, the case says {c.Declared}");
        }
        return (kind, id, declared);
    }

    /// <summary>
    /// One varint out of the case's header. The header is complete in
    /// <c>serialized</c> even where <c>chunks</c> splits it, so a truncated varint
    /// here is a corrupt case rather than an expected outcome.
    /// </summary>
    private static ulong Varint(string name, byte[] b, ref int at)
    {
        ulong v = 0;
        int shift = 0;
        while (at < b.Length)
        {
            byte x = b[at++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) { return v; }
            shift += 7;
            if (shift > 63)
            {
                throw new InvalidOperationException($"{name}: varint wider than 64 bits");
            }
        }
        throw new InvalidOperationException($"{name}: serialized ends inside a varint");
    }

    // --- the destination the ceiling lives in --------------------------------

    /// <summary>
    /// The destination a generated message class would be for the case's one
    /// field: it judges the declared length or count at the header hook, and binds
    /// the payload behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ceiling is applied at <see cref="IVisitor.FixlenBegin"/> /
    /// <see cref="IVisitor.ArrayBegin"/> -- the word that declares the size, which
    /// §6.2.1 names as the enforcement point ("before the allocation it is meant to
    /// prevent") and which the decoder raises before any payload byte. For a string
    /// or a blob the comparison itself is not restated here: it is
    /// <see cref="PayloadAcc.CheckStringLength"/> / <see cref="PayloadAcc.CheckBlobLength"/>,
    /// the library's own §6.2.1 check, so these cases bite on the library rather
    /// than on an assertion written in this file.
    /// </para>
    /// <para>
    /// The two ceilings are wired in mutually exclusively, exactly as the case
    /// states them: a <c>schema</c> case gets a bound this destination enforces
    /// itself and NO cap, a <c>limits</c> case gets the cap and NO schema bound.
    /// Neither route can borrow the other's number.
    /// </para>
    /// </remarks>
    private sealed class HeaderDest : IVisitor
    {
        private readonly int _fieldId;
        private readonly Construct _construct;
        private readonly Ceiling _kind;
        private readonly long _bound;
        private readonly PayloadAcc _acc = new();

        /// <summary>
        /// How many values this destination actually bound. Every case in the
        /// block ends at or before the length word, so it must stay 0 -- which is
        /// only worth asserting because the counter genuinely moves when a payload
        /// does arrive (<see cref="TheDestinationBindsWhenThePayloadArrives"/>).
        /// </summary>
        internal int BoundValues { get; private set; }

        internal HeaderDest(int fieldId, Construct construct, Ceiling kind, long bound)
        {
            _fieldId = fieldId;
            _construct = construct;
            _kind = kind;
            _bound = bound;
        }

        /// <summary>The declared length judged at the length word, before any payload byte.</summary>
        public void FixlenBegin(int id, FixlenType subtype, int total)
        {
            if (id != _fieldId || !IsOurs(subtype))
            {
                // A field this destination does not read: MESSAGE_SPEC §7.3 skips
                // it, and §6.2.1 never caps a skipped field.
                return;
            }
            if (_kind == Ceiling.Schema)
            {
                // A schema bound is a statement about VALIDITY (MESSAGE_SPEC §7.1):
                // the number that exceeds it is already on the wire and no later
                // byte can make it legal, so the field is malformed, not declined.
                if (total > _bound)
                {
                    throw new SofabException(SofabError.InvalidMessage,
                        $"declared length {total} over schema maxlen {_bound}");
                }
                return;
            }
            // A receiver cap: the bytes are well-formed and the same header decodes
            // under a looser cap, so the refusal is LimitExceeded (§6.2.1, §6.3).
            if (subtype == FixlenType.String)
            {
                PayloadAcc.CheckStringLength(total, _bound);
            }
            else
            {
                PayloadAcc.CheckBlobLength(total, _bound);
            }
        }

        /// <summary>
        /// The declared element count, judged the same way and at the same point.
        /// </summary>
        /// <remarks>
        /// A COUNT ahead of its payload is bound exactly as a length is (§6.2.1) --
        /// and unlike a wrapper array, which carries no count on the wire and is
        /// bound at the element index instead (SequenceGrowthTests). The corelib
        /// offers no call for this one: array counts are generated code's
        /// throughout (see <see cref="SofabError.LimitExceeded"/>), so the
        /// comparison is here.
        /// </remarks>
        public void ArrayBegin(int id, ArrayKind kind, int count)
        {
            if (id != _fieldId || _construct != Construct.Array)
            {
                return;
            }
            if (count > _bound)
            {
                throw new SofabException(
                    _kind == Ceiling.Schema ? SofabError.InvalidMessage : SofabError.LimitExceeded,
                    _kind == Ceiling.Schema
                        ? $"declared count {count} over schema count {_bound}"
                        : $"declared count {count} over max_dyn_array_count {_bound}");
            }
        }

        public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            if (id != _fieldId || _construct != Construct.String) { return; }
            if (_acc.String(total, offset, data, chunkOffset, chunkLength, _bound) != null)
            {
                BoundValues++;
            }
        }

        public void Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            if (id != _fieldId || _construct != Construct.Blob) { return; }
            if (_acc.Blob(total, offset, data, chunkOffset, chunkLength, _bound) != null)
            {
                BoundValues++;
            }
        }

        public void Unsigned(int id, ulong value)
        {
            if (id == _fieldId && _construct == Construct.Array) { BoundValues++; }
        }

        public void Signed(int id, long value)
        {
            if (id == _fieldId && _construct == Construct.Array) { BoundValues++; }
        }

        /// <summary>
        /// Whether an arrived fixlen subtype is the one this destination reads. The
        /// corelib is schema-agnostic and reports the subtype that ARRIVED, so a
        /// receiver whose schema names another one treats the field as a
        /// MESSAGE_SPEC §7.3 skip and does not measure it against this field's
        /// bound.
        /// </summary>
        private bool IsOurs(FixlenType subtype) =>
            (_construct == Construct.String && subtype == FixlenType.String)
            || (_construct == Construct.Blob && subtype == FixlenType.Blob);
    }

    // --- feeding the case ----------------------------------------------------

    /// <summary>
    /// Feed the case -- as <c>chunks</c> where it splits the bytes, otherwise as
    /// one feed of <c>serialized</c> -- and report the outcome, or the exception
    /// that ended it.
    /// </summary>
    /// <remarks>
    /// The chunked case is not decoration: <c>header_string_over_cap_split</c>
    /// divides the LENGTH VARINT ITSELF, so the ceiling fires on a word no single
    /// feed delivered whole. The verdict is a property of the bytes and not of how
    /// they were chunked (CORELIB_PLAN §7.2 item 4).
    /// </remarks>
    private static (DecodeStatus Status, SofabException? Thrown) Feed(
        IStream istream, Case c, IVisitor dest)
    {
        string[] pieces = c.Chunks.Length > 0 ? c.Chunks : new[] { c.Serialized };
        DecodeStatus status = DecodeStatus.Complete;
        foreach (string piece in pieces)
        {
            try
            {
                status = istream.Feed(Convert.FromHexString(piece), dest);
            }
            catch (SofabException e)
            {
                return (DecodeStatus.Invalid, e);
            }
        }
        return (status, null);
    }

    /// <summary>
    /// A COMPLETE, valid message -- one unsigned field at id 0 with value 0 -- fed
    /// after a terminal rejection. It is the sharpest question available: a decoder
    /// that resumed would answer <see cref="DecodeStatus.Complete"/>, so anything
    /// but the re-raised verdict is visible.
    /// </summary>
    private static byte[] TerminalProbe() => Bytes(0x00, 0x00);

    /// <summary>
    /// A field id no case in the block uses, so a destination built on it reads
    /// nothing and can refuse nothing -- see the terminality assertion, which needs
    /// the second feed to have no ceiling of its own left to fire.
    /// </summary>
    private const int NeutralFieldId = 99;

    // --- the cases -----------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void HeaderCaseMatchesExpectation(string name)
    {
        Case c = ByName(name);
        if (!Runs(c))
        {
            // An unsatisfied tag is a SKIP here, for every tag: these cases assert
            // a rejection with a specific category, and a build that cannot
            // represent the construct would reject for an unrelated reason.
            return;
        }

        (Construct construct, _, _) = ReadShape(c);
        var dest = new HeaderDest(c.FieldId, construct, c.Kind, c.Bound);
        var istream = new IStream();
        (DecodeStatus status, SofabException? thrown) = Feed(istream, c, dest);

        switch (c.Outcome)
        {
            case "incomplete":
                // THE IN-CAP CONTROL, and not filler: the same shape at a length the
                // ceiling ADMITS. A port that rejects every short read passes every
                // rejection case in this block and is badly broken.
                Assert.True(thrown == null,
                    $"the ceiling admits {c.Bound} and the header declares {c.Declared}, "
                    + $"yet the decode was refused with {thrown?.Error}");
                Assert.Equal(DecodeStatus.Incomplete, status);
                Assert.False(c.Terminal,
                    "an INCOMPLETE case cannot be terminal: it is precisely the state more bytes lift");
                break;

            case "limit_exceeded":
                // A policy rejection, not INVALID: the bytes are well-formed and the
                // same header decodes under a looser cap (§6.2.1, §6.3).
                Assert.True(thrown != null,
                    $"outcome {status}, want LimitExceeded at the length word: the header declares "
                    + $"{c.Declared} and the cap admits {c.Bound}. Incomplete is the answer §5.2.3's "
                    + "reason exists to prevent -- it says more bytes can change the verdict, and "
                    + "after a cap has fired nothing can.");
                Assert.Equal(SofabError.LimitExceeded, thrown!.Error);
                break;

            case "invalid":
                // The schema bound is a statement about validity (MESSAGE_SPEC §7.1),
                // so the very word that is a cap breach one case over is malformed
                // here -- and must not be reported as a receiver-cap refusal, which
                // §6.2.1 forbids on a schema-bounded field in the first place.
                Assert.True(thrown != null,
                    $"outcome {status}, want InvalidMessage at the length word: the header declares "
                    + $"{c.Declared} and the schema bounds the field at {c.Bound}.");
                Assert.Equal(SofabError.InvalidMessage, thrown!.Error);
                break;

            default:
                throw new InvalidOperationException($"{c.Name}: unknown expected outcome {c.Outcome}");
        }

        if (c.Terminal)
        {
            // The rejection is terminal: a further feed RE-RAISES rather than
            // consuming (§6.3, MESSAGE_SPEC §5.2).
            //
            // The probe goes to a NEUTRAL destination -- another field id, and a
            // ceiling nothing can reach -- and that is the whole point of the
            // assertion. Fed back to `dest`, whose ceiling is still armed, a
            // decoder that did NOT latch would resume mid-field, hand the probe's
            // bytes to the same destination as payload and be refused a second
            // time with the SAME SofabError, so the question "re-raised, or
            // resumed?" would have one answer in both worlds. With the ceiling out
            // of reach on the second feed, the only thing that can still throw is
            // the stream's latched verdict: a decoder that resumed reads the probe
            // as a whole valid message and answers Complete instead.
            var neutral = new HeaderDest(NeutralFieldId, Construct.String, Ceiling.Cap, int.MaxValue);
            SofabException again = Assert.Throws<SofabException>(
                () => istream.Feed(TerminalProbe(), neutral));
            Assert.Equal(thrown!.Error, again.Error);
            Assert.Equal(0, neutral.BoundValues);
        }

        // Nothing was bound in any case of this block: the rejections answer at the
        // word, and the controls end before a payload byte arrives.
        Assert.Equal(0, dest.BoundValues);
    }

    /// <summary>
    /// The standing proof that the verdicts above come from the ceiling the case
    /// configured, and not from something incidental about a header with no payload
    /// behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rejection is replayed with BOTH KINDS OF CEILING LIFTED -- no schema
    /// bound, and a receiver cap nothing this block declares can reach -- and must
    /// fall back to INCOMPLETE, the ordinary answer for a message that ends inside a
    /// field. A port whose rejections survived the lift would be rejecting short
    /// reads for an unrelated reason and passing the block by accident.
    /// </para>
    /// <para>
    /// The one admissible exception is a FORMAT ceiling (<c>ARRAY_MAX</c> /
    /// <c>FIXLEN_MAX</c>, both INT32_MAX), which is the format's bound rather than a
    /// receiver cap and cannot be lifted by any configuration: reaching one stays
    /// InvalidMessage. No case in the block reaches it today -- the amplification
    /// case declares 1 GiB, comfortably inside it -- so the branch keeps this test
    /// honest rather than brittle if upstream adds one.
    /// </para>
    /// </remarks>
    [Fact]
    public void WithTheCeilingsLiftedEveryRejectionFallsBackToIncomplete()
    {
        const long Lifted = int.MaxValue; // the format ceiling; nothing above it is configurable
        int rejections = 0, fellBack = 0, heldByTheFormat = 0;

        foreach (Case c in Cases)
        {
            if (c.Outcome == "incomplete" || !Runs(c)) { continue; }
            rejections++;

            (Construct construct, _, _) = ReadShape(c);
            var dest = new HeaderDest(c.FieldId, construct, c.Kind, Lifted);
            (DecodeStatus status, SofabException? thrown) = Feed(new IStream(), c, dest);

            if (thrown == null && status == DecodeStatus.Incomplete)
            {
                fellBack++;
            }
            else if (thrown?.Error == SofabError.InvalidMessage && c.Declared > Lifted)
            {
                heldByTheFormat++;
            }
            else
            {
                Assert.Fail($"{c.Name}: with the ceilings lifted the outcome is {status} "
                    + $"({thrown?.Error.ToString() ?? "no exception"}), want Incomplete -- the rejection "
                    + "does not come from the ceiling the case configured");
            }
        }

        Assert.True(rejections > 0, "no rejection case ran; the negative control proves nothing");
        _out.WriteLine($"[header-limits] negative control (both ceiling kinds lifted): {rejections} "
            + $"rejections, {fellBack} fell back to Incomplete, {heldByTheFormat} held by the format ceiling");
    }

    /// <summary>
    /// The counter the "nothing was bound" assertion rests on genuinely moves, so
    /// that assertion is not vacuous: the same destination, at the same ceiling,
    /// binds the value when the payload actually arrives.
    /// </summary>
    [Fact]
    public void TheDestinationBindsWhenThePayloadArrives()
    {
        var dest = new HeaderDest(0, Construct.String, Ceiling.Cap, 16);
        byte[] message = Encode(os => os.WriteString(0, "12345678"));

        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(message, dest));
        Assert.Equal(1, dest.BoundValues);
    }

    /// <summary>
    /// The block's gating is the opposite of a vector's, and the tagging has to
    /// match the ceiling the case configures: a case that configures a §6.2.1
    /// receiver cap needs the profile capability, and one bounded by the schema
    /// needs no cap and must not demand it -- or a profile that refuses
    /// schema-unbounded fields at generate time would skip the very pair that keeps
    /// the two categories apart.
    /// </summary>
    [Fact]
    public void EveryCaseIsTaggedForTheCeilingItConfigures()
    {
        foreach (Case c in Cases)
        {
            if (c.Kind == Ceiling.Cap)
            {
                Assert.Contains("receiver_caps", c.Requires);
            }
            else
            {
                Assert.DoesNotContain("receiver_caps", c.Requires);
            }
        }
    }

    /// <summary>
    /// An inventory guard, in the shape of the growth block's: floors and structure
    /// rather than equalities, so upstream growing the block does not fail this
    /// port, while a block that SHRANK -- or lost one of the properties it exists
    /// for -- is caught.
    /// </summary>
    [Fact]
    public void TheBlockCarriesEveryCaseKind()
    {
        Assert.True(Cases.Count >= 10, $"header_limits carries {Cases.Count} cases, want at least 10");

        var groups = new HashSet<string>();
        var outcomes = new HashSet<string>();
        var constructs = new HashSet<Construct>();
        // Every rejection must be paired with an in-cap control on THE SAME
        // ceiling, or the block proves nothing.
        var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        // The identical-bytes pair: one header, the two ceilings.
        var byBytes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        int chunked = 0;

        foreach (Case c in Cases)
        {
            (Construct construct, _, _) = ReadShape(c);
            groups.Add(c.Group);
            outcomes.Add(c.Outcome);
            constructs.Add(construct);
            if (c.Chunks.Length > 0) { chunked++; }

            string ceiling = $"{c.CeilingName}={c.Bound}/{construct}";
            if (c.Outcome == "incomplete")
            {
                admitted.Add(ceiling);
            }
            else
            {
                rejected[ceiling] = c.Name;
                Assert.True(c.Terminal,
                    $"{c.Name}: expects {c.Outcome} but is not marked terminal; a ceiling's rejection is terminal (§6.3)");
            }

            if (!byBytes.TryGetValue(c.Serialized, out HashSet<string>? seen))
            {
                seen = new HashSet<string>(StringComparer.Ordinal);
                byBytes[c.Serialized] = seen;
            }
            seen.Add(c.Outcome);
        }

        Assert.Contains("limits/header", groups);
        foreach (string o in new[] { "limit_exceeded", "invalid", "incomplete" })
        {
            Assert.Contains(o, outcomes);
        }
        // A length ceiling and a count ceiling are different code paths, and a port
        // can wire one and miss the other -- §6.2.1 keeps string and blob on
        // separate caps for exactly that reason.
        foreach (Construct k in new[] { Construct.String, Construct.Blob, Construct.Array })
        {
            Assert.Contains(k, constructs);
        }
        Assert.True(chunked > 0,
            "no case splits the header across chunks; the verdict must not depend on the chunking (§7.2 item 4)");

        foreach (KeyValuePair<string, string> r in rejected)
        {
            Assert.True(admitted.Contains(r.Key),
                $"{r.Value} rejects on ceiling {r.Key} with no in-cap control at a length that ceiling "
                + "admits; treat a missing control as a bug in the block");
        }
        Assert.True(
            byBytes.Values.Any(seen => seen.Contains("invalid") && seen.Contains("limit_exceeded")),
            "no two cases carry identical bytes under the two different ceilings; that pair is what "
            + "keeps INVALID and LIMIT_EXCEEDED apart");

        _out.WriteLine($"[header-limits] {Cases.Count} cases: {Cases.Count(Runs)} run, "
            + $"{Cases.Count(c => !Runs(c))} skipped, {chunked} chunked");
    }
}
