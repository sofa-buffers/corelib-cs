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
 * SPDX-License-Identifier: MIT
 */

using System;

namespace SofaBuffers.Bench;

internal static class Program
{
    private static int Main(string[] args)
    {
        string which = args.Length > 0 ? args[0].ToLowerInvariant() : "perf";
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
                Console.Error.WriteLine("usage: SofaBuffers.Bench [perf|bench|all]");
                return 2;
        }
    }
}
