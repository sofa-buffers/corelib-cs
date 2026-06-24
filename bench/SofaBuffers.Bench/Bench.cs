/*
 * SofaBuffers C# - throughput benchmark (MB/s, CPU time).
 *
 * Mirror of bench/c/bench.c, bench/cpp/bench.cpp, benches/bench.rs and Java's
 * Bench: encode / decode throughput for two workloads -- a 1000-element u64
 * array and a small "typical" mixed message. Each workload runs in a ~1 s
 * CPU-time loop and reports MB/s in the same table layout as the other tools, so
 * the implementations can be compared directly. MB = 1e6 bytes.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Diagnostics;

namespace SofaBuffers.Bench;

internal static class Bench
{
    private const int N = 1000;
    private const double MinSeconds = 1.0;

    // Consumed after the loops so the JIT cannot elide the work.
    internal static long Blackhole;

    /// <summary>Process CPU time in seconds (not wall-clock), mirroring C clock().</summary>
    private static double CpuNow() => Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;

    /// <summary>Decode sink that folds every value into a checksum (defeats elision).</summary>
    private sealed class Checksum : IVisitor
    {
        public long Acc;
        public void Unsigned(int id, ulong v) { Acc += (long)v ^ id; }
        public void Signed(int id, long v) { Acc += v ^ id; }
        public void Fp32(int id, float v) { Acc += BitConverter.SingleToInt32Bits(v); }
        public void Fp64(int id, double v) { Acc += BitConverter.DoubleToInt64Bits(v); }
        public void String(int id, int total, int offset, byte[] d, int o, int l) { Acc += l; }
        public void Blob(int id, int total, int offset, byte[] d, int o, int l) { Acc += l; }
        public void ArrayBegin(int id, ArrayKind kind, int count) { /* no-op */ }
    }

    private static ulong[] MakeSrc()
    {
        var a = new ulong[N];
        for (int i = 0; i < N; i++)
        {
            a[i] = (ulong)i * 0x9E37_79B9_7F4A_7C15UL;
        }
        return a;
    }

    private static void EncodeTypical(OStream os)
    {
        os.WriteUnsigned(1, 0xDEAD_BEEFUL);
        os.WriteSigned(2, -12345);
        os.WriteBoolean(3, true);
        os.WriteFp32(4, 3.14159f);
        os.WriteString(5, "sofab");
        os.WriteArrayUnsigned(6, new ushort[] { 10, 20, 30, 40 });
        os.WriteSequenceBegin(7);
        os.WriteUnsigned(1, 99);
        os.WriteSigned(2, -7);
        os.WriteSequenceEnd();
    }

    /// <summary>Run <paramref name="body"/> for ~1 s of CPU time (after warmup) -> MB/s.</summary>
    private static double Measure(int bytes, Action body)
    {
        for (int i = 0; i < 200_000; i++)
        {
            body(); // warmup / JIT
        }
        long it = 0;
        double t0 = CpuNow();
        double el;
        do
        {
            body();
            it++;
            el = CpuNow() - t0;
        }
        while (el < MinSeconds);
        return (double)bytes * it / el / 1e6;
    }

    internal static void Run()
    {
        ulong[] src = MakeSrc();

        // Pre-encode to learn byte sizes and to use as decode input.
        var u64Buf = new byte[N * 11 + 16];
        int u64Used;
        {
            var os = new OStream(u64Buf);
            os.WriteArrayUnsigned(1, src);
            u64Used = os.BytesUsed;
        }
        var u64Wire = new byte[u64Used];
        Array.Copy(u64Buf, u64Wire, u64Used);

        var typBuf = new byte[256];
        int typUsed;
        {
            var os = new OStream(typBuf);
            EncodeTypical(os);
            typUsed = os.BytesUsed;
        }
        var typWire = new byte[typUsed];
        Array.Copy(typBuf, typWire, typUsed);

        int ba = u64Used;
        int bt = typUsed;

        // Reused encode targets (allocation outside the timed loop).
        var encU64Out = new byte[N * 11 + 16];
        var encTypOut = new byte[256];

        long sink = 0;

        double encU64 = Measure(ba, () =>
        {
            var os = new OStream(encU64Out);
            os.WriteArrayUnsigned(1, src);
            sink += os.BytesUsed;
        });
        double encTyp = Measure(bt, () =>
        {
            var os = new OStream(encTypOut);
            EncodeTypical(os);
            sink += os.BytesUsed;
        });
        double decU64 = Measure(ba, () =>
        {
            var c = new Checksum();
            new IStream().Feed(u64Wire, c);
            sink += c.Acc;
        });
        double decTyp = Measure(bt, () =>
        {
            var c = new Checksum();
            new IStream().Feed(typWire, c);
            sink += c.Acc;
        });
        Blackhole = sink;

        Console.WriteLine("=== SofaBuffers C# throughput (CPU time, MB/s) ===");
        Console.WriteLine($"{"Workload",-26} {"MB/s",12}");
        Console.WriteLine($"{"--------",-26} {"----",12}");
        Console.WriteLine($"{"encode: u64 array (1000)",-26} {encU64,12:F2}");
        Console.WriteLine($"{"encode: typical message",-26} {encTyp,12:F2}");
        Console.WriteLine($"{"decode: u64 array (1000)",-26} {decU64,12:F2}");
        Console.WriteLine($"{"decode: typical message",-26} {decTyp,12:F2}");
        Console.WriteLine();
        Console.WriteLine("MB = 1e6 bytes. ~1s CPU-time loop per workload.");
        if (Blackhole == 42)
        {
            Console.Write("");
        }
    }
}
