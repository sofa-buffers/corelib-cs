/*
 * SofaBuffers C# - shared conformance suite.
 *
 * Replays the language-agnostic test vectors through the encoder and decoder, as
 * CORELIB_PLAN §7.1 requires of every port. They are read from
 * assets/test_vectors.json, where §8 puts them, and are a verbatim copy of the
 * set corelib-c-cpp generates -- that repo is their source of truth, and the
 * vectors outrank the prose. Each vector is exercised six ways:
 *
 *   1. encode         -- replay fields at the given offset; bytes must equal serialized.hex
 *   2. chunked-encode -- encode through 1/3/7-byte buffers + a flush sink; the
 *                        streamed-out bytes must still equal serialized.hex
 *   3. decode         -- feed serialized.hex; decoded fields must match fields[]
 *   4. decode 1-by-1  -- feed one byte at a time; result must match the whole-feed decode
 *   5. roundtrip      -- encode then decode; recovered fields must match fields[]
 *   6. skip-ids       -- for vectors carrying a per-vector "skip_ids": a receiver that
 *                        ignores those ids (dropping the field whatever its wire type,
 *                        and the whole sub-sequence at any depth when the id names a
 *                        sequence) must still decode the remaining fields and fully
 *                        consume the message -- both whole-feed and one byte at a time.
 *                        The full consumption is asserted, not assumed: the decode
 *                        must end at DecodeStatus.Complete, so a decode that eats a
 *                        byte too few or too many is caught even where the surviving
 *                        fields happen to still line up.
 *
 * What scenario 6 grades here -- and what it does not. sofab.IStream is a push
 * decoder with no decline hook: it parses every field and hands it to the visitor,
 * and "skipping" is the receiver not acting on an id it was given (there is no
 * bind-a-destination step to leave unbound, as in the C API). So scenario 6 grades
 * the *receiver* model -- that ignoring an id, including a whole sub-sequence at any
 * depth, leaves exactly the expected residual fields, in order, with their exact
 * values -- and not a decoder skip path, because this port has none to grade.
 *
 * That is measured, not assumed. Seed the fixlen payload length one byte short in
 * IStream and 36 skip cases fail -- and every one of those vectors also fails
 * scenario 4 on the same mutation, 49 of them in total; seed a one-byte element
 * count and the one skip case that fails (skip_long_int_arrays) fails scenario 3 as
 * well. The skip cases bite, but they detect a subset of what the plain decode
 * scenarios detect on the same 58 vectors, so the 58x2 case count is not additional
 * decoder coverage. The decoder coverage the regenerated file does add -- two-byte
 * lengths and counts, the fp64 element width read from the fixlen_word, a
 * three-byte header varint, zero-length payloads -- arrives through scenarios 1-5,
 * which run all 131 vectors. CORELIB_PLAN §7.2 item 7's "assert correct resync on
 * the following field" is met the same way: the anchor field that follows each
 * skipped id is compared by value in every plain decode of that vector.
 *
 * What scenario 6 does add over scenario 3 is the receiver-side rule itself, and
 * that is cross-checked against the plain decode rather than only against a second
 * copy of the same skip logic -- see AssertSkipMatchesPlainDecode.
 *
 * A vector's optional "requires" array names capability tags it needs (fixlen,
 * array, sequence, fp64, int64). This full-wire-format implementation supports
 * them all and runs every vector; a feature-reduced build would skip the rest.
 *
 * The vector file is a verbatim copy and may carry top-level blocks this suite
 * does not replay (today: "sequence_growth", CORELIB_PLAN §7.2 item 8). The loader
 * ignores what it does not run -- an unknown block is never a load failure -- but
 * it refuses anything it would have to shrink to fit: see the loader guards below.
 *
 * The run prints how many vectors and how many checks it executed (see RunTally).
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace SofaBuffers.Tests;

public class TestVectorsConformanceTests : IClassFixture<TestVectorsConformanceTests.RunTally>
{
    private readonly RunTally _tally;

    public TestVectorsConformanceTests(RunTally tally) => _tally = tally;

    // --- vector model + loader ---------------------------------------------

    private sealed class Vector
    {
        public string Name = "";

        /// <summary>
        /// The vector's <c>group</c> (e.g. <c>skip/matrix</c>), carried so the
        /// loader guard can assert the skip families arrived whole.
        /// </summary>
        public string Group = "";

        public int Offset;
        public JsonElement[] Fields = Array.Empty<JsonElement>();
        public byte[] Expected = Array.Empty<byte>();

        /// <summary>
        /// Field ids a receiver is expected to skip during decoding, or
        /// <c>null</c> when the vector does not drive the skip-ids scenario.
        /// </summary>
        public int[]? SkipIds;

        /// <summary>
        /// Capability tags the vector demands (<c>fixlen</c>, <c>array</c>,
        /// <c>sequence</c>, <c>fp64</c>, <c>int64</c>). A feature-reduced build
        /// would skip vectors needing a disabled capability; this full-wire-format
        /// implementation supports them all, so every vector runs.
        /// </summary>
        public string[] Requires = Array.Empty<string>();
    }

    /// <summary>
    /// Capabilities this implementation provides. SofaBuffers C# supports the
    /// full wire format, so it satisfies every <c>requires</c> tag and runs every
    /// vector (per the test-vectors spec, full implementations ignore
    /// <c>requires</c>). Kept explicit so a future feature-reduced build can gate
    /// vectors honestly.
    /// </summary>
    private static readonly HashSet<string> Supported =
        new() { "fixlen", "array", "sequence", "fp64", "int64" };

    /// <summary>
    /// Every capability tag this loader knows how to grade a vector against.
    /// Identical to <see cref="Supported"/> today because this build provides
    /// them all, but the two say different things: this set is what the loader
    /// <em>understands</em>, <see cref="Supported"/> is what the build
    /// <em>offers</em>. A tag outside this set is a vector file newer than the
    /// harness, and <see cref="Load"/> refuses it rather than treating it as an
    /// unsupported capability and quietly dropping the vector -- the failure mode
    /// CORELIB_PLAN §7.1 exists to prevent, where the suite reports green while
    /// testing less than the vectors describe.
    /// </summary>
    private static readonly HashSet<string> KnownRequires =
        new() { "fixlen", "array", "sequence", "fp64", "int64" };

    private static readonly Dictionary<string, Vector> Vectors = Load();

    /// <summary>
    /// Loads the shared vectors. Every assertion in this file is against the
    /// <c>serialized</c> column — the primitive-layer ground truth: the exact
    /// bytes for the given sequence of field-write ops, which is precisely what
    /// this repo implements.
    /// </summary>
    /// <remarks>
    /// The vectors also carry a <c>serialized_sparse</c> column (the MESSAGE_SPEC
    /// §2 form: every field equal to its declared default omitted, including a
    /// sequence-typed field that turns out all-default). <b>Nothing here reads it,
    /// and nothing here could:</b> producing that form requires knowing each
    /// field's declared default, which lives in a schema — and this corelib has no
    /// message layer, it writes the ops it is handed. <c>serialized_sparse</c> is
    /// exercised by the <i>generator's</i> conformance drivers, which generate
    /// typed message classes from a schema and compare each field against its
    /// default (sofa-buffers/generator,
    /// <c>tests/conformance/csharp/check_vectors.py</c>). Do not add a test for it
    /// here: it could only re-encode defaults hard-coded in the test, asserting
    /// the test's own arithmetic rather than the library's behaviour.
    /// </remarks>
    private static Dictionary<string, Vector> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "test_vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));

        // Nothing here is bounded by a compile-time size: skip_ids lists, field
        // ids, element counts and payload lengths all land in growable .NET
        // collections, so a bigger vector file is loaded whole rather than
        // trimmed to fit. The C harness's fixed MAXSKIP truncated over-long
        // skip_ids lists and kept passing while testing less
        // (corelib-c-cpp#160); the guards below are what stands in for the cap
        // this port does not have -- every one of them throws, so a vector file
        // the harness cannot represent faithfully fails the run loudly instead
        // of shrinking into it. See LoaderTakesTheVectorFileWhole.
        JsonElement vectors = doc.RootElement.GetProperty("vectors");
        int declared = vectors.GetArrayLength();
        var map = new Dictionary<string, Vector>();
        foreach (JsonElement v in vectors.EnumerateArray())
        {
            var vec = new Vector
            {
                Name = v.GetProperty("name").GetString()!,
                Group = v.TryGetProperty("group", out JsonElement grp) ? grp.GetString()! : "",
                Offset = v.GetProperty("offset").GetInt32(),
                // Clone so the elements outlive the JsonDocument.
                Fields = v.GetProperty("fields").EnumerateArray().Select(f => f.Clone()).ToArray(),
                Expected = Convert.FromHexString(v.GetProperty("serialized").GetProperty("hex").GetString()!),
                SkipIds = v.TryGetProperty("skip_ids", out JsonElement skip)
                    ? skip.EnumerateArray().Select(e => e.GetInt32()).ToArray()
                    : null,
                Requires = v.TryGetProperty("requires", out JsonElement req)
                    ? req.EnumerateArray().Select(e => e.GetString()!).ToArray()
                    : Array.Empty<string>(),
            };
            foreach (string tag in vec.Requires)
            {
                if (!KnownRequires.Contains(tag))
                {
                    throw new InvalidOperationException(
                        $"vector '{vec.Name}' requires the unknown capability '{tag}': the vector file is "
                        + "newer than this harness -- teach the harness the tag rather than letting the "
                        + "vector be gated out unrun");
                }
            }

            // Names key the xUnit cases, so a duplicate would overwrite its twin
            // and silently drop one vector from every scenario.
            if (map.ContainsKey(vec.Name))
            {
                throw new InvalidOperationException($"duplicate vector name '{vec.Name}' in test_vectors.json");
            }

            map[vec.Name] = vec;
        }

        if (map.Count != declared)
        {
            throw new InvalidOperationException(
                $"loaded {map.Count} of {declared} vectors from test_vectors.json");
        }

        return map;
    }

    /// <summary>
    /// Counts what the suite actually ran and prints the tally once the class's
    /// last test has finished (xUnit disposes a class fixture after the last
    /// test in the class), so a CI log states the size of the run instead of
    /// only its colour.
    /// </summary>
    /// <remarks>
    /// A <em>check</em> is one scenario replayed against one vector, and the
    /// chunked encoder counts one per output-buffer size -- the same accounting
    /// the C runner in <c>corelib-c-cpp</c> uses, so the two numbers are
    /// comparable: it reported 583 checks over the 81-vector file and 1033 over
    /// this 131-vector one.
    /// </remarks>
    public sealed class RunTally : IDisposable
    {
        private readonly ConcurrentDictionary<string, byte> _ran = new();
        private readonly ConcurrentDictionary<string, byte> _gated = new();
        private int _checks;

        /// <summary>Records <paramref name="checks"/> checks passed for a vector.</summary>
        internal void Check(string vector, int checks = 1)
        {
            _ran[vector] = 0;
            Interlocked.Add(ref _checks, checks);
        }

        /// <summary>Records a vector this build cannot run (see <c>requires</c>).</summary>
        internal void Gated(string vector) => _gated[vector] = 0;

        public void Dispose()
        {
            int skipVectors = Vectors.Values.Count(v => v.SkipIds != null);
            Console.WriteLine(
                $"[test-vectors] {Vectors.Count} vectors loaded, {_ran.Count} run, "
                + $"{_gated.Count} gated out by `requires`; "
                + $"{skipVectors} carry skip_ids (run whole-feed and one byte at a time); "
                + $"{Volatile.Read(ref _checks)} checks executed");
        }
    }

    /// <summary>One xUnit case per vector, keyed by name.</summary>
    public static IEnumerable<object[]> VectorNames => Vectors.Keys.Select(n => new object[] { n });

    /// <summary>One xUnit case per vector that carries a <c>skip_ids</c> array.</summary>
    public static IEnumerable<object[]> SkipVectorNames =>
        Vectors.Values.Where(v => v.SkipIds != null).Select(v => new object[] { v.Name });

    // --- helpers ------------------------------------------------------------

    private static int Id(JsonElement f) => f.GetProperty("id").GetInt32();

    /// <summary>A finite JSON number, or the literals "inf" / "-inf".</summary>
    private static double Fp(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            string s = e.GetString()!;
            return s switch
            {
                "inf" => double.PositiveInfinity,
                "-inf" => double.NegativeInfinity,
                _ => double.Parse(s, CultureInfo.InvariantCulture),
            };
        }
        return e.GetDouble();
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // --- encode replay ------------------------------------------------------

    private static void ReplayEncode(OStream os, JsonElement[] fields)
    {
        foreach (JsonElement f in fields)
        {
            switch (f.GetProperty("op").GetString())
            {
                case "unsigned": os.WriteUnsigned(Id(f), f.GetProperty("value").GetUInt64()); break;
                case "signed": os.WriteSigned(Id(f), f.GetProperty("value").GetInt64()); break;
                case "boolean": os.WriteBoolean(Id(f), f.GetProperty("value").GetBoolean()); break;
                case "fp32": os.WriteFp32(Id(f), (float)Fp(f.GetProperty("value"))); break;
                case "fp64": os.WriteFp64(Id(f), Fp(f.GetProperty("value"))); break;
                case "string": os.WriteString(Id(f), f.GetProperty("value").GetString()!); break;
                case "blob": os.WriteBlob(Id(f), Convert.FromHexString(f.GetProperty("value_hex").GetString()!)); break;
                case "array": ReplayArray(os, f); break;
                case "sequence_begin": os.WriteSequenceBeginLazy(Id(f)); break;
                // The vectors' `serialized` hex is the DENSE image: every frame a
                // vector names is on the wire, empty ones included. Replaying is
                // raw-encoder work with no schema behind it, so the closer that
                // reproduces that image is the frame-keeping one -- WriteSequenceEnd
                // would drop the three empty-sequence vectors to zero bytes
                // (MESSAGE_SPEC §2; the omitted form is `serialized_sparse`).
                case "sequence_end": os.WriteSequenceEndKeep(); break;
                default: throw new InvalidOperationException("unknown op " + f.GetProperty("op").GetString());
            }
        }
    }

    private static void ReplayArray(OStream os, JsonElement f)
    {
        int id = Id(f);
        JsonElement vals = f.GetProperty("values");
        string et = f.GetProperty("element_type").GetString()!;
        switch (et)
        {
            case "u8": os.WriteArrayUnsigned(id, vals.EnumerateArray().Select(x => (byte)x.GetUInt64()).ToArray()); break;
            case "u16": os.WriteArrayUnsigned(id, vals.EnumerateArray().Select(x => (ushort)x.GetUInt64()).ToArray()); break;
            case "u32": os.WriteArrayUnsigned(id, vals.EnumerateArray().Select(x => (uint)x.GetUInt64()).ToArray()); break;
            case "u64": os.WriteArrayUnsigned(id, vals.EnumerateArray().Select(x => x.GetUInt64()).ToArray()); break;
            case "i8": os.WriteArraySigned(id, vals.EnumerateArray().Select(x => (sbyte)x.GetInt64()).ToArray()); break;
            case "i16": os.WriteArraySigned(id, vals.EnumerateArray().Select(x => (short)x.GetInt64()).ToArray()); break;
            case "i32": os.WriteArraySigned(id, vals.EnumerateArray().Select(x => (int)x.GetInt64()).ToArray()); break;
            case "i64": os.WriteArraySigned(id, vals.EnumerateArray().Select(x => x.GetInt64()).ToArray()); break;
            case "fp32": os.WriteArrayFp32(id, vals.EnumerateArray().Select(x => (float)Fp(x)).ToArray()); break;
            case "fp64": os.WriteArrayFp64(id, vals.EnumerateArray().Select(x => Fp(x)).ToArray()); break;
            default: throw new InvalidOperationException("unknown element_type " + et);
        }
    }

    // --- decode: normalized event tokens -----------------------------------

    /// <summary>Records decoded fields as flat tokens, coalescing string/blob chunks.</summary>
    private class TokenVisitor : IVisitor
    {
        public readonly List<string> Tokens = new();
        private readonly MemoryStream _pending = new();
        private string? _kind;
        private int _id;
        private int _total;

        public void Unsigned(int id, ulong v) { if (!Drop(id)) Tokens.Add($"u:{id}={v}"); }
        public void Signed(int id, long v) { if (!Drop(id)) Tokens.Add($"s:{id}={v}"); }
        public void Fp32(int id, float v) { if (!Drop(id)) Tokens.Add($"f32:{id}={BitConverter.SingleToInt32Bits(v)}"); }
        public void Fp64(int id, double v) { if (!Drop(id)) Tokens.Add($"f64:{id}={BitConverter.DoubleToInt64Bits(v)}"); }
        public void String(int id, int total, int offset, byte[] d, int o, int l) { if (!Drop(id)) Chunk("str", id, total, d, o, l); }
        public void Blob(int id, int total, int offset, byte[] d, int o, int l) { if (!Drop(id)) Chunk("blob", id, total, d, o, l); }
        public void ArrayBegin(int id, ArrayKind kind, int count) { if (!Drop(id)) Tokens.Add($"arr:{id}:{kind}:{count}"); }
        public virtual void SequenceBegin(int id) => Tokens.Add($"seq{{:{id}");
        public virtual void SequenceEnd() => Tokens.Add("seq}");

        /// <summary>
        /// Whether the field currently being delivered must be dropped instead of
        /// recorded. Always false for the plain recorder; the sole extension point
        /// <see cref="SkippingTokenVisitor"/> needs beyond the two sequence hooks.
        /// </summary>
        protected virtual bool Drop(int id) => false;

        private void Chunk(string kind, int id, int total, byte[] d, int o, int l)
        {
            if (_kind == null)
            {
                _kind = kind;
                _id = id;
                _total = total;
                _pending.SetLength(0);
            }
            _pending.Write(d, o, l);
            if (_pending.Length >= _total)
            {
                Tokens.Add($"{_kind}:{_id}={Hex(_pending.ToArray())}");
                _kind = null;
            }
        }
    }

    /// <summary>Builds the expected token stream directly from the vector's fields[].</summary>
    private static List<string> ExpectedTokens(JsonElement[] fields)
    {
        var t = new List<string>();
        foreach (JsonElement f in fields)
        {
            int id = f.TryGetProperty("id", out JsonElement idEl) ? idEl.GetInt32() : 0;
            switch (f.GetProperty("op").GetString())
            {
                case "unsigned": t.Add($"u:{id}={f.GetProperty("value").GetUInt64()}"); break;
                case "signed": t.Add($"s:{id}={f.GetProperty("value").GetInt64()}"); break;
                case "boolean": t.Add($"u:{id}={(f.GetProperty("value").GetBoolean() ? 1 : 0)}"); break;
                case "fp32": t.Add($"f32:{id}={BitConverter.SingleToInt32Bits((float)Fp(f.GetProperty("value")))}"); break;
                case "fp64": t.Add($"f64:{id}={BitConverter.DoubleToInt64Bits(Fp(f.GetProperty("value")))}"); break;
                case "string": t.Add($"str:{id}={Hex(Encoding.UTF8.GetBytes(f.GetProperty("value").GetString()!))}"); break;
                case "blob": t.Add($"blob:{id}={f.GetProperty("value_hex").GetString()!.ToLowerInvariant()}"); break;
                case "sequence_begin": t.Add($"seq{{:{id}"); break;
                case "sequence_end": t.Add("seq}"); break;
                case "array": ExpectedArrayTokens(t, f, id); break;
                default: throw new InvalidOperationException("unknown op");
            }
        }
        return t;
    }

    private static void ExpectedArrayTokens(List<string> t, JsonElement f, int id)
    {
        JsonElement vals = f.GetProperty("values");
        string et = f.GetProperty("element_type").GetString()!;
        int count = vals.GetArrayLength();
        bool signed = et[0] == 'i';
        bool fp = et[0] == 'f';
        ArrayKind kind = fp
            ? (et == "fp32" ? ArrayKind.Fp32 : ArrayKind.Fp64)
            : (signed ? ArrayKind.Signed : ArrayKind.Unsigned);
        t.Add($"arr:{id}:{kind}:{count}");
        foreach (JsonElement x in vals.EnumerateArray())
        {
            if (et == "fp32")
            {
                t.Add($"f32:{id}={BitConverter.SingleToInt32Bits((float)Fp(x))}");
            }
            else if (et == "fp64")
            {
                t.Add($"f64:{id}={BitConverter.DoubleToInt64Bits(Fp(x))}");
            }
            else if (signed)
            {
                t.Add($"s:{id}={x.GetInt64()}");
            }
            else
            {
                t.Add($"u:{id}={x.GetUInt64()}");
            }
        }
    }

    // --- skip-ids scenario --------------------------------------------------

    /// <summary>
    /// A <see cref="TokenVisitor"/> that ignores fields whose id is in
    /// <c>skipIds</c>: it drops a scalar/array/string of any wire type, and the
    /// entire sub-sequence (at any nesting depth) when the id names a sequence.
    /// This models a receiver that simply ignores optional fields it does not
    /// care about, the visitor-pattern equivalent of the C API's "don't bind a
    /// destination" skip.
    /// </summary>
    /// <remarks>
    /// The drop happens <em>after</em> the decoder has parsed the field and
    /// delivered it: a push decoder has no skip path, and this class is where the
    /// skipping lives. It is therefore a model of the receiver, not a second
    /// decoder mode -- see the note on scenario 6 at the top of this file for what
    /// that means for the coverage the skip cases carry.
    /// </remarks>
    private sealed class SkippingTokenVisitor : TokenVisitor
    {
        private readonly HashSet<int> _skip;

        // Sequence nesting depth, and the depth of the skipped sequence whose
        // sub-tree we are currently dropping (-1 when not skipping a sub-tree).
        private int _depth;
        private int _skipStartDepth = -1;

        public SkippingTokenVisitor(IEnumerable<int> skipIds) => _skip = new HashSet<int>(skipIds);

        private bool Skipping => _skipStartDepth >= 0;

        /// <summary>True if the current field/element must be dropped.</summary>
        protected override bool Drop(int id) => Skipping || _skip.Contains(id);

        public override void SequenceBegin(int id)
        {
            if (Skipping)
            {
                _depth++; // still inside a dropped sub-tree
                return;
            }
            if (_skip.Contains(id))
            {
                _skipStartDepth = _depth; // begin dropping this whole sub-tree
                _depth++;
                return;
            }
            base.SequenceBegin(id);
            _depth++;
        }

        public override void SequenceEnd()
        {
            _depth--;
            if (Skipping)
            {
                if (_depth == _skipStartDepth)
                {
                    _skipStartDepth = -1; // closed the dropped sub-tree
                }
                return;
            }
            base.SequenceEnd();
        }
    }

    /// <summary>
    /// Builds the expected token stream from the vector's fields[] with the same
    /// skip rules applied, so it can be compared against a
    /// <see cref="SkippingTokenVisitor"/> decode.
    /// </summary>
    private static List<string> ExpectedTokensSkipping(JsonElement[] fields, int[] skipIds)
    {
        var skip = new HashSet<int>(skipIds);
        var t = new List<string>();
        int depth = 0;
        int skipStartDepth = -1;
        bool Skipping() => skipStartDepth >= 0;

        foreach (JsonElement f in fields)
        {
            string op = f.GetProperty("op").GetString()!;
            if (op == "sequence_begin")
            {
                int id = Id(f);
                if (Skipping()) { depth++; continue; }
                if (skip.Contains(id)) { skipStartDepth = depth; depth++; continue; }
                t.Add($"seq{{:{id}");
                depth++;
                continue;
            }
            if (op == "sequence_end")
            {
                depth--;
                if (Skipping())
                {
                    if (depth == skipStartDepth) skipStartDepth = -1;
                    continue;
                }
                t.Add("seq}");
                continue;
            }

            // A value-bearing field (scalar / float / string / blob / array).
            if (Skipping()) continue;
            int fid = f.TryGetProperty("id", out JsonElement idEl) ? idEl.GetInt32() : 0;
            if (skip.Contains(fid)) continue;
            ExpectedTokens(new[] { f }).ForEach(t.Add);
        }
        return t;
    }

    /// <summary>
    /// Cross-checks a skipping decode against the same message decoded plainly.
    /// </summary>
    /// <remarks>
    /// The per-test comparison against <see cref="ExpectedTokensSkipping"/> pits
    /// the visitor's sub-tree walk against a second implementation of the same
    /// rule, so a walk that is wrong in both places agrees with itself. These
    /// assertions come at the residual from the other side -- from the tokens an
    /// ordinary <see cref="TokenVisitor"/> recorded for the very same bytes:
    /// <list type="number">
    /// <item><description>deletions only: the skipping stream is a subsequence of
    /// the plain one, so nothing was invented, duplicated or reordered;</description></item>
    /// <item><description>every skipped id is gone, at every nesting level;</description></item>
    /// <item><description>the skip is not vacuous -- it removed at least one
    /// token, so a <c>skip_ids</c> list naming ids the vector does not contain
    /// cannot pass as a skip test;</description></item>
    /// <item><description>and where no skipped id names a sequence -- 44 of the 58
    /// vectors -- the residual must equal the plain stream with the tokens
    /// carrying a skipped id filtered out. That is a one-line filter sharing no
    /// code with the depth-tracking walk, which is what makes it an independent
    /// oracle rather than a restatement.</description></item>
    /// </list>
    /// </remarks>
    private static void AssertSkipMatchesPlainDecode(Vector v, List<string> skipped)
    {
        var plain = new TokenVisitor();
        new IStream().Feed(v.Expected, plain);
        var skipSet = new HashSet<int>(v.SkipIds!);

        Assert.True(
            IsSubsequence(skipped, plain.Tokens),
            "the skipping decode is not a subsequence of the plain decode: ["
                + string.Join(", ", skipped) + "] vs [" + string.Join(", ", plain.Tokens) + "]");

        Assert.DoesNotContain(skipped, t => TokenId(t) is int id && skipSet.Contains(id));

        Assert.True(
            skipped.Count < plain.Tokens.Count,
            "skip_ids removed nothing: the vector's skip scenario is vacuous");

        bool skipsASequence = plain.Tokens.Any(
            t => t.StartsWith("seq{:", StringComparison.Ordinal) && skipSet.Contains(TokenId(t)!.Value));
        if (!skipsASequence)
        {
            Assert.Equal(
                plain.Tokens.Where(t => TokenId(t) is not int id || !skipSet.Contains(id)).ToList(),
                skipped);
        }
    }

    /// <summary>The field id a token carries, or null for a token that has none (<c>seq}</c>).</summary>
    private static int? TokenId(string token)
    {
        int colon = token.IndexOf(':');
        if (colon < 0) return null;
        int end = colon + 1;
        while (end < token.Length && token[end] >= '0' && token[end] <= '9') end++;
        return end > colon + 1
            ? int.Parse(token.AsSpan(colon + 1, end - colon - 1), CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>True if <paramref name="part"/> is <paramref name="whole"/> with items deleted.</summary>
    private static bool IsSubsequence(List<string> part, List<string> whole)
    {
        int i = 0;
        foreach (string w in whole)
        {
            if (i < part.Count && part[i] == w) i++;
        }
        return i == part.Count;
    }

    // --- the six scenarios --------------------------------------------------

    /// <summary>
    /// True if this implementation provides every capability the vector requires.
    /// Always true here (full wire-format support); kept so a feature-reduced
    /// build can skip incompatible vectors instead of failing them.
    /// </summary>
    private static bool Runnable(Vector v) => v.Requires.All(Supported.Contains);

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void EncodeMatchesVector(string name)
    {
        Vector v = Vectors[name];
        if (!Runnable(v)) { _tally.Gated(name); return; }
        var buf = new byte[v.Expected.Length + v.Offset + 16];
        var os = new OStream(buf, v.Offset);
        ReplayEncode(os, v.Fields);

        // The produced message is the bytes after the reserved offset prefix.
        var produced = new byte[os.BytesUsed - v.Offset];
        Array.Copy(buf, v.Offset, produced, 0, produced.Length);
        Assert.Equal(v.Expected, produced);
        _tally.Check(name);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void ChunkedEncodeMatchesVector(string name)
    {
        Vector v = Vectors[name];
        if (!Runnable(v)) { _tally.Gated(name); return; }

        // Encode through deliberately tiny output buffers so the buffer-full
        // flush path (PushByte / PushRaw spilling to the FlushSink mid-field)
        // is exercised at every boundary. The streamed-out bytes must still
        // reassemble to exactly serialized.hex.
        //
        // The first size is the port's own declared MIN_OUTPUT_BUFFER, which is
        // what CORELIB_PLAN §7.2 item 4 requires this test to run at: it is the
        // size that proves the constant is real. The larger ones land the flush
        // boundary at other offsets within a field.
        foreach (int chunk in new[] { Sofab.MinOutputBuffer, 3, 7 })
        {
            var produced = new MemoryStream();
            var os = new OStream(new byte[chunk], 0, (d, o, l) => produced.Write(d, o, l));
            ReplayEncode(os, v.Fields);
            os.Flush();
            Assert.Equal(v.Expected, produced.ToArray());
            _tally.Check(name);
        }
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void RoundTripMatchesVector(string name)
    {
        Vector v = Vectors[name];
        if (!Runnable(v)) { _tally.Gated(name); return; }

        // Encode fresh, then decode what we produced: the recovered fields must
        // match the vector's structure -- an end-to-end check independent of the
        // serialized.hex ground truth.
        var buf = new byte[v.Expected.Length + 16];
        var os = new OStream(buf);
        ReplayEncode(os, v.Fields);
        var produced = new byte[os.BytesUsed];
        Array.Copy(buf, produced, os.BytesUsed);

        var visitor = new TokenVisitor();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(produced, visitor));
        Assert.Equal(ExpectedTokens(v.Fields), visitor.Tokens);
        _tally.Check(name);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void DecodeMatchesVector(string name)
    {
        Vector v = Vectors[name];
        if (!Runnable(v)) { _tally.Gated(name); return; }
        var visitor = new TokenVisitor();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(v.Expected, visitor));
        Assert.Equal(ExpectedTokens(v.Fields), visitor.Tokens);
        _tally.Check(name);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void DecodeByteByByteMatchesWhole(string name)
    {
        Vector v = Vectors[name];
        if (!Runnable(v)) { _tally.Gated(name); return; }

        var whole = new TokenVisitor();
        new IStream().Feed(v.Expected, whole);

        var oneByOne = new TokenVisitor();
        var iss = new IStream();
        foreach (byte b in v.Expected)
        {
            iss.Feed(new[] { b }, oneByOne);
        }

        Assert.Equal(whole.Tokens, oneByOne.Tokens);
        Assert.Equal(DecodeStatus.Complete, iss.Status);
        _tally.Check(name);
    }

    [Theory]
    [MemberData(nameof(SkipVectorNames))]
    public void DecodeSkippingIdsMatchesVector(string name)
    {
        Vector v = Vectors[name];
        if (!Runnable(v)) { _tally.Gated(name); return; }
        Assert.NotNull(v.SkipIds);

        // A receiver that ignores the skip_ids must still decode the remaining
        // fields and fully consume the message. Feed throws on a malformed
        // structure, and the returned DecodeStatus pins the rest: COMPLETE means
        // the bytes ended exactly at a top-level field boundary with no sequence
        // left open, so a decode that consumed a byte too few or too many cannot
        // pass by leaving the decoder mid-field.
        var visitor = new SkippingTokenVisitor(v.SkipIds!);
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(v.Expected, visitor));

        Assert.Equal(ExpectedTokensSkipping(v.Fields, v.SkipIds!), visitor.Tokens);
        AssertSkipMatchesPlainDecode(v, visitor.Tokens);
        _tally.Check(name);
    }

    [Theory]
    [MemberData(nameof(SkipVectorNames))]
    public void DecodeSkippingIdsByteByByteMatchesVector(string name)
    {
        Vector v = Vectors[name];
        if (!Runnable(v)) { _tally.Gated(name); return; }
        Assert.NotNull(v.SkipIds);

        // The chunked variant of the skip-ids scenario: the same receiver must see
        // the same residual fields when the message arrives one byte at a time
        // across many Feed calls, so every ignored field's length word, payload
        // and end marker straddles a chunk boundary. The bytes then travel the
        // per-byte state machine rather than the whole-buffer fast paths -- a
        // genuinely different route through IStream, and the one a seeded
        // one-byte-short fixlen length breaks first (36 of these cases fail on
        // it). The skip is still the receiver's, not the decoder's: what makes
        // this case fail is a decode bug that DecodeByteByByteMatchesWhole sees
        // too, on the same vector.
        var visitor = new SkippingTokenVisitor(v.SkipIds!);
        var iss = new IStream();
        foreach (byte b in v.Expected)
        {
            iss.Feed(new[] { b }, visitor);
        }

        Assert.Equal(ExpectedTokensSkipping(v.Fields, v.SkipIds!), visitor.Tokens);
        Assert.Equal(DecodeStatus.Complete, iss.Status);
        AssertSkipMatchesPlainDecode(v, visitor.Tokens);
        _tally.Check(name);
    }

    // --- loader guards ------------------------------------------------------

    /// <summary>The raw vector file, re-read independently of <see cref="Load"/>.</summary>
    private static JsonDocument RawVectorFile() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "test_vectors.json")));

    /// <summary>
    /// The loader takes the vector file whole: every vector, and every id of
    /// every <c>skip_ids</c> list, survives into the model.
    /// </summary>
    /// <remarks>
    /// This is the standing guard against the failure the C harness shipped with
    /// (corelib-c-cpp#160): a fixed <c>MAXSKIP</c> truncated an over-long
    /// <c>skip_ids</c> list, so the ids past the cap were <em>read</em> instead of
    /// skipped and the vector kept passing while testing something weaker than it
    /// claimed. Nothing on this side is capped, and this test is what says so out
    /// loud -- it compares the loaded model element by element against the raw
    /// JSON rather than trusting that no bound was introduced.
    /// </remarks>
    [Fact]
    public void LoaderTakesTheVectorFileWhole()
    {
        using JsonDocument raw = RawVectorFile();
        JsonElement[] rawVectors = raw.RootElement.GetProperty("vectors").EnumerateArray().ToArray();

        Assert.Equal(rawVectors.Length, Vectors.Count);
        foreach (JsonElement rv in rawVectors)
        {
            Vector v = Vectors[rv.GetProperty("name").GetString()!];

            if (rv.TryGetProperty("skip_ids", out JsonElement skip))
            {
                Assert.Equal(skip.EnumerateArray().Select(e => e.GetInt32()).ToArray(), v.SkipIds);
            }
            else
            {
                Assert.Null(v.SkipIds);
            }

            Assert.Equal(rv.GetProperty("fields").GetArrayLength(), v.Fields.Length);
            Assert.Equal(
                Convert.FromHexString(rv.GetProperty("serialized").GetProperty("hex").GetString()!).Length,
                v.Expected.Length);
        }
    }

    /// <summary>
    /// The sizes the regenerated file introduced are present and carried at full
    /// width: a nine-entry <c>skip_ids</c> list, a three-byte header varint (id
    /// 100001), 130-element arrays, 130-byte string/blob payloads, and an fp64
    /// array whose element length is read from the <c>fixlen_word</c>.
    /// </summary>
    /// <remarks>
    /// Each of these is a size a fixed bound would have clipped, and clipping any
    /// of them still leaves a green suite -- the vector just tests less. Naming
    /// them individually means a reintroduced cap fails here with the size that
    /// broke it, rather than somewhere downstream with a value mismatch.
    /// </remarks>
    [Fact]
    public void TheLargeShapesTheSkipVectorsNeedSurviveLoading()
    {
        Assert.True(Vectors.Values.Max(v => v.SkipIds?.Length ?? 0) >= 9, "no skip_ids list of 9 ids");

        var fields = Vectors.Values.SelectMany(v => v.Fields).ToArray();
        Assert.True(
            fields.Any(f => f.TryGetProperty("id", out JsonElement id) && id.GetInt32() >= 100001),
            "no field id needing a three-byte header varint");
        Assert.True(
            fields.Any(f => f.GetProperty("op").GetString() == "array"
                && f.GetProperty("values").GetArrayLength() >= 130),
            "no array of 130 elements");
        Assert.True(
            fields.Any(f => f.GetProperty("op").GetString() == "string"
                && Encoding.UTF8.GetByteCount(f.GetProperty("value").GetString()!) >= 130),
            "no 130-byte string payload");
        Assert.True(
            fields.Any(f => f.GetProperty("op").GetString() == "blob"
                && f.GetProperty("value_hex").GetString()!.Length >= 260),
            "no 130-byte blob payload");
        Assert.True(
            fields.Any(f => f.GetProperty("op").GetString() == "array"
                && f.GetProperty("element_type").GetString() == "fp64"),
            "no fp64 array");
    }

    /// <summary>
    /// The skip families arrived whole and every one of their vectors drives the
    /// skip scenario: the 36-vector <c>skip/matrix</c> cross product and the
    /// 16-vector <c>skip</c> axis set that corelib-c-cpp#160 added (CORELIB_PLAN
    /// §7.2 item 7).
    /// </summary>
    /// <remarks>
    /// The counts are floors, not pins: the shared file only ever grows, and a
    /// later regeneration adding vectors must not have to touch this test. What
    /// they catch is the opposite direction -- an <c>assets/test_vectors.json</c>
    /// silently reverted to the 81-vector file, which is otherwise a fully green
    /// run of a smaller suite.
    /// </remarks>
    [Fact]
    public void TheSkipFamiliesAreLoadedAndAllDriveTheSkipScenario()
    {
        Vector[] matrix = Vectors.Values.Where(v => v.Group == "skip/matrix").ToArray();
        Vector[] axes = Vectors.Values.Where(v => v.Group == "skip").ToArray();

        Assert.True(matrix.Length >= 36, $"only {matrix.Length} skip/matrix vectors loaded");
        Assert.True(axes.Length >= 16, $"only {axes.Length} skip vectors loaded");
        Assert.All(matrix.Concat(axes), v => Assert.NotNull(v.SkipIds));

        Assert.True(
            Vectors.Values.Count(v => v.SkipIds != null) >= 58,
            "fewer than 58 vectors carry skip_ids: is assets/test_vectors.json the regenerated file?");
    }

    /// <summary>
    /// The file may carry top-level blocks this suite does not replay, and the
    /// loader ignores them instead of failing.
    /// </summary>
    /// <remarks>
    /// <c>sequence_growth</c> (CORELIB_PLAN §7.2 item 8) arrived with the
    /// regenerated file and is not run here; <c>invalid_utf8</c> is run, but by
    /// StrictUtf8Tests rather than by this file. Tolerating an unrun block is
    /// what keeps §7.1's "copy it verbatim" possible -- the alternative is
    /// trimming the shared file to what this port replays, which is exactly the
    /// hand-editing §7.1 forbids.
    /// </remarks>
    [Fact]
    public void UnrunTopLevelBlocksAreToleratedNotRejected()
    {
        using JsonDocument raw = RawVectorFile();
        Assert.True(raw.RootElement.TryGetProperty("sequence_growth", out _));
        Assert.NotEmpty(Vectors);
    }
}
