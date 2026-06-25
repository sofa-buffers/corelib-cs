/*
 * SofaBuffers C# - perf single-shot workloads for instruction counting.
 *
 * ARCHITECTURE.md §10.1 requires the `perf` tool to be CPU-speed independent.
 * The managed .NET runtime exposes no hardware cycle counter, so this provides
 * the spec's second acceptable technique: non-inlined single-shot entry points
 * (one operation per process invocation) that a profiler such as Callgrind can
 * count instructions on while excluding setup, exactly as the C/Go/Rust tools do.
 *
 * The reference workloads match the other languages: a 1000-element u64 array
 * and a "typical" mixed message, each for encode and decode.
 *
 * Example (instruction count for one encode, setup excluded):
 *   valgrind --tool=callgrind --collect-atstart=no \
 *     --toggle-collect='*Callgrind.OpEncodeU64Array*' \
 *     dotnet bin/Release/net9.0/SofaBuffers.Bench.dll encode_u64_array
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Runtime.CompilerServices;

namespace SofaBuffers.Bench;

internal static class Callgrind
{
    private const int N = 1000;

    // Consumed so the JIT cannot elide the single operation.
    internal static long Blackhole;

    internal static readonly string[] Workloads =
        { "encode_u64_array", "decode_u64_array", "encode_typical", "decode_typical" };

    /// <summary>Run one workload exactly once (setup outside the measured call).</summary>
    internal static int Run(string workload)
    {
        switch (workload)
        {
            case "encode_u64_array":
            {
                ulong[] src = MakeU64();
                var buf = new byte[N * 11 + 16];
                Blackhole += OpEncodeU64Array(buf, src);
                return 0;
            }
            case "decode_u64_array":
            {
                byte[] wire = EncodeU64();
                Blackhole += OpDecodeU64Array(wire);
                return 0;
            }
            case "encode_typical":
            {
                var buf = new byte[256];
                Blackhole += OpEncodeTypical(buf);
                return 0;
            }
            case "decode_typical":
            {
                byte[] wire = EncodeTypical();
                Blackhole += OpDecodeTypical(wire);
                return 0;
            }
            default:
                Console.Error.WriteLine($"perf: unknown workload '{workload}'");
                Console.Error.WriteLine("       known: " + string.Join(", ", Workloads));
                return 2;
        }
    }

    // --- single-shot operations (NoInlining = a stable Callgrind toggle point) ---

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int OpEncodeU64Array(byte[] buf, ulong[] src)
    {
        var os = new OStream(buf);
        os.WriteArrayUnsigned(1, src);
        return os.BytesUsed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long OpDecodeU64Array(byte[] wire)
    {
        var c = new Checksum();
        new IStream().Feed(wire, c);
        return c.Acc;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int OpEncodeTypical(byte[] buf)
    {
        var os = new OStream(buf);
        EncodeTypical(os);
        return os.BytesUsed;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long OpDecodeTypical(byte[] wire)
    {
        var c = new Checksum();
        new IStream().Feed(wire, c);
        return c.Acc;
    }

    // --- shared message builders (identical to bench/perf) ------------------

    private static ulong[] MakeU64()
    {
        var a = new ulong[N];
        for (int i = 0; i < N; i++)
        {
            a[i] = (ulong)i * 0x9E37_79B9_7F4A_7C15UL;
        }
        return a;
    }

    private static byte[] EncodeU64()
    {
        var buf = new byte[N * 11 + 16];
        var os = new OStream(buf);
        os.WriteArrayUnsigned(1, MakeU64());
        var wire = new byte[os.BytesUsed];
        Array.Copy(buf, wire, os.BytesUsed);
        return wire;
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

    private static byte[] EncodeTypical()
    {
        var buf = new byte[256];
        var os = new OStream(buf);
        EncodeTypical(os);
        var wire = new byte[os.BytesUsed];
        Array.Copy(buf, wire, os.BytesUsed);
        return wire;
    }

    private sealed class Checksum : IVisitor
    {
        public long Acc;
        public void Unsigned(int id, ulong v) { Acc += (long)v ^ id; }
        public void Signed(int id, long v) { Acc += v ^ id; }
        public void Fp32(int id, float v) { Acc += BitConverter.SingleToInt32Bits(v); }
        public void Fp64(int id, double v) { Acc += BitConverter.DoubleToInt64Bits(v); }
        public void String(int id, int total, int offset, byte[] d, int o, int l) { Acc += l; }
        public void Blob(int id, int total, int offset, byte[] d, int o, int l) { Acc += l; }
    }
}
