/*
 * SofaBuffers C# - per-operation cost benchmark.
 *
 * Mirror of bench/c/perf.c, bench/cpp/perf.cpp, benches/perf.rs and Java's Perf:
 * encodes and decodes the identical message (same field ids, types and values)
 * through the streaming API and prints the same report, so the C, C++, Rust,
 * Java and C# implementations can be compared directly.
 *
 * Two metrics per workload:
 *   1. cycles/op  -- cost of the code itself, read off a hardware cycle counter.
 *      The managed .NET runtime exposes no portable cycle counter (no RDTSC
 *      intrinsic), so this line reports that it is unavailable, exactly as the
 *      Java tool does and as the C/Rust tools do off-arch. CPU time/op below is
 *      the runtime-independent proxy for code cost. For a fully CPU-speed-
 *      independent figure, run bench/run_callgrind.sh, which is available on
 *      every target and has no such fallback.
 *   2. throughput MB/s + CPU time/op -- a "speedtest" for this machine, derived
 *      from process CPU time (not wall-clock), the .NET equivalent of the C
 *      tool's clock(). MB = 1e6 bytes.
 *
 * Both metrics are gathered over the same adaptive ~1 s CPU-time loop (Loop.cs,
 * shared with the throughput tool), so they describe the exact same work.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.IO;
using System.Text;
using static System.FormattableString;

namespace SofaBuffers.Bench;

internal static class Perf
{
    // --- hardware cycle counter ---------------------------------------------
    // The C/C++/Rust tools read the CPU's time-stamp counter (x86 TSC /
    // AArch64 cntvct_el0) for a CPU-speed-independent cost figure. The managed
    // .NET runtime exposes no portable cycle-counter intrinsic, so -- like the
    // Java tool -- we report cycles/op as unavailable and rely on CPU time/op
    // (process CPU time, runtime/clock independent) as the code-cost proxy.
    // `readonly` rather than `const`: a constant false would make the counter
    // branch unreachable code, and this is a port-specific fact, not a language
    // one — a future .NET intrinsic flips it here and nowhere else.
    private static readonly bool HaveCycles = false;

    private static double Cycles() => 0;

    // --- message under test (identical to perf.c / perf.rs) ----------------
    private const string PerfString = "perf-benchmark-message";
    private static readonly uint[] PerfSamples =
        { 1_000_000, 2_000_000, 3_000_000, 4_000_000, 5_000_000, 6_000_000, 7_000_000, 8_000_000 };
    private static readonly int[] PerfDeltas =
        { -100_000, -200_000, -300_000, -400_000, -500_000, -600_000, -700_000, -800_000 };
    private static readonly double[] PerfFp64 = { 3.14159265, 6.28318530, 9.42477795, 12.56637060 };

    /// <summary>
    /// BENCH_SPEC's twelve-field <c>perf</c> message. Its encoded size — 170
    /// bytes on every implementation — is a cross-port parity check: a different
    /// number here means the encoding has diverged.
    /// </summary>
    /// <param name="buf">output buffer (at least 170 bytes)</param>
    /// <returns>encoded size in bytes</returns>
    internal static int PerfEncode(byte[] buf)
    {
        var os = new OStream(buf);
        os.WriteUnsigned(1, 0xDEAD_BEEFUL);
        os.WriteSigned(2, -12345);
        os.WriteUnsigned(3, 0x0123_4567_89AB_CDEFUL);
        os.WriteSigned(4, -5_000_000_000_000L);
        os.WriteBoolean(5, true);
        os.WriteFp32(6, 3.14159f);
        os.WriteFp64(7, 2.718281828459045);
        os.WriteString(8, PerfString);
        os.WriteArrayUnsigned(9, PerfSamples);
        os.WriteArraySigned(10, PerfDeltas);
        os.WriteArrayFp64(11, PerfFp64);
        os.WriteSequenceBeginLazy(12);
        os.WriteUnsigned(1, 99);
        os.WriteSigned(2, -7);
        os.WriteSequenceEnd();
        return os.BytesUsed;
    }

    /// <summary>Decode sink: folds every value into a checksum and captures id 1 / id 8.</summary>
    private sealed class PerfOut : IVisitor
    {
        public long Acc;
        public int Depth;
        public uint U32Top;
        public readonly byte[] StrBuf = new byte[32];
        public int StrLen;

        public void Unsigned(int id, ulong v)
        {
            Acc += (long)v ^ id;
            if (Depth == 0 && id == 1)
            {
                U32Top = (uint)v;
            }
        }

        public void Signed(int id, long v) { Acc += v ^ id; }

        public void Fp32(int id, float v) { Acc += BitConverter.SingleToInt32Bits(v); }

        public void Fp64(int id, double v) { Acc += BitConverter.DoubleToInt64Bits(v); }

        public void String(int id, int total, int offset, byte[] d, int o, int l)
        {
            Acc += l;
            if (id == 8 && offset < StrBuf.Length)
            {
                int end = Math.Min(offset + l, StrBuf.Length);
                Array.Copy(d, o, StrBuf, offset, end - offset);
                StrLen = end;
            }
        }

        public void Blob(int id, int total, int offset, byte[] d, int o, int l) { Acc += l; }

        public void ArrayBegin(int id, ArrayKind kind, int count) { /* no-op */ }

        public void SequenceBegin(int id) { Depth++; }

        public void SequenceEnd() { Depth--; }
    }

    private static void PerfDecode(byte[] buf, int len, PerfOut outp)
    {
        new IStream().Feed(buf, 0, len, outp);
    }

    private static void Report(TextWriter output, string what, Loop.Result r, int bytes)
    {
        output.WriteLine();
        output.WriteLine($"--- perf: {what} ---");
        output.WriteLine(Invariant($"  iterations    : {r.Iterations}"));
        output.WriteLine(Invariant($"  message size  : {bytes} bytes"));
        if (HaveCycles)
        {
            output.WriteLine(Invariant($"  cycles/op     : {Cycles():F1}  (hardware cycle counter)"));
        }
        else
        {
            output.WriteLine("  cycles/op     : (cycle counter unavailable on .NET)");
        }
        output.WriteLine(Invariant($"  CPU time/op   : {r.NanosPerOp:F1} ns  (process CPU time, not wall-clock)"));
        output.WriteLine(Invariant(
            $"  throughput    : {r.MegabytesPerSecond(bytes):F1} MB/s  (speedtest, MB = 1e6 bytes)"));
    }

    internal static void Run() => Run(Console.Out, Loop.Seconds());

    /// <summary>
    /// Measure one encode and one decode of the <c>perf</c> message and print
    /// BENCH_SPEC's per-op report to <paramref name="output"/>. Every formatted
    /// number is invariant: the harness matches <c>[\d.]+</c>, which a
    /// comma-decimal culture would not produce.
    /// </summary>
    /// <param name="output">where the report goes</param>
    /// <param name="seconds">CPU seconds per measurement (BENCH_SPEC: ~1)</param>
    internal static void Run(TextWriter output, double seconds)
    {
        var buffer = new byte[512];
        int msgSize = PerfEncode(buffer);

        output.WriteLine("=== SofaBuffers C# per-op cost (cycles/op + throughput MB/s) ===");

        Loop.Result enc = Loop.Run(() => PerfEncode(buffer), seconds);
        Report(output, "serialize (stream API)", enc, msgSize);

        // Self-check that the decode actually reproduced the data.
        var check = new PerfOut();
        PerfDecode(buffer, msgSize, check);
        byte[] expected = Encoding.UTF8.GetBytes(PerfString);
        bool strOk = check.StrLen == expected.Length;
        for (int i = 0; strOk && i < expected.Length; i++)
        {
            strOk = check.StrBuf[i] == expected[i];
        }
        if (check.U32Top != 0xDEAD_BEEFU || !strOk)
        {
            Console.Error.WriteLine("perf: decode self-check failed");
            Environment.Exit(1);
        }

        Loop.Result dec = Loop.Run(
            () =>
            {
                var o = new PerfOut();
                PerfDecode(buffer, msgSize, o);
                return o.Acc;
            },
            seconds);
        Report(output, "deserialize (stream API)", dec, msgSize);

        output.WriteLine();
        output.WriteLine("cycles/op tracks code cost; MB/s is this machine's throughput.");
        if (Loop.Blackhole == 42)
        {
            output.Write(""); // keep the blackhole observably live
        }
    }
}
