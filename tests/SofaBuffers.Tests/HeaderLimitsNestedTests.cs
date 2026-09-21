/*
 * SofaBuffers C# - the shared `header_limits_nested` block (CORELIB_PLAN §6.2.1,
 * §6.3; MESSAGE_SPEC §7.1, §5.2).
 *
 * The same truncated over-ceiling header the flat `header_limits` block feeds, ONE
 * OR TWO SEQUENCE FRAMES DEEPER -- and that is the only difference:
 *
 *   3e 02 a2 06   then EOF
 *   ^^ id 7, wire type 6: a sequence OPENS, and is never closed
 *      ^^ id 0, wire type 2 (fixlen)
 *         ^^^^^ the length word (100 << 3) | 2 -- a 100-byte STRING is declared
 *                 ... and the message ends, with the frame still open.
 *
 * WHY DEPTH IS ITS OWN AXIS. Every case of the flat block puts its field at
 * field_id 0 in the top-level scope, so a port can bind its ceiling to the
 * top-level receiver, pass all ten, and cap NOTHING inside a sequence -- where the
 * payload of a real message lives. The cases here are that axis and only that axis:
 * same key set, same outcome vocabulary, same terminality rule, same pairing of
 * every rejection with an in-cap control.
 *
 * AND WHY THE NEGATIVE CONTROL IS LOAD-BEARING HERE, not a formality. These bytes
 * end with a frame still open, so the decoder has a SECOND, fully independent
 * reason to answer INCOMPLETE. A port that rejects for some unrelated reason -- a
 * depth guard, a frame-count guard, a "sequence left open at end of input"
 * rule -- answers limit_exceeded or invalid and passes the forward pass while never
 * having consulted the ceiling under test. Only replaying every rejection WITH THE
 * CEILING LIFTED tells the two apart: if the answer does not change, the ceiling
 * was never what spoke. WithTheCeilingLiftedEveryNestedRejectionChangesItsAnswer is
 * that pass, and it asserts how many cases it examined so it cannot degenerate into
 * a loop that checks nothing.
 *
 * NOTHING TO RESTORE AFTERWARDS. A cap in this port is an argument to the read,
 * never a held or defaulted number (§6.2.1), and a schema bound is the
 * destination's own; both are built per case and die with it, so the block leaks no
 * configuration between its cases and leaves none behind for the next block.
 *
 * THE LEAF IS THE FLAT BLOCK'S LEAF. Common/HeaderCeiling.cs's HeaderDest is the
 * destination both runners judge with; NestedDest below adds the frame chain and
 * nothing else -- it forwards an event to that leaf only when the decoder has taken
 * it to exactly the frames the case names. A nested block with a leaf of its own
 * could pass by a different mechanism than the one under test, which is what this
 * block exists to rule out.
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

public class HeaderLimitsNestedTests
{
    private readonly ITestOutputHelper _out;

    public HeaderLimitsNestedTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Whether this port satisfies one <c>requires</c> tag of the block. The
    /// answers are <see cref="PortCapability"/>'s, shared with the flat block.
    /// </summary>
    /// <remarks>
    /// AN UNSATISFIED TAG IS A SKIP, FOR EVERY TAG: these cases assert a rejection
    /// with a specific CATEGORY, so a build that cannot represent the construct
    /// would reject for an unrelated reason and appear to pass while testing
    /// nothing. An UNKNOWN tag, on the other hand, is satisfied -- a newer vector
    /// file has to stay runnable by an older runner. What stops that leniency from
    /// silently disabling the block is
    /// <see cref="TheBlockReportsWhatItRanAndWhatItGated"/>, which names every gated
    /// case and asserts run + gated is the whole block.
    /// </remarks>
    private static bool Satisfies(string tag) => PortCapability.Known(tag) ?? true;

    private static bool Runs(Case c) => c.Requires.TrueForAll(Satisfies);

    private static string GatingTag(Case c) => c.Requires.Find(t => !Satisfies(t)) ?? "-";

    // --- the block's shape (test_vectors_README.md) --------------------------

    private sealed record Case(
        string Name,
        string Group,
        string Description,
        List<string> Requires,
        int[] Frames,
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
        if (!doc.RootElement.TryGetProperty("header_limits_nested", out JsonElement block))
        {
            throw new InvalidOperationException(
                "assets/test_vectors.json carries no header_limits_nested block: the depth axis of "
                + "§6.2.1/§6.3 has no corpus to run");
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

            // `frames` is the key this block is FOR: the chain of sequence ids the
            // target field is nested in, outermost first. An empty one would be the
            // flat block, so it is a corrupt case rather than a degenerate run.
            int[] frames = c.GetProperty("frames").EnumerateArray().Select(f => f.GetInt32()).ToArray();
            if (frames.Length == 0)
            {
                throw new InvalidOperationException($"{name}: `frames` is empty; the block is the depth axis");
            }

            string[] chunks = c.TryGetProperty("chunks", out JsonElement ch)
                ? ch.EnumerateArray().Select(x => x.GetString()!).ToArray()
                : Array.Empty<string>();

            (Ceiling kind, string ceiling, long bound) = HeaderCeiling.ReadCeiling(c, name);
            JsonElement e = c.GetProperty("expect");

            cases.Add(new Case(
                name,
                c.GetProperty("group").GetString()!,
                c.GetProperty("description").GetString()!,
                requires,
                frames,
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
            throw new InvalidOperationException("header_limits_nested block is empty");
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

    /// <summary>
    /// The construct the case's bytes declare, read by the shared reader AFTER its
    /// frame chain -- which is cross-checked against <c>frames</c> on the way
    /// through, so a case whose bytes and whose `frames` disagree is a corrupt case
    /// rather than a silent pass at the wrong depth.
    /// </summary>
    private static Construct ShapeOf(Case c) =>
        HeaderCeiling.ReadShape(c.Name, c.Serialized, c.Frames, c.FieldId, c.Declared).Kind;

    // --- the frame chain the case names --------------------------------------

    /// <summary>
    /// The receiver the case describes: the sequence chain of <c>frames</c>, with
    /// the flat block's <see cref="HeaderDest"/> at the innermost depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a generated message class reaches a nested field in this port --
    /// the visitor is flat and the scope is carried by
    /// <see cref="IVisitor.SequenceBegin"/> / <see cref="IVisitor.SequenceEnd"/>, so
    /// generated code keeps the path and matches it (the same shape as
    /// <c>GeneratedFp32Slot</c> in FixlenArrayHeaderOrderTests). Everything this
    /// class adds is that gate: the ceiling itself is bound at the INNERMOST depth
    /// and nowhere else, so a header that arrives at the top level, or one frame
    /// short, or under different ids, reaches no ceiling at all.
    /// </para>
    /// <para>
    /// The leaf is not re-implemented here, and that is deliberate: a nested runner
    /// with its own size comparison would be testing its own arithmetic rather than
    /// the library's enforcement point.
    /// </para>
    /// </remarks>
    private sealed class NestedDest : IVisitor
    {
        private readonly int[] _frames;
        private readonly HeaderDest _leaf;
        private readonly List<int> _path = new();

        internal NestedDest(int[] frames, int fieldId, Construct construct, Ceiling kind, long bound)
        {
            _frames = frames;
            _leaf = new HeaderDest(fieldId, construct, kind, bound);
        }

        /// <summary>How many values the leaf bound; see <see cref="HeaderDest.BoundValues"/>.</summary>
        internal int BoundValues => _leaf.BoundValues;

        /// <summary>
        /// How many events actually reached the leaf. A case whose header never got
        /// there was answered by something other than the ceiling, however right the
        /// outcome looked.
        /// </summary>
        internal int LeafEvents { get; private set; }

        /// <summary>The deepest frame nesting the decoder announced.</summary>
        internal int DeepestDepth { get; private set; }

        /// <summary>Whether the decoder has taken us to exactly the frames the case names.</summary>
        private bool AtLeaf
        {
            get
            {
                if (_path.Count != _frames.Length) { return false; }
                for (int i = 0; i < _frames.Length; i++)
                {
                    if (_path[i] != _frames[i]) { return false; }
                }
                return true;
            }
        }

        public void SequenceBegin(int id)
        {
            _path.Add(id);
            if (_path.Count > DeepestDepth) { DeepestDepth = _path.Count; }
        }

        public void SequenceEnd()
        {
            if (_path.Count > 0) { _path.RemoveAt(_path.Count - 1); }
        }

        public void FixlenBegin(int id, FixlenType subtype, int total)
        {
            if (!AtLeaf) { return; }
            LeafEvents++;
            _leaf.FixlenBegin(id, subtype, total);
        }

        public void ArrayBegin(int id, ArrayKind kind, int count)
        {
            if (!AtLeaf) { return; }
            LeafEvents++;
            _leaf.ArrayBegin(id, kind, count);
        }

        public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            if (!AtLeaf) { return; }
            LeafEvents++;
            _leaf.String(id, total, offset, data, chunkOffset, chunkLength);
        }

        public void Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            if (!AtLeaf) { return; }
            LeafEvents++;
            _leaf.Blob(id, total, offset, data, chunkOffset, chunkLength);
        }

        public void Unsigned(int id, ulong value)
        {
            if (!AtLeaf) { return; }
            LeafEvents++;
            _leaf.Unsigned(id, value);
        }

        public void Signed(int id, long value)
        {
            if (!AtLeaf) { return; }
            LeafEvents++;
            _leaf.Signed(id, value);
        }
    }

    private static NestedDest DestFor(Case c, long bound) =>
        new(c.Frames, c.FieldId, ShapeOf(c), c.Kind, bound);

    // --- feeding the case ----------------------------------------------------

    /// <summary>
    /// Feed the case -- as <c>chunks</c> where it splits the bytes, otherwise as one
    /// feed of <c>serialized</c> -- and report the outcome, or the exception that
    /// ended it. Nothing is appended and no frame is closed: the truncation IS the
    /// case.
    /// </summary>
    /// <remarks>
    /// No case in this block splits its bytes today, but the two blocks share a key
    /// set and the flat one has such a case, so <c>chunks</c> is honoured here as
    /// well -- and every feed before the last must answer Incomplete, or the decoder
    /// answered on bytes it had not yet seen.
    /// </remarks>
    private static (DecodeStatus Status, SofabException? Thrown) Feed(
        IStream istream, Case c, IVisitor dest)
    {
        string[] pieces = c.Chunks.Length > 0 ? c.Chunks : new[] { c.Serialized };
        DecodeStatus status = DecodeStatus.Complete;
        for (int i = 0; i < pieces.Length; i++)
        {
            try
            {
                status = istream.Feed(Convert.FromHexString(pieces[i]), dest);
            }
            catch (SofabException e)
            {
                return (DecodeStatus.Invalid, e);
            }
            if (i < pieces.Length - 1)
            {
                Assert.Equal(DecodeStatus.Incomplete, status);
            }
        }
        return (status, null);
    }

    /// <summary>
    /// Eight bytes of would-be payload, fed after a terminal rejection: exactly what
    /// the refused header promised, and what a decoder that had NOT latched would
    /// consume and ask for more of.
    /// </summary>
    /// <remarks>
    /// They are legal content for either construct the block rejects on -- UTF-8
    /// 'A's for the string, eight one-byte varints for the array -- so a decoder
    /// that resumed has nothing to object to and answers Incomplete. Asking the
    /// stream to repeat a stored status instead would let exactly that decoder look
    /// terminal.
    /// </remarks>
    private static byte[] PayloadProbe() => Bytes(0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41);

    /// <summary>
    /// A field id no case in the block uses, so the destination the probe is fed to
    /// reads nothing and can refuse nothing of its own.
    /// </summary>
    private const int NeutralFieldId = 99;

    // --- the cases -----------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void NestedHeaderCaseMatchesExpectation(string name)
    {
        Case c = ByName(name);
        if (!Runs(c))
        {
            // Gated: see Satisfies. TheBlockReportsWhatItRanAndWhatItGated is where
            // a skip becomes visible rather than silent.
            return;
        }

        string what = $"{c.Name} (frames [{string.Join(", ", c.Frames)}]): {c.Description}";
        NestedDest dest = DestFor(c, c.Bound);
        var istream = new IStream();
        (DecodeStatus status, SofabException? thrown) = Feed(istream, c, dest);

        switch (c.Outcome)
        {
            case "incomplete":
                // THE IN-CAP CONTROL, and not filler: the same shape, at the same
                // depth, at a length the ceiling ADMITS. A port that rejects
                // everything nested passes every rejection in this block and is
                // badly broken.
                Assert.True(thrown == null,
                    $"{what}\nthe ceiling admits {c.Bound} and the header declares {c.Declared}, "
                    + $"yet the decode was refused with {thrown?.Error}");
                Assert.Equal(DecodeStatus.Incomplete, status);
                Assert.False(c.Terminal,
                    "an INCOMPLETE case cannot be terminal: it is precisely the state more bytes lift");
                break;

            case "limit_exceeded":
                // A policy rejection, not INVALID: the bytes are well-formed and the
                // same header decodes under a looser cap (§6.2.1, §6.3).
                Assert.True(thrown != null,
                    $"{what}\noutcome {status}, want LimitExceeded at the nested length word: the header "
                    + $"declares {c.Declared} and the cap admits {c.Bound}. Incomplete is the trap -- a "
                    + "frame really is open, so it looks plausible while meaning the ceiling was never "
                    + "reached at this depth (§5.2.3, §6.2.1).");
                Assert.Equal(SofabError.LimitExceeded, thrown!.Error);
                break;

            case "invalid":
                // The schema bound is a statement about validity (MESSAGE_SPEC §7.1),
                // so the word that is a cap breach one case over is malformed here.
                // The two cases carry IDENTICAL BYTES at identical depth and differ
                // only in the ceiling configured; a port that collapses the two
                // categories passes seven of eight and fails exactly here.
                Assert.True(thrown != null,
                    $"{what}\noutcome {status}, want InvalidMessage at the nested length word: the header "
                    + $"declares {c.Declared} and the schema bounds the field at {c.Bound}.");
                Assert.Equal(SofabError.InvalidMessage, thrown!.Error);
                break;

            default:
                throw new InvalidOperationException($"{c.Name}: unknown expected outcome {c.Outcome}");
        }

        // The header reached the innermost frame, and the decoder really did
        // announce the chain: an outcome produced without the leaf ever hearing the
        // header came from somewhere else, whatever it says.
        Assert.True(dest.LeafEvents > 0,
            $"{what}\nno header event arrived at frames [{string.Join(", ", c.Frames)}]; "
            + $"the outcome {c.Outcome} was decided without the ceiling being consulted");
        Assert.Equal(c.Frames.Length, dest.DeepestDepth);

        if (c.Terminal)
        {
            // The rejection is terminal: a further feed RE-RAISES rather than
            // consuming (§6.3, MESSAGE_SPEC §5.2). The probe is the payload the
            // header promised -- a decoder that had merely reported an error and
            // carried on would swallow it and answer Incomplete.
            //
            // It goes to a NEUTRAL destination -- another field id, another depth,
            // a ceiling nothing can reach -- so that the only thing left that can
            // throw is the stream's latched verdict. Fed back to a destination whose
            // ceiling is still armed, a decoder that resumed mid-field would be
            // refused a second time with the SAME error, and "re-raised or resumed?"
            // would have one answer in both worlds.
            var neutral = new NestedDest(
                c.Frames, NeutralFieldId, Construct.String, Ceiling.Cap, int.MaxValue);
            SofabException again = Assert.Throws<SofabException>(
                () => istream.Feed(PayloadProbe(), neutral));
            Assert.Equal(thrown!.Error, again.Error);
            Assert.Equal(0, neutral.BoundValues);
        }

        // Rejected, never clamped (§6.2.1) -- and checked AFTER the terminality feed,
        // so a late materialization is caught too. Every case here ends at or before
        // the size word, and the controls end before a payload byte arrives.
        Assert.Equal(0, dest.BoundValues);
    }

    /// <summary>
    /// THE NEGATIVE CONTROL, and the load-bearing test of this block: every
    /// rejection replayed with its own ceiling LIFTED above what the case declares,
    /// and the answer must CHANGE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it a port that refuses these bytes for an unrelated reason -- an
    /// unclosed frame at end of input, a depth guard, a strict-mode path -- is
    /// indistinguishable from one that consulted the ceiling at the innermost frame.
    /// Both are green on the forward pass. Only lifting the ceiling separates them.
    /// </para>
    /// <para>
    /// The SAME KIND of ceiling is lifted, never the other one: a <c>schema</c> case
    /// gets a lifted schema bound and a <c>limits</c> case a lifted receiver cap,
    /// which falls out of the destination taking the case's own
    /// <see cref="Ceiling"/> and only its value being replaced. Lifting the wrong
    /// one would leave the real ceiling at 16 and fail for the wrong reason -- or,
    /// worse, lifting both would pass the schema case for the cap's reason.
    /// </para>
    /// <para>
    /// What is asserted is that the answer CHANGED, not that it is Incomplete:
    /// the point is that the ceiling caused the rejection, not what the alternative
    /// answer happens to be. The count is asserted too, because a control loop that
    /// quietly examines nothing is green and worthless. The flat block exempts its
    /// 1 GiB amplification case, whose ceiling cannot be lifted past; this block has
    /// no such case, so every rejection it gates in must be covered.
    /// </para>
    /// </remarks>
    [Fact]
    public void WithTheCeilingLiftedEveryNestedRejectionChangesItsAnswer()
    {
        // Far above every `declared` in the block (100), and small enough that
        // lifting it cannot provoke the allocation §6.2.1 exists to prevent.
        const long Lifted = 65536;

        int expected = Cases.Count(c => Runs(c) && c.Outcome != "incomplete");
        int checkedCases = 0;
        var changed = new List<string>();

        foreach (Case c in Cases)
        {
            // Only a rejection can be shown to depend on its ceiling.
            if (c.Outcome == "incomplete" || !Runs(c)) { continue; }
            if (c.Declared > Lifted)
            {
                // No such case today; if upstream adds one, say so rather than
                // counting it as checked.
                _out.WriteLine($"[header-limits-nested] {c.Name} declares {c.Declared}, above the lifted "
                    + "ceiling: not controllable, skipped in this pass");
                continue;
            }
            checkedCases++;

            NestedDest dest = DestFor(c, Lifted);
            (DecodeStatus status, SofabException? thrown) = Feed(new IStream(), c, dest);

            SofabError wasRejectedWith =
                c.Outcome == "invalid" ? SofabError.InvalidMessage : SofabError.LimitExceeded;
            string now = thrown == null ? status.ToString() : thrown.Error.ToString();
            Assert.True(thrown?.Error != wasRejectedWith,
                $"{c.Name}: with the {(c.Kind == Ceiling.Schema ? "schema bound" : "receiver cap")} "
                + $"lifted from {c.Bound} to {Lifted} the answer is still {now} -- so the rejection does "
                + "not come from the ceiling this case configures, and the forward pass proves nothing "
                + $"about the ceiling at depth [{string.Join(", ", c.Frames)}]");

            // In practice it becomes Incomplete, because a frame is still open --
            // which is exactly the answer the ceiling was overriding.
            changed.Add($"{c.Name}: {wasRejectedWith} -> {now}");

            // And the lift must not have let anything materialize either.
            Assert.Equal(0, dest.BoundValues);
        }

        Assert.Equal(expected, checkedCases);
        Assert.True(checkedCases > 0, "no rejection case was controlled; the negative control proves nothing");
        _out.WriteLine($"[header-limits-nested] negative control: {checkedCases} rejections replayed with "
            + $"the case's own ceiling lifted to {Lifted}, all changed their answer");
        foreach (string line in changed) { _out.WriteLine("  " + line); }
    }

    /// <summary>
    /// The counter the "nothing was bound" assertion rests on genuinely moves at
    /// depth, so that assertion is not vacuous -- and the frame gate really does
    /// forward to the leaf rather than swallowing everything, which would make every
    /// in-cap control pass for the wrong reason.
    /// </summary>
    [Fact]
    public void TheNestedDestinationBindsWhenThePayloadArrives()
    {
        var dest = new NestedDest(new[] { 7 }, 0, Construct.String, Ceiling.Cap, 16);
        byte[] message = Encode(os =>
        {
            os.WriteSequenceBeginLazy(7);
            os.WriteString(0, "12345678");
            os.WriteSequenceEndKeep();
        });

        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(message, dest));
        Assert.Equal(1, dest.BoundValues);

        // And the gate is a gate: the identical field at the TOP level reaches no
        // ceiling and binds nothing, which is the mistake the whole block is about.
        var atTopLevel = new NestedDest(new[] { 7 }, 0, Construct.String, Ceiling.Cap, 16);
        Assert.Equal(DecodeStatus.Complete,
            new IStream().Feed(Encode(os => os.WriteString(0, "12345678")), atTopLevel));
        Assert.Equal(0, atTopLevel.BoundValues);
        Assert.Equal(0, atTopLevel.LeafEvents);
    }

    /// <summary>
    /// What ran and what was gated, named -- the cheap guard against a runner that
    /// reports green while executing nothing (a mis-spelled capability, a probe that
    /// answers "unsupported" by accident, an unknown tag disabling a case).
    /// </summary>
    [Fact]
    public void TheBlockReportsWhatItRanAndWhatItGated()
    {
        var ran = new List<string>();
        var gated = new List<string>();
        foreach (Case c in Cases)
        {
            if (Runs(c)) { ran.Add(c.Name); }
            else { gated.Add($"{c.Name} (gated on `{GatingTag(c)}`)"); }
        }

        _out.WriteLine($"[header-limits-nested] {Cases.Count} cases: {ran.Count} ran, {gated.Count} gated");
        foreach (string g in gated) { _out.WriteLine("  gated: " + g); }

        Assert.Equal(Cases.Count, ran.Count + gated.Count);

        // A port with receiver caps runs all eight; one without runs the
        // schema-bounded pair, which needs no cap and is tagged accordingly.
        Assert.True(ran.Count > 0, "every case was gated; the block tested nothing");
        Assert.Contains(Cases.Where(c => c.Kind == Ceiling.Schema), c => Runs(c));

        // Depth 2 is its own trap: a chain builder can be off by one, or treat the
        // inner sequence header as the target field, and handle depth 1 perfectly.
        Assert.Contains(Cases.Where(c => c.Frames.Length >= 2), c => Runs(c));
    }

    /// <summary>
    /// The gating matches the ceiling each case configures: a case that configures a
    /// §6.2.1 receiver cap needs the profile capability, and one bounded by the
    /// schema needs no cap and must not demand it -- or a profile that refuses
    /// schema-unbounded fields at generate time would skip the very pair that keeps
    /// the two categories apart. Every case also needs <c>sequence</c>, which is
    /// what this block is.
    /// </summary>
    [Fact]
    public void EveryCaseIsTaggedForTheCeilingItConfiguresAndForItsDepth()
    {
        foreach (Case c in Cases)
        {
            Assert.Contains("sequence", c.Requires);
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
    /// An inventory guard, in the shape of the flat block's: floors and structure
    /// rather than equalities, so upstream growing the block does not fail this
    /// port, while a block that SHRANK -- or lost one of the properties it exists
    /// for -- is caught.
    /// </summary>
    [Fact]
    public void TheBlockCarriesEveryCaseKind()
    {
        Assert.True(Cases.Count >= 8, $"header_limits_nested carries {Cases.Count} cases, want at least 8");

        var groups = new HashSet<string>();
        var outcomes = new HashSet<string>();
        var depths = new HashSet<int>();
        var constructs = new HashSet<Construct>();
        // Every rejection must be paired with an in-cap control on THE SAME ceiling
        // at THE SAME depth, or the block proves nothing.
        var rejected = new Dictionary<string, string>(StringComparer.Ordinal);
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        // The identical-bytes pair: one header at one depth, the two ceilings.
        var byBytes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (Case c in Cases)
        {
            Construct construct = ShapeOf(c);
            groups.Add(c.Group);
            outcomes.Add(c.Outcome);
            depths.Add(c.Frames.Length);
            constructs.Add(construct);

            string ceiling = $"{c.CeilingName}={c.Bound}/{construct}/d{c.Frames.Length}";
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

        Assert.Contains("limits/header-nested", groups);
        foreach (string o in new[] { "limit_exceeded", "invalid", "incomplete" })
        {
            Assert.Contains(o, outcomes);
        }
        // A length ceiling and a count ceiling are different code paths, and a port
        // can carry one into a sequence and leave the other at the top level.
        foreach (Construct k in new[] { Construct.String, Construct.Array })
        {
            Assert.Contains(k, constructs);
        }
        // One level may be special-cased, so the block must go deeper than one.
        Assert.Contains(1, depths);
        Assert.Contains(2, depths);

        foreach (KeyValuePair<string, string> r in rejected)
        {
            Assert.True(admitted.Contains(r.Key),
                $"{r.Value} rejects on ceiling {r.Key} with no in-cap control at a length that ceiling "
                + "admits; treat a missing control as a bug in the block");
        }
        Assert.True(
            byBytes.Values.Any(seen => seen.Contains("invalid") && seen.Contains("limit_exceeded")),
            "no two cases carry identical bytes under the two different ceilings; that pair is what "
            + "keeps INVALID and LIMIT_EXCEEDED apart at depth");
    }

    /// <summary>
    /// The leaf this block judges with is the flat block's leaf, not a copy: the two
    /// blocks differ in where the field arrives and in nothing else, and a nested
    /// runner that re-implemented the size comparison would be testing its own
    /// arithmetic.
    /// </summary>
    [Fact]
    public void TheNestedLeafIsTheFlatBlocksLeaf()
    {
        System.Reflection.FieldInfo[] leaves = typeof(NestedDest)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(HeaderDest))
            .ToArray();
        Assert.Single(leaves);

        // And the flat runner builds the same type, so "shared" is a fact about both
        // files rather than a claim in this one.
        string flat = File.ReadAllText(Path.Combine(SourceDirectory(), "HeaderLimitsTests.cs"));
        Assert.Contains("new HeaderDest(", flat, StringComparison.Ordinal);
    }

    private static string SourceDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path)!;
}
