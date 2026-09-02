/*
 * SofaBuffers C# - the benchmark tools against BENCH_SPEC.
 *
 * BENCH_SPEC is the cross-language contract for `bench` / `perf` /
 * `run_callgrind.sh`: the same workloads on the same data, printed in a grammar
 * a central harness parses into the comparison tables. Two things can silently
 * break that, and neither is visible from inside the library:
 *
 *   * a **dataset** that drifts -- the encoded sizes (the perf message's 170
 *     bytes, the blob message's 1,000,005, the composite message's 956) are the
 *     spec's own parity checks;
 *   * a **row** that goes missing or gets misspelled -- the harness matches row
 *     labels by regex, so a renamed or absent row is dropped from the table
 *     rather than reported, and a workload nobody notices is missing measures
 *     nothing.
 *
 * So the tools are run here (over a millisecond-scale loop, not the reportable
 * ~1 s one) and their output is matched against the spec's own regexes. This is
 * a format and dataset test, never a performance assertion: no timing figure is
 * checked.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using SofaBuffers.Bench;
// `Bench` alone would bind to the *namespace* from inside SofaBuffers.Tests.
using BenchTool = SofaBuffers.Bench.Bench;
using sofab;
using Xunit;

namespace SofaBuffers.Tests;

public class BenchSpecTests
{
    // --- the harness's own regexes (BENCH_SPEC "Output grammar") -------------

    private static readonly Regex ThroughputHeader = new("=== SofaBuffers (.+?) throughput");
    private static readonly Regex PerOpHeader = new("=== SofaBuffers (.+?) per-op");
    private static readonly Regex Row = new(
        @"^(encode|decode):\s+(u64 array \(1000\)|typical message|blob 1MB one-shot"
        + @"|blob 1MB streaming|blob 1MB passthrough|blob 1MB|composite skip-all"
        + @"|composite)\s+([\d.]+)$");

    /// <summary>
    /// Every row BENCH_SPEC requires, in the order it lists them. The optional
    /// <c>blob 1MB passthrough</c> row is absent on purpose: this port implements
    /// no pass-through, and BENCH_SPEC says such a port omits the row rather than
    /// printing a placeholder.
    /// </summary>
    private static readonly string[] RequiredRows =
    {
        "encode: u64 array (1000)",
        "encode: typical message",
        "encode: blob 1MB one-shot",
        "encode: blob 1MB streaming",
        "encode: composite",
        "decode: u64 array (1000)",
        "decode: typical message",
        "decode: blob 1MB",
        "decode: composite",
        "decode: composite skip-all",
    };

    /// <summary>A measurement loop short enough that these tests check shape, not speed.</summary>
    private const double FastLoop = 0.001;

    /// <summary>Repository root: this file lives in tests/SofaBuffers.Tests/.</summary>
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    private static string ScriptPath() => Path.Combine(RepoRoot(), "bench", "run_callgrind.sh");

    // --- the workload set ----------------------------------------------------

    [Fact]
    public void TheWorkloadSetIsExactlyTheRowsBenchSpecRequires()
    {
        // The tools measure whatever this list holds, so it *is* the row set.
        Assert.Equal(RequiredRows, Workloads.All().Select(w => w.Label));
    }

    // --- datasets ------------------------------------------------------------

    [Fact]
    public void TheU64ArrayIsTheLiteralFormula()
    {
        ulong[] src = Workloads.MakeU64Array();
        Assert.Equal(1000, src.Length);
        Assert.Equal(
            Enumerable.Range(0, 1000).Select(i => (ulong)i * 0x9E37_79B9_7F4A_7C15UL),
            src);
    }

    [Fact]
    public void TheBlobPayloadIsTheLiteralFormula()
    {
        byte[] blob = Workloads.MakeBlob();
        Assert.Equal(1_000_000, blob.Length);
        var want = new byte[1_000_000];
        for (int i = 0; i < want.Length; i++)
        {
            want[i] = (byte)(((ulong)i * 0x9E37_79B9_7F4A_7C15UL) & 0xFF);
        }
        Assert.Equal(want, blob);
    }

    [Fact]
    public void TheBlobMessageIs1000005Bytes()
    {
        byte[] blob = Workloads.MakeBlob();
        byte[] wire = OneShotBlob(blob);

        Assert.Equal(1_000_005, wire.Length); // a cross-port parity check, like perf's 170
        Assert.Equal(Workloads.BlobEncoded, wire.Length);
        // BENCH_SPEC spells the framing out: a 1-byte header (id 1, FIXLEN) and a
        // 4-byte fixlen_word ((1000000 << 3) | 3), then the payload.
        Assert.Equal((byte)((1 << 3) | 2), wire[0]);
        Assert.Equal(Varint(((ulong)1_000_000 << 3) | 3), wire[1..5]);
        Assert.Equal(blob, wire[5..]);
    }

    [Fact]
    public void ThePerfMessageIs170Bytes()
    {
        Assert.Equal(170, Perf.PerfEncode(new byte[512]));
    }

    [Fact]
    public void TheCompositeMessageIs956Bytes()
    {
        // This port's contribution of a parity check, as perf's 170 is.
        Assert.Equal(956, CompositeWire().Length);
    }

    /// <summary>Each composite field is in the suite for a reason; check each is there.</summary>
    [Fact]
    public void TheCompositeMessageCarriesWhatBenchSpecAsksFor()
    {
        byte[] wire = CompositeWire();
        var seen = new Composite();
        new IStream().Feed(wire, seen);

        // Field 4 is equal to its declared default, so the encoder must not write
        // it: the ids that reach the wire are 1, 2, 3 and 130.
        Assert.Equal(new[] { 1, 2, 3, 130 }, seen.TopIds);

        // id 1: the wrapper array -- one field header per element, element id =
        // array index, so ids 0..15 take a one-byte header and 16..63 two.
        Assert.Equal(Enumerable.Range(0, 64), seen.ElementIds);
        Assert.Equal(Enumerable.Range(0, 64).Select(i => "item-" + i), seen.Elements);

        // id 2: 320 UTF-8 bytes across all four sequence widths.
        Assert.Equal(320, seen.TextTotal);
        Assert.Equal(Encoding.UTF8.GetBytes(Workloads.MakeText()), seen.Text.ToArray());

        // id 3: nesting at depth 3, carrying 7 and -1.
        Assert.Equal(3, seen.MaxDepth);
        Assert.Equal(new long[] { 7, -1 }, seen.Nested);

        // id 130: the one two-byte field header in the suite, (130 << 3) | 0.
        Assert.Equal(0xDEAD_BEEFUL, seen.TwoByteHeaderField);
        Assert.Equal(Varint(130UL << 3), wire[^7..^5]);
    }

    // --- the streaming rows drive the streaming API --------------------------

    /// <summary>
    /// The streaming row must be the <em>same message</em>, only flushed 245
    /// times. A row driven through a 4096-byte buffer that produced anything
    /// other than the one-shot bytes would make the pair's difference -- the only
    /// number BENCH_SPEC asks anyone to read here -- meaningless.
    /// </summary>
    [Fact]
    public void TheStreamingBlobEncodeProducesTheOneShotBytes()
    {
        byte[] blob = Workloads.MakeBlob();
        var flushed = new MemoryStream();
        int flushes = 0;
        FlushSink capture = (data, off, len) =>
        {
            flushes++;
            flushed.Write(data, off, len);
        };

        var os = new OStream(new byte[Workloads.StreamBuffer], 0, capture);
        os.WriteBlob(1, blob);
        os.Flush();

        Assert.Equal(OneShotBlob(blob), flushed.ToArray());
        // 1,000,005 bytes through a 4096-byte window is 245 flushes.
        Assert.Equal(245, flushes);
    }

    /// <summary>
    /// BENCH_SPEC: the streaming sink consumes and discards. An accumulating sink
    /// would charge the streaming row a copy the one-shot row never pays.
    /// </summary>
    [Fact]
    public void TheStreamingSinkConsumesAndDiscards()
    {
        var discard = new Workloads.Discard();
        discard.Flush(new byte[] { 1, 2 }, 0, 2);
        discard.Flush(new byte[] { 3, 4 }, 0, 2);
        Assert.Equal(1 ^ 3, discard.Acc); // one byte per call, folded -- nothing kept

        foreach (FieldInfo f in typeof(Workloads.Discard).GetFields(
                     BindingFlags.Instance | BindingFlags.Static
                     | BindingFlags.Public | BindingFlags.NonPublic))
        {
            // A sink field that can hold bytes is somewhere to accumulate into.
            Assert.True(f.FieldType.IsPrimitive, f.ToString());
        }
    }

    /// <summary>The decode row must actually stream: 4096-byte chunks, not one big feed.</summary>
    [Fact]
    public void TheBlobDecodeIsFedInChunks()
    {
        byte[] wire = OneShotBlob(Workloads.MakeBlob());
        var seen = new Chunks();
        var istream = new IStream();
        DecodeStatus status = DecodeStatus.Incomplete;
        for (int off = 0; off < wire.Length; off += Workloads.StreamBuffer)
        {
            status = istream.Feed(
                wire, off, Math.Min(Workloads.StreamBuffer, wire.Length - off), seen);
        }
        Assert.Equal(DecodeStatus.Complete, status);
        Assert.Equal(1_000_000, seen.Bytes); // the whole payload arrived
        // A payload delivered in one piece is not a streaming decode.
        Assert.True(seen.Calls >= 244, "chunks: " + seen.Calls);
    }

    /// <summary>
    /// The skip-all row must still be a complete decode: it walks the whole
    /// message and materializes nothing, which is only a meaningful measurement
    /// if the walk actually finished.
    /// </summary>
    [Fact]
    public void TheSkipAllRowWalksTheWholeMessage()
    {
        Workloads.Workload skip =
            Workloads.All().Single(w => w.Name == "decode_composite_skip");
        Assert.Equal((long)DecodeStatus.Complete, skip.Body());
    }

    // --- output grammar ------------------------------------------------------

    [Fact]
    public void TheBenchOutputMatchesTheSpecGrammar()
    {
        List<string> output = RunTool(w => BenchTool.Run(w, FastLoop));

        Match header = ThroughputHeader.Match(output[0]);
        Assert.True(header.Success, output[0]);
        // The captured label picks the display name in the comparison tables.
        Assert.Equal("C#", header.Groups[1].Value);
        Assert.Equal(
            new[] { "Workload", "MB/s" },
            output[1].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("MB = 1e6 bytes. ~1s CPU-time loop per workload.", output);

        List<Match> rows = RowsOf(output);
        Assert.Equal(RequiredRows, rows.Select(m => m.Groups[1].Value + ": " + m.Groups[2].Value));
        foreach (Match m in rows)
        {
            Assert.True(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) > 0, m.Value);
            // label left-justified to 26, value right-justified to 12, 2 decimals
            Assert.Equal(39, m.Groups[3].Index + m.Groups[3].Length);
            Assert.Matches(@"\.\d\d$", m.Groups[3].Value);
        }
    }

    [Fact]
    public void ThePerfOutputMatchesTheSpecGrammar()
    {
        List<string> lines = RunTool(w => Perf.Run(w, FastLoop));
        string output = string.Join("\n", lines);

        Match header = PerOpHeader.Match(lines[0]);
        Assert.True(header.Success, lines[0]);
        Assert.Equal("C#", header.Groups[1].Value);
        Assert.Contains("--- perf: serialize", output);
        Assert.Contains("--- perf: deserialize", output);
        Assert.EndsWith(
            "cycles/op tracks code cost; MB/s is this machine's throughput.",
            output.TrimEnd());

        // Five value lines per section, and .NET exposes no hardware cycle
        // counter, so BENCH_SPEC's parenthetical stands in for the number.
        Assert.Equal(2, Count(output, @"^  iterations    : \d+$"));
        Assert.Equal(2, Count(output, @"^  message size  : 170 bytes$"));
        Assert.Equal(2, Count(output, @"^  cycles/op     : \(.*unavailable.*\)$"));
        Assert.Equal(2, Count(output, @"^  CPU time/op   : [\d.]+ ns  .*$"));
        Assert.Equal(2, Count(output, @"^  throughput    : [\d.]+ MB/s  .*$"));
    }

    /// <summary>
    /// Every number in both tables goes through a composite format string, which
    /// is locale-sensitive: on a machine whose culture is comma-decimal an
    /// unqualified interpolation would print <c>1234,56</c>, which the harness's
    /// <c>[\d.]+</c> does not match -- the row would silently vanish from the
    /// comparison table rather than fail.
    /// </summary>
    /// <remarks>
    /// The comma-decimal culture is built by hand rather than looked up by name:
    /// a runtime in globalization-invariant mode (no ICU — which is how this
    /// repo's own container runs) knows no <c>de-DE</c> to look up, and the test
    /// would then fail for the wrong reason instead of exercising the formatting.
    /// </remarks>
    [Fact]
    public void TheTablesAreLocaleIndependent()
    {
        var commaDecimal = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaDecimal.NumberFormat.NumberDecimalSeparator = ",";
        commaDecimal.NumberFormat.NumberGroupSeparator = ".";

        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = commaDecimal;
            List<Match> rows = RowsOf(RunTool(w => BenchTool.Run(w, FastLoop)));
            Assert.Equal(
                RequiredRows,
                rows.Select(m => m.Groups[1].Value + ": " + m.Groups[2].Value));

            string perf = string.Join("\n", RunTool(w => Perf.Run(w, FastLoop)));
            Assert.Equal(2, Count(perf, @"^  CPU time/op   : [\d.]+ ns  .*$"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>The table's data rows, each already matched against the harness's regex.</summary>
    private static List<Match> RowsOf(List<string> output)
    {
        var rows = new List<Match>();
        foreach (string line in output)
        {
            if (!line.StartsWith("encode:", StringComparison.Ordinal)
                && !line.StartsWith("decode:", StringComparison.Ordinal))
            {
                continue;
            }
            Match m = Row.Match(line);
            Assert.True(m.Success, "row is unparseable by the harness regex: '" + line + "'");
            rows.Add(m);
        }
        Assert.NotEmpty(rows);
        return rows;
    }

    // --- the Callgrind rep mode ---------------------------------------------

    public static TheoryData<string> WorkloadNames()
    {
        var data = new TheoryData<string>();
        foreach (Workloads.Workload w in Workloads.All())
        {
            data.Add(w.Name);
        }
        return data;
    }

    /// <summary>
    /// <c>run_callgrind.sh</c> drives each workload by name at two rep counts; a
    /// key that no longer runs would print a dash in the table instead of
    /// failing, so it is checked here.
    /// </summary>
    [Theory]
    [MemberData(nameof(WorkloadNames))]
    public void EveryWorkloadRunsOneRep(string name)
    {
        var err = new StringWriter();
        Assert.Equal(0, Callgrind.Run(new[] { name, "1" }, err));
        Assert.Matches(@"^bytes=\d+ sink=-?\d+ reps=1$", err.ToString().Trim());
    }

    [Fact]
    public void AnUnknownWorkloadIsRejected()
    {
        var err = new StringWriter();
        Assert.Equal(2, Callgrind.Run(new[] { "encode_nothing", "1" }, err));
        Assert.Contains("unknown workload", err.ToString());
    }

    [Fact]
    public void ANonNumericRepCountIsRejected()
    {
        var err = new StringWriter();
        Assert.Equal(2, Callgrind.Run(new[] { "encode_typical", "lots" }, err));
        Assert.Contains("reps must be an integer", err.ToString());
    }

    [Fact]
    public void NoArgumentsPrintsUsage()
    {
        var err = new StringWriter();
        Assert.Equal(2, Callgrind.Run(Array.Empty<string>(), err));
        Assert.Contains("usage:", err.ToString());
    }

    /// <summary>
    /// The script's workload list and the tool's registry must agree -- a
    /// workload missing from the script is a row missing from the Ir/op table,
    /// and a label only the script knows is a label the harness may not
    /// recognise.
    /// </summary>
    [Fact]
    public void TheCallgrindScriptDrivesEveryWorkload()
    {
        Assert.True(File.Exists(ScriptPath()), ScriptPath());
        string script = File.ReadAllText(ScriptPath());
        foreach (Workloads.Workload w in Workloads.All())
        {
            Assert.Matches(@"\b" + Regex.Escape(w.Name) + @"\b", script);
            Assert.Contains(w.Label, script);
        }
        foreach (string line in File.ReadAllLines(ScriptPath()))
        {
            // BENCH_SPEC's optional row is omitted, not printed as a placeholder.
            Assert.True(
                line.TrimStart().StartsWith("#", StringComparison.Ordinal)
                || !line.Contains("passthrough", StringComparison.Ordinal),
                line);
        }
    }

    /// <summary>
    /// The two-rep subtraction only cancels fixed cost if compilation has already
    /// happened when the measured loop starts. CoreCLR reaches full optimization
    /// on a method's first call only with tiered compilation off, so the script
    /// must pin it -- otherwise a tier-up could land inside the high-rep run
    /// alone and charge a single op with the whole JIT -- and every workload must
    /// then run at least one warmup op before it is measured.
    /// </summary>
    [Fact]
    public void EveryWorkloadIsCompiledBeforeItIsMeasured()
    {
        string script = File.ReadAllText(ScriptPath());
        Assert.Matches("DOTNET_TieredCompilation=0", script);
        foreach (Workloads.Workload w in Workloads.All())
        {
            Assert.True(Callgrind.WarmupFor(w.Name) >= 1, w.Name);
        }
    }

    /// <summary>
    /// The script must run the built assembly, not the reference assembly of the
    /// same name the SDK leaves in <c>obj/</c>: that one has no method bodies, so
    /// the measurement would fail rather than mislead -- but only on a machine
    /// where <c>find</c> happened to reach <c>obj/</c> first.
    /// </summary>
    [Fact]
    public void TheCallgrindScriptRunsTheBuiltAssembly()
    {
        string script = File.ReadAllText(ScriptPath());
        Assert.Contains("*/bin/Release/", script);
        Assert.DoesNotContain("-path '*Release*'", script);
    }

    // --- plumbing ------------------------------------------------------------

    /// <summary>Run a tool over a millisecond loop and return its output lines.</summary>
    private static List<string> RunTool(Action<TextWriter> tool)
    {
        var buf = new StringWriter();
        tool(buf);
        return buf.ToString()
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToList();
    }

    /// <summary>The BENCH_SPEC blob message, encoded one-shot into a hand-sized buffer.</summary>
    private static byte[] OneShotBlob(byte[] blob)
    {
        var buf = new byte[Workloads.BlobEncoded];
        var os = new OStream(buf);
        os.WriteBlob(1, blob);
        Assert.Equal(buf.Length, os.BytesUsed);
        return buf;
    }

    private static byte[] CompositeWire()
    {
        var buf = new byte[4096];
        var os = new OStream(buf);
        Workloads.EncodeComposite(os, Workloads.MakeItems(), Workloads.MakeText());
        return buf[..os.BytesUsed];
    }

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        ulong v = value;
        while ((v & ~0x7FUL) != 0)
        {
            bytes.Add((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
        bytes.Add((byte)v);
        return bytes.ToArray();
    }

    private static int Count(string haystack, string pattern) =>
        Regex.Matches(haystack, pattern, RegexOptions.Multiline).Count;

    // --- visitors ------------------------------------------------------------

    /// <summary>Records everything BENCH_SPEC says the composite message must contain.</summary>
    private sealed class Composite : IVisitor
    {
        internal readonly List<int> TopIds = new();
        internal readonly List<int> ElementIds = new();
        internal readonly List<string> Elements = new();
        internal readonly MemoryStream Text = new();
        internal readonly List<long> Nested = new();
        internal int TextTotal;
        internal int Depth;
        internal int MaxDepth;
        internal ulong TwoByteHeaderField;

        public void SequenceBegin(int id)
        {
            if (Depth == 0)
            {
                TopIds.Add(id);
            }
            Depth++;
            MaxDepth = Math.Max(MaxDepth, Depth);
        }

        public void SequenceEnd() => Depth--;

        public void Unsigned(int id, ulong v)
        {
            if (Depth == 0)
            {
                TopIds.Add(id);
                TwoByteHeaderField = v;
            }
            else
            {
                Nested.Add((long)v);
            }
        }

        public void Signed(int id, long v)
        {
            if (Depth == 0)
            {
                TopIds.Add(id);
            }
            else
            {
                Nested.Add(v);
            }
        }

        public void String(int id, int total, int offset, byte[] d, int o, int l)
        {
            if (Depth == 1)
            {
                // a wrapper-array element
                ElementIds.Add(id);
                Elements.Add(Encoding.UTF8.GetString(d, o, l));
            }
            else
            {
                if (offset == 0)
                {
                    TopIds.Add(id);
                    TextTotal = total;
                }
                Text.Write(d, o, l);
            }
        }
    }

    /// <summary>Counts the pieces a chunked blob decode arrives in.</summary>
    private sealed class Chunks : IVisitor
    {
        internal int Calls;
        internal long Bytes;

        public void Blob(int id, int total, int offset, byte[] d, int o, int l)
        {
            Calls++;
            Bytes += l;
        }
    }
}
