/*
 * SofaBuffers C# - benchmark entry point.
 *
 * BENCH_SPEC's three tools, selected by argument:
 *   perf         -- per-op cost: a CPU-speed-independent figure + throughput MB/s
 *   bench        -- the throughput table in MB/s for every BENCH_SPEC workload
 *   <workload> N -- N measured operations of one workload, for the two-rep-count
 *                   instruction counting bench/run_callgrind.sh drives
 *
 * Run with (the project multi-targets, so `dotnet run` needs a framework):
 *   dotnet run -c Release --project bench/SofaBuffers.Bench -f net10.0 -- perf
 *   dotnet run -c Release --project bench/SofaBuffers.Bench -f net10.0 -- bench
 *   bash bench/run_callgrind.sh
 *
 * SPDX-License-Identifier: MIT
 */

using System;

namespace SofaBuffers.Bench;

internal static class Program
{
    private static int Main(string[] args)
    {
        string which = args.Length > 0 ? args[0] : "perf";

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
                // Anything else is a workload key for the Callgrind rep mode; it
                // reports an unknown one itself, against the single workload
                // registry, so no second list of names can drift out of date.
                return Callgrind.Run(args, Console.Error);
        }
    }
}
