/*
 * SofaBuffers C# - benchmark entry point.
 *
 * Two tools, selected by argument, mirroring the C/C++/Rust/Java benchmarks:
 *   perf  -- per-op cost: cycles/op (CPU-speed independent) + throughput MB/s
 *   bench -- throughput table in MB/s for encode/decode workloads
 *
 * Run with:
 *   dotnet run -c Release --project bench/SofaBuffers.Bench -- perf
 *   dotnet run -c Release --project bench/SofaBuffers.Bench -- bench
 *
 * A single-shot Callgrind workload (CPU-speed-independent instruction counting,
 * ARCHITECTURE.md §10.1) is selected by naming the workload directly:
 *   dotnet run -c Release --project bench/SofaBuffers.Bench -- encode_u64_array
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Linq;

namespace SofaBuffers.Bench;

internal static class Program
{
    private static int Main(string[] args)
    {
        string which = args.Length > 0 ? args[0].ToLowerInvariant() : "perf";

        // Single-shot named workloads for Callgrind instruction counting.
        if (Callgrind.Workloads.Contains(which))
        {
            return Callgrind.Run(which);
        }

        switch (which)
        {
            case "perf":
                Perf.Run();
                return 0;
            case "bench":
                Bench.Run();
                return 0;
            case "all":
                Perf.Run();
                Console.WriteLine();
                Bench.Run();
                return 0;
            default:
                Console.Error.WriteLine("usage: SofaBuffers.Bench [perf|bench|all|<workload>]");
                Console.Error.WriteLine("  workloads (single-shot, for Callgrind): " + string.Join(", ", Callgrind.Workloads));
                return 2;
        }
    }
}
