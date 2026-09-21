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
 * destination (a List<T>) and the routing into it are generated code's, while
 * the element-index bound, the gap fill and the growth are sofab's: Seq.PlaceElem
 * for a string element, Seq.ReserveElem for a struct element, Seq.CheckIndex at a
 * string's first piece. GrowthDest below stands in for the generated layer and
 * makes exactly those calls, so the cases exercise the corelib's placement and the
 * decoder's sequence events for real; only the routing is the test's own.
 * SeqPlacementTests pins the same helpers directly, without a decode.
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
    /// The wrapper-array destination a generated message class would be: a
    /// <see cref="List{T}"/> filled through the corelib's own placement calls —
    /// <see cref="Seq.PlaceElem{T}"/> for a string element,
    /// <see cref="Seq.ReserveElem{T}"/> for a struct element — exactly as the
    /// generator emits them.
    /// </summary>
    /// <remarks>
    /// The routing (which depth, which field) is this class's, as it is generated
    /// code's; the index bound, the gap fill and the growth are
    /// <see cref="Seq"/>'s. The field is schema-uncounted, so every call passes
    /// <c>cap = -1</c> and this port's receiver <see cref="Cap"/> as
    /// <c>rcap</c>, and a breach is <see cref="SofabError.LimitExceeded"/>.
    /// </remarks>
    private sealed class GrowthDest : IVisitor
    {
        /// <summary>The schema declares no count for the block's field.</summary>
        private const long NoCount = -1;

        private sealed class Elem
        {
            internal ulong Value;
        }

        private readonly int _fieldId;
        private readonly bool _structElements;
        private readonly List<string> _strings = new();
        private readonly List<Elem> _elems = new();
        private readonly PayloadAcc _acc = new();
        private int _depth;
        private int _element = -1;

        internal int Length => _structElements ? _elems.Count : _strings.Count;

        internal GrowthDest(int fieldId, bool structElements)
        {
            _fieldId = fieldId;
            _structElements = structElements;
        }

        internal string StringAt(int i) => i < _strings.Count ? _strings[i] : string.Empty;

        internal ulong NumberAt(int i) => i < _elems.Count ? _elems[i].Value : 0;

        public void SequenceBegin(int id)
        {
            _depth++;
            // depth 1 is the wrapper itself; depth 2 is a struct element.
            if (_depth == 2 && _structElements)
            {
                Seq.ReserveElem(_elems, id, static () => new Elem(), NoCount, Cap);
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
                _elems[_element].Value = value;
            }
        }

        public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            if (_depth != 1 || _structElements) { return; }
            // The index is bounded at the FIRST piece, before any payload is kept:
            // a rejection must not depend on the payload arriving whole.
            if (offset == 0)
            {
                Seq.CheckIndex(id, NoCount, Cap);
            }
            string? s = _acc.String(total, offset, data, chunkOffset, chunkLength, int.MaxValue);
            if (s is not null)
            {
                Seq.PlaceElem(_strings, id, string.Empty, s, NoCount, Cap);
            }
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
