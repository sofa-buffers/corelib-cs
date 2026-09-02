/*
 * SofaBuffers C# - the shared `sequence_growth` block (CORELIB_PLAN §7.2 item 8).
 *
 * A wrapper (sequence) array carries no element count on the wire: its length is
 * *highest present id + 1* (MESSAGE_SPEC §5.1), so the size is known only once the
 * array ends and the container GROWS as elements arrive. That is the one
 * allocation shape where growth is conformant, and it happens in the static
 * helper / generated layer, never in the codec (§6.6.1).
 *
 * Why these cases cannot be vectors: two ports that grow differently emit
 * IDENTICAL bytes and reach identical outcomes, so no serialized.hex can tell
 * them apart. The block is therefore keyed by a DELIVERY SEQUENCE OF ELEMENT IDS
 * and the port builds the message itself from `deliver`, asserting `expect` --
 * container length and outcome only, no allocator instrumentation, which is what
 * makes the cases portable across the family.
 *
 * WHAT THIS PORT OWNS, AND WHAT IT DOES NOT -- stated plainly, because the split
 * decides what these cases actually prove here. In C# the wrapper-array
 * destination is generated code's: sofab ships the growth POLICY (Seq.EnsureCap,
 * doubling and clamped) and the decode event stream (IStream/IVisitor), while the
 * element-index cap and the placement live in the generated layer. GrowthDest
 * below therefore stands in for that generated layer, exactly as the README's
 * generator example does. What the cases pin is the CONTRACT that layer must
 * meet, and they exercise Seq.EnsureCap and the decoder's sequence events for
 * real; they do not pin a decoder-side collector, because this port has none.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SofaBuffers.Tests;

public class SequenceGrowthTests
{
    /// <summary>
    /// THIS port's <c>max_dyn_array_count</c> for the block's run.
    /// </summary>
    /// <remarks>
    /// The block never names an absolute boundary: a receiver cap is per-target
    /// configuration and §6.2.1 fixes no family-wide number, so every case's
    /// <c>id_from_cap</c> / <c>length_from_cap</c> is an OFFSET onto whatever the
    /// port picks (-1 → cap-1, 0 → cap). The cases assume a cap of at least 4;
    /// 4 is the smallest value that satisfies them.
    /// </remarks>
    private const int Cap = 4;

    /// <summary>
    /// This port's answer to the <c>dynamic_arrays</c> capability the block gates
    /// on: a C# destination array grows as elements arrive, so the block runs.
    /// </summary>
    /// <remarks>
    /// Not a wire capability like the tags on a vector -- it states how a port
    /// ALLOCATES, not what it can parse, which is why it is the one tag a
    /// full-format port still has to honour (test_vectors_README.md, "Gating").
    /// </remarks>
    private static readonly bool GrowsDynamicArrays = true;

    // --- the block's shape (test_vectors_README.md) --------------------------

    private sealed record Element(int Id, string? Str, ulong Num);

    private sealed record Case(
        string Name,
        string Group,
        string ElementType,
        int FieldId,
        List<string> Requires,
        List<Element> Deliver,
        string Outcome,
        int? Length,
        int[] DefaultIds,
        bool Terminal,
        int? MaxLength);

    private static int Resolve(JsonElement owner, string absKey, string capKey, string what)
    {
        bool hasAbs = owner.TryGetProperty(absKey, out JsonElement abs);
        bool hasRel = owner.TryGetProperty(capKey, out JsonElement rel);
        if (hasAbs && hasRel)
        {
            throw new InvalidOperationException($"{what} carries both {absKey} and {capKey}");
        }
        if (hasAbs) { return abs.GetInt32(); }
        if (hasRel) { return Cap + rel.GetInt32(); }
        throw new InvalidOperationException($"{what} carries neither {absKey} nor {capKey}");
    }

    private static List<Case> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "test_vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("sequence_growth", out JsonElement block))
        {
            throw new InvalidOperationException(
                "assets/test_vectors.json carries no sequence_growth block: §7.2 item 8 has no corpus to run");
        }

        var cases = new List<Case>();
        foreach (JsonElement c in block.EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            string elementType = c.GetProperty("element_type").GetString()!;

            var requires = new List<string>();
            if (c.TryGetProperty("requires", out JsonElement req))
            {
                foreach (JsonElement r in req.EnumerateArray()) { requires.Add(r.GetString()!); }
            }

            var deliver = new List<Element>();
            foreach (JsonElement d in c.GetProperty("deliver").EnumerateArray())
            {
                int id = Resolve(d, "id", "id_from_cap", $"{name}: deliver entry");
                JsonElement v = d.GetProperty("value");
                deliver.Add(elementType == "string"
                    ? new Element(id, v.GetString(), 0)
                    : new Element(id, null, v.GetUInt64()));
            }

            JsonElement e = c.GetProperty("expect");
            string outcome = e.GetProperty("outcome").GetString()!;
            int? length = outcome == "complete"
                ? Resolve(e, "length", "length_from_cap", $"{name}: expect")
                : null;

            var defaults = new List<int>();
            if (e.TryGetProperty("default_ids", out JsonElement dids))
            {
                foreach (JsonElement d in dids.EnumerateArray()) { defaults.Add(d.GetInt32()); }
            }

            int? maxLength = e.TryGetProperty("max_length", out JsonElement ml) ? ml.GetInt32() : null;
            bool terminal = e.TryGetProperty("terminal", out JsonElement tm) && tm.GetBoolean();

            cases.Add(new Case(name, c.GetProperty("group").GetString()!, elementType,
                c.GetProperty("field_id").GetInt32(), requires, deliver,
                outcome, length, defaults.ToArray(), terminal, maxLength));
        }
        if (cases.Count == 0)
        {
            throw new InvalidOperationException("sequence_growth block is empty");
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

    // --- building the message the delivery sequence describes ----------------

    private static byte[] Build(Case c)
    {
        var buf = new byte[4096];
        var os = new OStream(buf);

        // The frame is KEPT even when empty: element presence is what carries the
        // array's length, so an empty wrapper is framed rather than omitted (§5.1).
        os.WriteSequenceBeginLazy(c.FieldId);
        foreach (Element d in c.Deliver)
        {
            if (c.ElementType == "string")
            {
                os.WriteString(d.Id, d.Str!);
            }
            else
            {
                // A struct element is a framed sub-sequence carrying one unsigned
                // field at id 0 -- it reaches a destination through the sequence
                // path rather than the leaf path.
                os.WriteSequenceBeginLazy(d.Id);
                os.WriteUnsigned(0, d.Num);
                os.WriteSequenceEndKeep();
            }
        }
        os.WriteSequenceEndKeep();

        var produced = new byte[os.BytesUsed];
        Array.Copy(buf, 0, produced, 0, produced.Length);
        return produced;
    }

    // --- the destination, standing in for the generated layer ----------------

    /// <summary>
    /// The wrapper-array destination a generated message class would be: it
    /// bounds the element INDEX, then grows through <see cref="Seq.EnsureCap"/>
    /// and places at the id.
    /// </summary>
    /// <remarks>
    /// The order of those two steps is the whole point of the growth/reject case:
    /// §6.2.1 bounds the index "before the container it indexes into is extended",
    /// so a rejected id must leave no partial extension behind. The logical length
    /// is tracked separately from the buffer's capacity, because EnsureCap doubles
    /// -- generated code trims to the length when the array ends, which is what
    /// makes the decoded length exactly highest present id + 1.
    /// </remarks>
    private sealed class GrowthDest : IVisitor
    {
        private readonly int _fieldId;
        private readonly bool _structElements;
        private string[] _strings = Array.Empty<string>();
        private ulong[] _numbers = Array.Empty<ulong>();
        private int _depth;
        private int _element = -1;
        private byte[] _payload = Array.Empty<byte>();

        internal int Length { get; private set; }

        /// <summary>
        /// The destination BUFFER's length, which is not the same as
        /// <see cref="Length"/>: EnsureCap doubles, so the buffer may legitimately
        /// run ahead of the logical length. Asserted separately on a rejection,
        /// because a partial extension is a fact about the buffer -- a logical
        /// length updated only after a successful placement would report the right
        /// number even if the buffer had already grown toward the rejected index.
        /// </summary>
        internal int Capacity => _structElements ? _numbers.Length : _strings.Length;

        internal GrowthDest(int fieldId, bool structElements)
        {
            _fieldId = fieldId;
            _structElements = structElements;
        }

        internal string StringAt(int i) => i < _strings.Length ? _strings[i] : string.Empty;

        internal ulong NumberAt(int i) => i < _numbers.Length ? _numbers[i] : 0;

        /// <summary>
        /// The element-index bound (§6.2.1): a wrapper array has no count header,
        /// so the INDEX is what has to be bounded, and a breach is a policy
        /// rejection -- LimitExceeded, never INVALID, because the bytes are
        /// well-formed and decode under a looser cap (§6.3).
        /// </summary>
        private void Place(int id)
        {
            if (id >= Cap)
            {
                throw new SofabException(SofabError.LimitExceeded,
                    $"element index {id} at or past max_dyn_array_count {Cap}");
            }
            if (_structElements)
            {
                // A numeric element default is the zero Array.Resize already
                // writes, so growth alone initialises the slots.
                _numbers = Seq.EnsureCap(_numbers, id, Cap);
            }
            else
            {
                // MESSAGE_SPEC §5.1: every destination slot is initialised to its
                // ELEMENT DEFAULT before the array is applied. For a string that
                // is "", not null -- Array.Resize writes default(T), so the new
                // slots are filled explicitly. Getting this wrong is invisible
                // until a gap case looks at the slot nothing was written to.
                int grown = _strings.Length;
                _strings = Seq.EnsureCap(_strings, id, Cap);
                for (int i = grown; i < _strings.Length; i++) { _strings[i] = string.Empty; }
            }
            if (id + 1 > Length) { Length = id + 1; }
        }

        public void SequenceBegin(int id)
        {
            _depth++;
            // depth 1 is the wrapper itself; depth 2 is a struct element.
            if (_depth == 2 && _structElements)
            {
                Place(id);
                _element = id;
            }
        }

        public void SequenceEnd()
        {
            if (_depth == 2) { _element = -1; }
            _depth--;
        }

        public void Unsigned(int id, ulong value)
        {
            if (_depth == 2 && _structElements && _element >= 0 && id == 0)
            {
                _numbers[_element] = value;
            }
        }

        public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            if (_depth != 1 || _structElements) { return; }
            // The index is bounded at the FIRST piece, before any payload is kept:
            // a rejection must not depend on the payload arriving whole.
            if (offset == 0)
            {
                Place(id);
                _payload = new byte[total];
            }
            Array.Copy(data, chunkOffset, _payload, offset, chunkLength);
            if (offset + chunkLength == total)
            {
                _strings[id] = Encoding.UTF8.GetString(_payload);
            }
        }

        /// <summary>Trim the capacity down to the logical length, as generated code does when the array ends.</summary>
        internal void Finish()
        {
            if (_structElements) { Array.Resize(ref _numbers, Length); }
            else { Array.Resize(ref _strings, Length); }
        }

        internal int FieldId => _fieldId;
    }

    // --- the cases -----------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void GrowthCaseMatchesExpectation(string name)
    {
        // A statically bounded profile declares dynamic_arrays false and states
        // that in its README instead (§7.2 item 8); this port grows, so it runs.
        if (!GrowsDynamicArrays) { return; }
        Case c = ByName(name);

        byte[] message = Build(c);
        var dest = new GrowthDest(c.FieldId, c.ElementType == "struct");
        var istream = new IStream();

        SofabException? thrown = null;
        try
        {
            istream.Feed(message, dest);
        }
        catch (SofabException e)
        {
            thrown = e;
        }

        if (c.Outcome == "complete")
        {
            Assert.Null(thrown);
            dest.Finish();
            Assert.Equal(c.Length!.Value, dest.Length);

            // A gap below the cap holds the element default, and neither shortens
            // nor shifts the array (§5.1).
            foreach (int id in c.DefaultIds)
            {
                Assert.True(id < dest.Length, $"default id {id} past the container length {dest.Length}");
                if (c.ElementType == "string") { Assert.Equal(string.Empty, dest.StringAt(id)); }
                else { Assert.Equal(0UL, dest.NumberAt(id)); }
            }
        }
        else
        {
            // A policy rejection, not INVALID: the same bytes decode under a
            // looser cap (§6.2.1, §6.3).
            Assert.NotNull(thrown);
            Assert.Equal(SofabError.LimitExceeded, thrown!.Error);

            // The bound is applied BEFORE the container is extended, so the length
            // never passes what legitimately arrived -- and the rejection is
            // terminal, so an element delivered after it does not land either.
            if (c.MaxLength is int max)
            {
                Assert.True(dest.Length <= max,
                    $"container length {dest.Length}, want at most {max} -- extended toward the rejected index");
                // And the buffer behind it: §6.2.1 bounds the index "before the
                // container it indexes into is extended", so a rejected id must
                // leave no allocation behind either.
                Assert.True(dest.Capacity <= max,
                    $"destination buffer grew to {dest.Capacity}, want at most {max} -- "
                    + "the index was bounded after the container was extended");
            }
            if (c.Terminal)
            {
                // Terminal means the stream is closed to further feeds: a caller
                // that caught the first verdict and fed on gets it re-raised.
                // It is deliberately NOT folded into the wire-conformance
                // outcome (§6.3) -- these bytes are well-formed, so the refusal
                // stays LimitExceeded on the error channel and never becomes
                // InvalidMessage, and Feed never returns Complete for it.
                Assert.NotEqual(SofabError.InvalidMessage, thrown!.Error);
                var again = Assert.Throws<SofabException>(() => istream.Feed(message, dest));
                Assert.Equal(SofabError.LimitExceeded, again.Error);
                Assert.NotEqual(SofabError.InvalidMessage, again.Error);
            }
        }
    }

    /// <summary>
    /// The block is the one place a full-format port still honours
    /// <c>requires</c>: the tag says how the port ALLOCATES, not what it can
    /// parse, so a statically bounded build must skip these cases even though it
    /// runs every vector. Pin that every case carries it.
    /// </summary>
    [Fact]
    public void EveryCaseIsGatedOnDynamicArrays()
    {
        foreach (Case c in Cases)
        {
            Assert.Contains("dynamic_arrays", c.Requires);
        }
    }

    /// <summary>
    /// An inventory guard: floors rather than equalities, so upstream growing the
    /// block does not fail this port, while a block that SHRANK -- or a case kind
    /// that vanished -- is caught.
    /// </summary>
    [Fact]
    public void TheBlockCarriesEveryCaseKind()
    {
        Assert.True(Cases.Count >= 8, $"sequence_growth carries {Cases.Count} cases, want at least 8");

        var groups = new HashSet<string>();
        var kinds = new HashSet<string>();
        var outcomes = new HashSet<string>();
        foreach (Case c in Cases)
        {
            groups.Add(c.Group);
            kinds.Add(c.ElementType);
            outcomes.Add(c.Outcome);
        }

        foreach (string g in new[] { "growth/index", "growth/gap", "growth/reject", "growth/length" })
        {
            Assert.Contains(g, groups);
        }
        // Both element kinds are mandatory: a string element reaches the container
        // through the leaf path and a struct element through the sequence path,
        // and a port can get one right and the other wrong.
        foreach (string k in new[] { "string", "struct" }) { Assert.Contains(k, kinds); }
        foreach (string o in new[] { "complete", "limit_exceeded" }) { Assert.Contains(o, outcomes); }
    }
}
