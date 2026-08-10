/*
 * SofaBuffers C# - machine-independent instruction cost (Callgrind Ir/op).
 *
 * Companion to Bench.cs (throughput) and Perf.cs (per-op timing). Reports
 * instructions retired per operation: unlike wall-clock or cycle counts, an
 * instruction count is deterministic and independent of the host's clock speed
 * and scheduler, so the numbers compare across machines (and against the
 * C/C++/Rust/Go/Python/TypeScript/Java tools -- the workloads, ids and values
 * are identical, because they all come from Workloads.cs).
 *
 * The .NET runtime JITs the hot code at run time, so there is no stable native
 * `run_<workload>` symbol Callgrind could `--toggle-collect` on (and a
 * single-shot toggle would mix in the one-time JIT compilation). So --
 * BENCH_SPEC's second permitted mechanism, as in the Python, TypeScript and
 * Java ports -- bench/run_callgrind.sh runs this program at two rep counts R1
 * and R2 and subtracts the whole-process instruction counts,
 *
 *     Ir/op = ( Ir(R2) - Ir(R1) ) / ( R2 - R1 )
 *
 * which cancels *all* fixed cost exactly -- CLR startup, JIT compilation and the
 * one-time setup -- leaving the pure per-op cost. For the subtraction to be
 * clean the two runs must differ *only* in the measured rep count, so this
 * program does a fixed warmup (independent of `reps`) that puts every measured
 * op in compiled code, and run_callgrind.sh pins the JIT and sizes the heap so
 * nothing else varies between runs. Invoked as:  <workload> [reps].
 *
 * Run via: bash bench/run_callgrind.sh
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using static System.FormattableString;

namespace SofaBuffers.Bench;

internal static class Callgrind
{
    /// <summary>
    /// Warmup operations per run, independent of <c>reps</c> so it cancels in the
    /// subtraction.
    /// </summary>
    /// <remarks>
    /// Its job is to put every measured op in <em>compiled</em> code. The script
    /// pins <c>DOTNET_TieredCompilation=0</c>, so CoreCLR JITs each method to
    /// full opt on its first call and even a handful of warmup ops reaches steady
    /// state; the counts here leave room to spare and also settle the heap, so
    /// the measured loop allocates into an already-touched gen0. The
    /// <c>blob 1MB</c> rows get the smaller figure because they carry a megabyte
    /// of copying per op, which is slow under Callgrind. Override with
    /// <c>SOFAB_WARMUP</c>.
    /// </remarks>
    /// <param name="workload">the workload key</param>
    /// <returns>number of warmup operations</returns>
    internal static int WarmupFor(string workload)
    {
        string? env = Environment.GetEnvironmentVariable("SOFAB_WARMUP");
        if (env != null
            && int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
            && w >= 0)
        {
            return w;
        }
        return workload.Contains("blob", StringComparison.Ordinal) ? 200 : 2_000;
    }

    /// <summary>
    /// One rep-mode run: warm up, then perform exactly <c>reps</c> measured
    /// operations of one workload and report its encoded size on
    /// <paramref name="error"/> for the table's <c>bytes</c> column.
    /// </summary>
    /// <param name="args"><c>&lt;workload&gt; [reps]</c>; <c>reps</c> defaults to one</param>
    /// <param name="error">where usage and the <c>bytes=</c> line go</param>
    /// <returns>process exit status: 0 on success, 2 on a usage error</returns>
    internal static int Run(string[] args, TextWriter error)
    {
        List<Workloads.Workload> all = Workloads.All();
        if (args.Length < 1)
        {
            error.WriteLine("usage: SofaBuffers.Bench <workload> [reps]");
            error.WriteLine("  workloads: " + string.Join(", ", all.ConvertAll(w => w.Name)));
            return 2;
        }

        string name = args[0];
        int reps = 1;
        if (args.Length >= 2
            && !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out reps))
        {
            error.WriteLine($"reps must be an integer, not '{args[1]}'");
            return 2;
        }

        Workloads.Workload? workload = all.Find(w => w.Name == name);
        if (workload == null)
        {
            error.WriteLine($"unknown workload: {name}");
            error.WriteLine("  known: " + string.Join(", ", all.ConvertAll(w => w.Name)));
            return 2;
        }

        long sink = 0;
        for (int i = WarmupFor(name); i > 0; i--)
        {
            sink += workload.Body();
        }
        for (int i = 0; i < reps; i++)
        {
            sink += workload.Body();
        }
        Loop.Blackhole = sink;

        // stderr feeds the size column; the sink keeps the work observable.
        error.WriteLine(Invariant($"bytes={workload.Bytes} sink={Loop.Blackhole} reps={reps}"));
        return 0;
    }
}
