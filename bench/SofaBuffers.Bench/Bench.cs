/*
 * SofaBuffers C# - throughput benchmark (MB/s, CPU time).
 *
 * Mirror of bench/c/bench.c, bench/cpp/bench.cpp, benches/bench.rs and Java's
 * Bench: encode / decode throughput for BENCH_SPEC's workload set -- a
 * 1000-element u64 array, a small "typical" mixed message, an unbounded 1 MB
 * blob and the "composite" message that reaches the paths the flat datasets
 * miss. Each workload runs in a ~1 s CPU-time loop and reports MB/s in the same
 * table layout as the other tools, so the implementations can be compared
 * directly. MB = 1e6 bytes.
 *
 * **Read the blob 1MB rows against each other, not against the others.** Five
 * bytes of that message are metadata and a million are payload, so its MB/s is
 * this machine's memory bandwidth rather than a statement about the corelib --
 * and the streamed row can even come out ahead of the one-shot one, since a
 * 4 KiB window stays in L1 while a one-shot encode writes a megabyte out to
 * memory. The flush machinery's own cost (CORELIB_PLAN §5.1) does not survive
 * that; bench/run_callgrind.sh is what measures it.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using static System.FormattableString;

namespace SofaBuffers.Bench;

internal static class Bench
{
    internal static void Run() => Run(Console.Out, Loop.Seconds());

    /// <summary>
    /// Time every workload and print BENCH_SPEC's throughput table to
    /// <paramref name="output"/>.
    /// </summary>
    /// <remarks>
    /// Every number goes through a composite format string, which is
    /// locale-sensitive: on a machine whose culture is comma-decimal an
    /// unqualified interpolation would print <c>1234,56</c>, which the harness's
    /// <c>[\d.]+</c> does not match — the row would silently vanish from the
    /// comparison table rather than fail. Hence <see cref="Invariant"/> on every
    /// formatted line.
    /// </remarks>
    /// <param name="output">where the table goes</param>
    /// <param name="seconds">CPU seconds per workload (BENCH_SPEC: ~1)</param>
    internal static void Run(TextWriter output, double seconds)
    {
        List<Workloads.Workload> workloads = Workloads.All();
        var mbs = new double[workloads.Count];
        for (int i = 0; i < workloads.Count; i++)
        {
            Workloads.Workload w = workloads[i];
            mbs[i] = Loop.Run(w.Body, seconds).MegabytesPerSecond(w.Bytes);
        }

        output.WriteLine("=== SofaBuffers C# throughput (CPU time, MB/s) ===");
        output.WriteLine(Invariant($"{"Workload",-26} {"MB/s",12}"));
        output.WriteLine(Invariant($"{"--------",-26} {"----",12}"));
        for (int i = 0; i < workloads.Count; i++)
        {
            output.WriteLine(Invariant($"{workloads[i].Label,-26} {mbs[i],12:F2}"));
        }
        output.WriteLine();
        output.WriteLine("MB = 1e6 bytes. ~1s CPU-time loop per workload.");
        output.WriteLine(
            "blob 1MB is bandwidth-bound: read one-shot vs streaming, not either alone.");
        if (Loop.Blackhole == 42)
        {
            output.Write(""); // keep the blackhole observably live
        }
    }
}
