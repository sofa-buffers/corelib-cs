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
 * so the numbers are generated code's and Common/HeaderCeiling.cs's HeaderDest
 * stands in for that layer, exactly as GrowthDest does in SequenceGrowthTests. What
 * it does NOT restate is the comparison itself for the two payload kinds: it routes
 * the declared length through PayloadAcc.CheckStringLength / CheckBlobLength, the
 * §6.2.1 comparison the library offers at the length word, from the
 * IVisitor.FixlenBegin hook the decoder raises there. So the string and blob cases
 * exercise this library's own guard and the decoder's header hook for real; the
 * array-count cap and the schema bounds are the destination's, because the corelib
 * has no call for either (see SofabError.LimitExceeded, "each rule is enforced in
 * exactly one of the two places").
 *
 * That destination is SHARED with HeaderLimitsNestedTests, which runs the same
 * headers one and two sequence frames deeper: the two blocks are required to differ
 * in where the field arrives and in nothing else, so they must not have a leaf each.
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
using SofaBuffers.Tests.Common;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class HeaderLimitsTests
{
    private readonly ITestOutputHelper _out;

    public HeaderLimitsTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Whether this port satisfies one <c>requires</c> tag of the block. The
    /// answers themselves live in <see cref="PortCapability"/>, shared with the
    /// nested block, which asks the same questions.
    /// </summary>
    /// <remarks>
    /// IN THIS BLOCK AN UNSATISFIED TAG MEANS SKIP, FOR EVERY TAG -- which is not
    /// how a <em>vector</em> treats one. A vector's unsatisfied wire-construct tag
    /// turns the vector into a negative case, because a build compiled without the
    /// construct rejects any message carrying it. These cases already assert a
    /// rejection WITH A SPECIFIC CATEGORY, so such a build would reject for an
    /// unrelated reason and appear to pass while testing nothing.
    /// <para>
    /// A tag this port does not recognize is treated as unsatisfied here, so a case
    /// resting on something unknown is skipped rather than half-run; every tag the
    /// block uses today is a known one, and the run/skipped line below is what makes
    /// a skip visible.
    /// </para>
    /// </remarks>
    private static bool Satisfies(string tag) => PortCapability.Known(tag) ?? false;

    private static bool Runs(Case c) => c.Requires.TrueForAll(Satisfies);

    // --- the block's shape (test_vectors_README.md) --------------------------

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

            (Ceiling kind, string ceiling, long bound) = HeaderCeiling.ReadCeiling(c, name);
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
    /// length-or-count word they carry -- read by the shared reader, at the top
    /// level (no frames), and cross-checked against what the case states.
    /// </summary>
    private static (Construct Kind, int Id, long Declared) ReadShape(Case c) =>
        HeaderCeiling.ReadShape(c.Name, c.Serialized, Array.Empty<int>(), c.FieldId, c.Declared);

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
