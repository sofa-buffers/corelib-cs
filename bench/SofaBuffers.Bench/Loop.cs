/*
 * SofaBuffers C# - the measurement loop the timed tools share.
 *
 * BENCH_SPEC's "Timing" section is one rule set for both timed tools: warm up
 * first, then run a ~1 s loop against a *process* CPU clock, never wall-clock,
 * and derive MB/s as message_bytes * iterations / cpu_seconds / 1e6. Bench and
 * Perf differ only in what they print, so the loop lives here once.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Diagnostics;
using System.Globalization;

namespace SofaBuffers.Bench;

internal static class Loop
{
    /// <summary>BENCH_SPEC's reportable loop length, in CPU seconds.</summary>
    internal const double DefaultSeconds = 1.0;

    /// <summary>
    /// Warmup operation cap: past this the JIT has nothing left to learn.
    /// </summary>
    private const int WarmupOps = 200_000;

    /// <summary>Consumed after the loops so the JIT cannot elide the measured work.</summary>
    internal static long Blackhole;

    /// <summary>
    /// Cached: <see cref="Process.GetCurrentProcess"/> allocates a fresh
    /// <see cref="Process"/> object, and doing that inside a measured loop adds
    /// GC pressure to a benchmark of an allocation-sensitive codec.
    /// <see cref="Process.TotalProcessorTime"/> is cached on the object, so
    /// <see cref="Process.Refresh"/> first or it would never advance.
    /// </summary>
    private static readonly Process Self = Process.GetCurrentProcess();

    /// <summary>
    /// Length of the reportable measurement loop, in CPU seconds: BENCH_SPEC's
    /// ~1 s unless <c>SOFAB_BENCH_SECONDS</c> overrides it. The tools' own tests
    /// pass a millisecond instead — a check on the <em>shape</em> of the output
    /// would otherwise spend ten seconds per tool measuring numbers it never
    /// looks at — and pass it as an argument rather than through this variable,
    /// so tests running in parallel cannot disturb each other.
    /// </summary>
    internal static double Seconds()
    {
        string? s = Environment.GetEnvironmentVariable("SOFAB_BENCH_SECONDS");
        if (s != null
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            && v > 0)
        {
            return v;
        }
        return DefaultSeconds;
    }

    /// <summary>Process CPU time in seconds (not wall-clock), mirroring C <c>clock()</c>.</summary>
    internal static double CpuNow()
    {
        Self.Refresh();
        return Self.TotalProcessorTime.TotalSeconds;
    }

    /// <summary>One measured run: the operations performed and the CPU seconds they took.</summary>
    internal readonly record struct Result(long Iterations, double Seconds)
    {
        internal double NanosPerOp => Seconds / Iterations * 1e9;

        internal double MegabytesPerSecond(int bytes) =>
            (double)bytes * Iterations / Seconds / 1e6;
    }

    /// <summary>Warm up, then run <paramref name="body"/> for ~<paramref name="seconds"/> of CPU time.</summary>
    internal static Result Run(Func<long> body, double seconds)
    {
        Warmup(body, seconds / 4.0);
        long batch = Calibrate(body, seconds / 100.0);
        long iterations = 0;
        long acc = 0;
        double t0 = CpuNow();
        double elapsed;
        do
        {
            for (long k = 0; k < batch; k++)
            {
                acc += body();
            }
            iterations += batch;
            elapsed = CpuNow() - t0;
        }
        while (elapsed < seconds);
        Blackhole += acc;
        return new Result(iterations, elapsed);
    }

    /// <summary>
    /// Drive the hot methods to their final JIT tier. Bounded by <em>time</em> as
    /// well as by <see cref="WarmupOps"/> because the workloads span four orders
    /// of magnitude per op: 200 000 operations is a warmup for the typical
    /// message and minutes of memory bandwidth for the 1 MB blob.
    /// </summary>
    private static void Warmup(Func<long> body, double budget)
    {
        double deadline = CpuNow() + budget;
        long acc = 0;
        for (int i = 0; i < WarmupOps; i++)
        {
            acc += body();
            if ((i & 0x3F) == 0x3F && CpuNow() >= deadline)
            {
                break;
            }
        }
        Blackhole += acc;
    }

    /// <summary>
    /// Grow a batch until it spans <paramref name="budget"/> CPU seconds, so the
    /// clock read that ends it is a rounding error against the work it timed.
    /// </summary>
    /// <remarks>
    /// <see cref="CpuNow"/> reads <c>/proc/self/stat</c>, which costs on the
    /// order of tens of microseconds — far more than an entire <c>typical
    /// message</c> operation — so reading it once per iteration would measure
    /// mostly the clock. Worse, it is a fixed cost per operation rather than a
    /// scaling factor, so it would distort the workloads unevenly: barely visible
    /// on a 1000-element array, dominant on a 37-byte message. BENCH_SPEC asks
    /// for a ~1 s CPU-time loop, a warmup and a given MB/s formula; how often the
    /// clock is sampled inside that loop is ours to choose.
    /// </remarks>
    private static long Calibrate(Func<long> body, double budget)
    {
        long acc = 0;
        for (long batch = 1; ; batch *= 2)
        {
            double t0 = CpuNow();
            for (long k = 0; k < batch; k++)
            {
                acc += body();
            }
            if (CpuNow() - t0 >= budget)
            {
                Blackhole += acc;
                return batch;
            }
        }
    }
}
