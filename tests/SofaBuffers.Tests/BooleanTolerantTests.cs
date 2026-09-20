/*
 * SofaBuffers C# - the shared `boolean_tolerant` block (CORELIB_PLAN §4.4).
 *
 * CANONICAL ON ENCODE, TOLERANT ON DECODE. An encoder MUST write `true` as `1`;
 * a decoder MUST read EVERY value other than `0` as `true` -- such a value is not
 * INVALID (§5.2), it is normalized away, and a re-encode emits `1`. A boolean is
 * therefore NOT bound the way an `enum` or a `bitfield` is (MESSAGE_SPEC §1):
 * those carry the width their declaration implies and a value outside it IS
 * INVALID, while a boolean carries no width bound at all.
 *
 * A boolean has no wire type of its own -- it rides the unsigned integer
 * (`0b000`) and, as an array, the unsigned varint array (`0b011`) -- so every
 * byte string here is ordinary, well-formed wire format. What is under test is
 * purely how the BOOLEAN SURFACE reads those bytes and what it writes back.
 *
 * WHY THE POSITIVE VECTORS CANNOT REACH THIS. A vector's bytes are produced by
 * replaying its `fields` ops through a conforming encoder, and a conforming
 * encoder never emits a non-canonical boolean. Bytes carrying 2, 256 or 2^64-1 at
 * a boolean position only ever arrive from SOMEONE ELSE'S encoder, which is why
 * this block is hand-authored and lives beside `vectors` rather than in it.
 *
 * THE THREE DEFECTS IT CATCHES, and the three different assertions that catch
 * them -- this is why the runner never stops at the outcome:
 *
 *   1. REJECTION     -- answering INVALID for 256 because a boolean is treated as
 *                       a one-bit / one-byte bounded type.   -> the outcome check
 *   2. TRUNCATION    -- masking the accumulated varint down to the destination
 *                       width before the zero test: 256 masked to 8 bits is 0, so
 *                       true silently becomes FALSE while the outcome stays
 *                       Complete.                            -> the value check
 *   3. NO NORMALIZING - storing the raw 2: outcome Complete, "is it true?" says
 *                       yes, and only the RE-ENCODE shows it, emitting 2 where
 *                       §4.4 demands 1.                      -> the re-encode check
 *
 * Those are not hypothetical: corelib-c-cpp#172 rejected 256 outright at a scalar
 * boolean, and sofa-buffers/generator#581 decoded a boolean array element of 256
 * to false while answering COMPLETE.
 *
 * WHERE THE BOOLEAN SURFACE LIVES IN THIS PORT, and what these cases therefore
 * prove here. On the encode side the surface is the library's own:
 * OStream.WriteBoolean is the "canonical on encode" half and it is exercised
 * directly. On the decode side sofab has no boolean callback -- a boolean arrives
 * as IVisitor.Unsigned, carrying the FULL 64-bit accumulated value, and the
 * value-to-boolean step belongs to generated code. BooleanDest below stands in
 * for that layer, exactly as GrowthDest does in SequenceGrowthTests and
 * HeaderDest in HeaderLimitsTests. So what the cases pin here is the decoder
 * delivering the whole value undamaged (defect 2 is the decoder's to commit, and
 * the value check catches it wherever it happens) together with the contract the
 * generated layer must meet; they do not pin a decoder-side boolean reader,
 * because this port has none.
 *
 * The destination is a `bool[]` POISONED to 0xAA before the feed and read back
 * through a byte view (§8.2/§8.4 of the block's runner contract): a decode that
 * never writes a slot cannot pass by landing on a zero-initialised buffer, and
 * what is asserted is that the slot ends up holding a representation a `bool` is
 * ALLOWED to hold -- 0 or 1, never 2. A `bool` compared against `true` could not
 * say that.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class BooleanTolerantTests : IClassFixture<BooleanTolerantTests.RunTally>
{
    private readonly RunTally _tally;

    public BooleanTolerantTests(RunTally tally) => _tally = tally;

    // --- capabilities --------------------------------------------------------

    /// <summary>
    /// Whether this build satisfies one <c>requires</c> tag of the block.
    /// </summary>
    /// <remarks>
    /// <see cref="Missing"/> names the tags this build does NOT offer, and it is
    /// EMPTY: a capability this port lacks would be listed there and nowhere else.
    /// <para>
    /// IN THIS BLOCK AN UNSATISFIED TAG MEANS REJECT, NOT SKIP -- the one place
    /// its rule differs from the positive vectors', and the one place a sensible
    /// looking shortcut produces a wrong runner. §4.4 lifts the width bound the
    /// TYPE carries, not the one a particular BUILD has: CORELIB_PLAN §6.2.2
    /// lists "scalar value width 32-bit" as a permitted profile variation, and
    /// §6.2 makes a varint that does not fit the built width INVALID (§5.2.2).
    /// So under a narrowed accumulator a boolean carrying 2^64-1 overflows before
    /// any boolean rule can apply and the message is REJECTED; reading it as true
    /// by truncation is precisely the corruption this block exists to catch, and
    /// skipping the case would assert nothing at all in the build most likely to
    /// have it. The same holds for <c>array</c>: a build compiled without arrays
    /// does not merely fail to store one, it rejects any message carrying one.
    /// </para>
    /// <para>
    /// This is a full-wire-format implementation with no compile-time feature
    /// switches, so its capability set is COMPLETE: every tag is satisfied, all
    /// cases run positively and <see cref="AssertRejected"/> is unreachable. The
    /// gate is written out anyway, so a future feature-reduced profile only has
    /// to name its missing tag in <see cref="Missing"/> rather than rewrite the
    /// runner.
    /// </para>
    /// <para>
    /// An unrecognised tag counts as SATISFIED, deliberately: the block's
    /// reference runner ignores a tag it does not know so the corpus can add one
    /// without turning every older port's copy into a wall of rejections, and all
    /// ports have to agree on that. It is the opposite of the choice
    /// TestVectorsConformanceTests makes for a <em>vector</em>, where an unknown
    /// tag is a load failure -- there the tag decides whether a vector runs at
    /// all, here it decides only which of two assertions a case gets.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> Missing = new();

    private static bool Satisfies(string tag) => !Missing.Contains(tag);

    private static bool Runs(Case c) => c.Requires.TrueForAll(Satisfies);

    // --- the block's shape (test_vectors_README.md) --------------------------

    private sealed record Case(
        string Name,
        string Group,
        string Description,
        List<string> Requires,
        int Id,
        string SerializedHex,
        string Outcome,
        bool[] Values,
        string ReencodedHex);

    private static List<Case> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "test_vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("boolean_tolerant", out JsonElement block))
        {
            // The blocks arrive with the file, which is a verbatim copy of the set
            // corelib-c-cpp owns (§7.1, §8). A copy predating this block would make
            // the loop below iterate zero cases and the suite pass while testing
            // nothing, so the absence is a loud failure instead.
            throw new InvalidOperationException(
                "assets/test_vectors.json carries no boolean_tolerant block: §4.4 has no corpus to run");
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

            JsonElement e = c.GetProperty("expect");
            bool[] values = e.GetProperty("values").EnumerateArray().Select(v => v.GetBoolean()).ToArray();
            if (values.Length == 0)
            {
                throw new InvalidOperationException($"{name}: expect.values is empty");
            }

            cases.Add(new Case(
                name,
                c.GetProperty("group").GetString()!,
                c.TryGetProperty("description", out JsonElement d) ? d.GetString()! : "",
                requires,
                // Read from the case, never assumed: every case carries id 0 today,
                // and a runner that hardcoded it would write the re-encode at the
                // wrong id the moment the corpus adds a case with another.
                c.GetProperty("id").GetInt32(),
                c.GetProperty("serialized_hex").GetString()!,
                e.GetProperty("outcome").GetString()!,
                values,
                e.GetProperty("reencoded_hex").GetString()!));
        }
        if (cases.Count == 0)
        {
            throw new InvalidOperationException("boolean_tolerant block is empty");
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

    // --- the run's size, printed ---------------------------------------------

    /// <summary>
    /// Counts what the run actually executed and prints it once the class's tests
    /// are done, so a CI log states the size of the run and not only its colour.
    /// </summary>
    /// <remarks>
    /// Every way this runner could test less than the file carries -- a stale
    /// copy, cases filtered out by <c>requires</c>, a loop that only ever reaches
    /// the scalar cases -- produces a GREEN suite and is invisible without a
    /// count. The split matters too: a full build must show <c>rejected = 0</c>
    /// and <c>decoded = found</c>, while a feature-reduced profile must show a
    /// non-zero <c>rejected</c>. <c>found</c> is what tells this port that the
    /// corpus has grown past the floor
    /// <see cref="TheBlockCarriesEveryCaseKind"/> asserts.
    /// </remarks>
    public sealed class RunTally : IDisposable
    {
        private int _decoded;
        private int _rejected;
        private int _checks;

        /// <summary>Records a case run on the positive path, with its checks.</summary>
        internal void Decoded(int checks)
        {
            Interlocked.Increment(ref _decoded);
            Interlocked.Add(ref _checks, checks);
        }

        /// <summary>Records a case this build cannot represent and therefore rejects.</summary>
        internal void Rejected(int checks)
        {
            Interlocked.Increment(ref _rejected);
            Interlocked.Add(ref _checks, checks);
        }

        public void Dispose()
        {
            int found = Cases.Count;
            int decoded = Volatile.Read(ref _decoded);
            int rejected = Volatile.Read(ref _rejected);
            Console.WriteLine(
                $"[boolean_tolerant] {found} cases found, {decoded} decoded, "
                + $"{rejected} rejected by `requires`; {Volatile.Read(ref _checks)} checks executed");
        }
    }

    // --- the destination, standing in for the generated layer ----------------

    /// <summary>
    /// The boolean destination generated code would be: it turns each delivered
    /// value into a boolean by testing the FULL value against zero (§4.4), and
    /// keeps the storage byte-inspectable so the test can assert the
    /// representation rather than a truthiness.
    /// </summary>
    /// <remarks>
    /// Nothing is asserted from inside the callbacks. A push decoder calls them
    /// from within <c>Feed</c>, where a throwing assertion would leave the stream
    /// in a state that can mask the very failure it reports; what the callbacks
    /// observed is recorded here and graded after <c>Feed</c> returns.
    /// </remarks>
    private sealed class BooleanDest : IVisitor
    {
        private readonly int _fieldId;
        private readonly bool[] _slots;

        /// <summary>The element count the wire declared, or -1 when no array header arrived.</summary>
        internal int DeclaredCount { get; private set; } = -1;

        /// <summary>The array element category the decoder announced.</summary>
        internal ArrayKind Kind { get; private set; }

        /// <summary>How many values were delivered at the case's field id.</summary>
        internal int Delivered { get; private set; }

        /// <summary>Deliveries past the last destination slot -- never silently dropped.</summary>
        internal int Overrun { get; private set; }

        /// <summary>
        /// Deliveries at the case's id through a callback that is not
        /// <see cref="Unsigned"/>. A boolean rides the unsigned wire type, so any
        /// other route is a mis-dispatch rather than a value to grade.
        /// </summary>
        internal int Misrouted { get; private set; }

        internal BooleanDest(int fieldId, int slots)
        {
            _fieldId = fieldId;
            _slots = new bool[slots];
            // POISON (§8.4). 0xAA is neither of the two representations a bool may
            // hold, so a slot the decode never writes is visible as itself instead
            // of passing as the `false` a zero-initialised buffer would hand out.
            // `new Span<bool>(...)` spelled out: under C# 14's first-class span
            // conversions a bare array argument binds to the ReadOnlySpan overload,
            // which has no Fill -- and the net10.0 leg is where that shows up.
            MemoryMarshal.AsBytes(new Span<bool>(_slots)).Fill(0xAA);
        }

        public void ArrayBegin(int id, ArrayKind kind, int count)
        {
            if (id != _fieldId) { return; }
            DeclaredCount = count;
            Kind = kind;
        }

        public void Unsigned(int id, ulong value)
        {
            if (id != _fieldId) { return; }
            if (Delivered >= _slots.Length)
            {
                Overrun++;
                return;
            }
            // THE boolean read: every value other than 0 is true (§4.4), tested
            // against the whole 64-bit value the decoder accumulated. Masking it
            // into the destination width first is defect 2 -- 256 would become
            // false -- so the comparison is deliberately made here and nowhere
            // narrower.
            _slots[Delivered++] = value != 0;
        }

        public void Signed(int id, long value)
        {
            if (id == _fieldId) { Misrouted++; }
        }

        public void Fp32(int id, float value)
        {
            if (id == _fieldId) { Misrouted++; }
        }

        public void Fp64(int id, double value)
        {
            if (id == _fieldId) { Misrouted++; }
        }

        public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            if (id == _fieldId) { Misrouted++; }
        }

        public void Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            if (id == _fieldId) { Misrouted++; }
        }

        /// <summary>The decoded value at <paramref name="i"/>, for the re-encode.</summary>
        internal bool ValueAt(int i) => _slots[i];

        /// <summary>
        /// The destination's storage as bytes: what the slots actually HOLD, which
        /// is the only thing that can tell a normalized 1 from a stored 2 and a
        /// written 0 from a slot nothing touched.
        /// </summary>
        internal byte[] Representation() =>
            MemoryMarshal.AsBytes(new ReadOnlySpan<bool>(_slots)).ToArray();
    }

    // --- feeding and grading a case ------------------------------------------

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    /// <summary>
    /// Decode the case's bytes into a fresh, poisoned destination and grade both
    /// the outcome and the stored representation; then re-encode what the decoder
    /// produced and grade the bytes. Returns the number of checks performed.
    /// </summary>
    /// <param name="c">the case</param>
    /// <param name="byteAtATime">
    /// feed one byte per <c>Feed</c> call instead of the whole message, which is
    /// what makes the ten-byte varints of the 2^64-1 cases span feed boundaries.
    /// </param>
    private static int AssertDecodesAndReencodes(Case c, bool byteAtATime)
    {
        // Read rather than assumed: every case in the block is `complete` today,
        // and a case that were not must fail loudly here instead of being run
        // under the wrong expectation.
        Assert.True(c.Outcome == "complete",
            $"{c.Name}: unexpected expect.outcome `{c.Outcome}` -- this runner grades `complete` only");

        byte[] message = Convert.FromHexString(c.SerializedHex);
        var dest = new BooleanDest(c.Id, c.Values.Length);
        // A fresh decoder per case: a terminal verdict or leftover parser state
        // from another case must not reach this one.
        var istream = new IStream();

        DecodeStatus status;
        if (byteAtATime)
        {
            status = DecodeStatus.Complete;
            for (int i = 0; i < message.Length; i++)
            {
                status = istream.Feed(new[] { message[i] }, dest);
                Assert.True(status == DecodeStatus.Complete || status == DecodeStatus.Incomplete,
                    $"{c.Name}: byte {i} left the decode at {status}");
            }
        }
        else
        {
            status = istream.Feed(message, dest);
        }

        // 1. THE OUTCOME. A tolerated value is not a rejected one: 256 at a boolean
        //    position is true, not INVALID (§4.4). An INVALID would have thrown,
        //    which fails the test on the spot; Incomplete is graded here.
        Assert.True(status == DecodeStatus.Complete,
            $"{c.Name} ({c.Description}): decode ended at {status}, want Complete");

        Assert.True(dest.Misrouted == 0,
            $"{c.Name}: {dest.Misrouted} value(s) at id {c.Id} arrived through a non-unsigned callback");
        Assert.True(dest.Overrun == 0,
            $"{c.Name}: {dest.Overrun} value(s) delivered past the {c.Values.Length} the case declares");
        Assert.True(dest.Delivered == c.Values.Length,
            $"{c.Name}: {dest.Delivered} value(s) delivered, want {c.Values.Length}");

        if (c.Values.Length > 1)
        {
            // The wire count, cross-checked where the decode surface exposes it: a
            // decoder delivering fewer elements than the header declares is caught
            // here even before the values are compared.
            Assert.True(dest.DeclaredCount == c.Values.Length,
                $"{c.Name}: array header declares {dest.DeclaredCount} elements, want {c.Values.Length}");
            Assert.Equal(ArrayKind.Unsigned, dest.Kind);
        }
        else
        {
            Assert.True(dest.DeclaredCount == -1,
                $"{c.Name}: a scalar boolean was announced as an array of {dest.DeclaredCount}");
        }

        // 2. THE VALUE, as the destination HOLDS it. This is the truncation check:
        //    256 masked into an eight-bit destination is 0, which is a wrong value
        //    behind a perfectly correct outcome -- and the poison makes a slot the
        //    decode never wrote fail here rather than read as `false`.
        byte[] stored = dest.Representation();
        for (int i = 0; i < c.Values.Length; i++)
        {
            byte want = c.Values[i] ? (byte)1 : (byte)0;
            Assert.True(stored[i] == want,
                $"{c.Name} ({c.Description}): element {i} holds 0x{stored[i]:x2}, want 0x{want:x2} "
                + (stored[i] == 0xAA
                    ? "-- the slot still carries the poison, so the decode never wrote it"
                    : "-- a non-zero value must read as true, undamaged by the destination width"));
        }

        // 3. THE RE-ENCODE, of what the DECODER produced -- never of expect.values,
        //    which would make this step assert the JSON against itself. This is
        //    where "canonical on encode" becomes observable: a stored 2 is legal
        //    under every truthiness test and only shows up as a 2 on the wire.
        byte[] produced = Encode(os =>
        {
            if (c.Values.Length == 1)
            {
                os.WriteBoolean(c.Id, dest.ValueAt(0));
            }
            else
            {
                // sofab has no boolean-array writer, so the normalized values go
                // out through the unsigned-array writer at the narrowest element
                // width -- the width never reaches the wire (§4.7), and taking the
                // destination's own bytes means an unnormalized slot would be
                // emitted as it stands rather than laundered into a 1 on the way.
                os.WriteArrayUnsigned(c.Id, stored);
            }
        });

        Assert.True(produced.Length == Convert.FromHexString(c.ReencodedHex).Length,
            $"{c.Name}: re-encoded {produced.Length} bytes, want {Convert.FromHexString(c.ReencodedHex).Length}");
        Assert.True(Hex(produced) == c.ReencodedHex,
            $"{c.Name} ({c.Description}): re-encoded {Hex(produced)}, want {c.ReencodedHex} "
            + "-- §4.4 requires a re-encode to emit 1 for every value that read as true");

        return 2;
    }

    /// <summary>
    /// The path a build takes for a case whose <c>requires</c> it does not
    /// satisfy: the message must be REJECTED, and terminally. Returns the number
    /// of checks performed.
    /// </summary>
    /// <remarks>
    /// Unreachable in this port, whose capability set is complete -- kept, and
    /// kept correct, because it is the assertion the rule of §7 is actually about,
    /// and a profile that narrowed the value width would need exactly this and
    /// nothing else. The verdict is INVALID (a width overflow, §5.2.2), never the
    /// LimitExceeded tier, which belongs to §6.2.1 policy caps.
    /// </remarks>
    private static int AssertRejected(Case c)
    {
        byte[] message = Convert.FromHexString(c.SerializedHex);
        // A destination that binds nothing: what is graded is the verdict, not
        // which fields arrived ahead of the offending one.
        var dest = new BooleanDest(NeutralFieldId, 1);
        var istream = new IStream();

        var thrown = Assert.Throws<SofabException>(() => istream.Feed(message, dest));
        Assert.Equal(SofabError.InvalidMessage, thrown.Error);

        // Terminal: a verdict a later feed lifts is a defect in its own right.
        var again = Assert.Throws<SofabException>(() => istream.Feed(Bytes(0x00), dest));
        Assert.Equal(SofabError.InvalidMessage, again.Error);

        return 1;
    }

    /// <summary>A field id no case in the block uses, so the destination binds nothing.</summary>
    private const int NeutralFieldId = 99;

    // --- the cases -----------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ToleratedValueDecodesAndReencodesCanonically(string name)
    {
        Case c = ByName(name);
        if (Runs(c))
        {
            _tally.Decoded(AssertDecodesAndReencodes(c, byteAtATime: false));
        }
        else
        {
            _tally.Rejected(AssertRejected(c));
        }
    }

    /// <summary>
    /// The same cases fed one byte at a time. The verdict and the values are
    /// properties of the bytes, not of how they were chunked (CORELIB_PLAN §7.2
    /// item 4) -- and the 2^64-1 cases carry a ten-byte varint, so this is where a
    /// value accumulator that does not survive a feed boundary shows up.
    /// </summary>
    [Theory]
    [MemberData(nameof(CaseNames))]
    public void ToleratedValueDecodesTheSameOneByteAtATime(string name)
    {
        Case c = ByName(name);
        if (!Runs(c))
        {
            // A gated case is graded by the whole-feed theory above, which asserts
            // the rejection and counts it; there is no second, chunked grading of
            // it to do here.
            return;
        }
        AssertDecodesAndReencodes(c, byteAtATime: true);
    }

    /// <summary>
    /// An inventory guard: floors rather than equalities, so upstream growing the
    /// block does not fail this port, while a block that SHRANK -- or a case kind
    /// that vanished -- is caught.
    /// </summary>
    [Fact]
    public void TheBlockCarriesEveryCaseKind()
    {
        Assert.True(Cases.Count >= 8, $"boolean_tolerant carries {Cases.Count} cases, want at least 8");
        Assert.All(Cases, c => Assert.Equal("boolean/tolerant", c.Group));
        Assert.All(Cases, c => Assert.Equal("complete", c.Outcome));

        // The untagged floor: the cases every build runs positively, including the
        // most reduced one. 0, 1, 2, 255 and 256 -- 255 and 256 as a pair, because
        // that pair is what separates "reads wide varints" from "truncates to the
        // destination width".
        Assert.True(Cases.Count(c => c.Requires.Count == 0) >= 5,
            "fewer than 5 untagged cases: the block's unconditional floor has shrunk");

        // Both levels of the rule. A scalar boolean and an array element reach the
        // destination through different surfaces, and a port can get one right and
        // the other wrong -- a runner that quietly ran only the scalars would lose
        // the whole element-level half.
        Assert.True(Cases.Any(c => c.Values.Length == 1), "no scalar case");
        Assert.True(Cases.Any(c => c.Values.Length > 1), "no array case");

        // The width-bound cases, whose tags are the ones §7 turns into rejections
        // in a narrowed build.
        Assert.True(Cases.Any(c => c.Requires.Contains("int64")), "no int64-tagged case");
        Assert.True(Cases.Any(c => c.Requires.Contains("array")), "no array-tagged case");

        // And the normalization itself: at least one case whose re-encode is NOT
        // its input. Without one the block would be a round-trip test, and a
        // decoder that stored the raw 2 would pass it.
        Assert.True(Cases.Any(c => c.ReencodedHex != c.SerializedHex),
            "every case re-encodes to its own input: the normalization half is gone");

        // Both boolean values are under test: a destination stuck at `true` would
        // otherwise pass every case.
        Assert.True(Cases.Any(c => c.Values.Contains(false)), "no case expects false");
        Assert.True(Cases.Any(c => c.Values.Contains(true)), "no case expects true");
    }
}
