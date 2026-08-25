/*
 * SofaBuffers C# - CORELIB_PLAN §6.6.4: the codec allocates nothing after its
 * one-time construction, and that is checked by *measurement*, not by reading
 * the source.
 *
 * §6.6 permits the codec's fixed-size state to be laid down when the encoder or
 * decoder is constructed, and forbids every allocation after that: "write, feed,
 * flush and every path they reach perform zero allocations". §6.6.4 makes the
 * measurement normative - "an allocation count, or the heap high-water mark, over
 * a complete encode and a complete decode, measured after the codec's one-time
 * construction", which on a runtime that does not box the codec's values (C#
 * scalars are structs, and every callback carries them by value) MUST be zero.
 *
 * The measurement is only worth what its warm-up is worth. Warming the JIT on the
 * *instance under measurement* would also perform any one-time per-stream
 * allocation inside the warm-up window and hide exactly the defects this file
 * exists to catch - a lazily created transcoder, a pending run that grows on the
 * first deep nesting. So every warm-up here runs on a separate instance, and the
 * assertion is exact zero rather than a threshold.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Text;
using Xunit;

namespace SofaBuffers.Tests;

public class CodecAllocationTests
{
    /// <summary>
    /// Construct a fresh codec, then measure only what running <paramref name="body"/>
    /// over it allocates; repeat until a run comes back at exactly zero, and fail
    /// with every figure seen if none does.
    /// </summary>
    /// <remarks>
    /// The construction is deliberately outside the measured window: §6.6 makes
    /// one-time construction the boundary and lets it allocate the codec's
    /// fixed-size state, and §6.6.4 measures "after the codec's one-time
    /// construction". It is a fresh codec every attempt all the same, because a
    /// per-stream allocation is exactly what this is looking for.
    /// <para>
    /// The property under test is deterministic: a codec that takes storage after
    /// construction takes it on <em>every</em> fresh instance, so every attempt
    /// reports it and the retry cannot mask one. What the retry absorbs is the
    /// runtime landing something of its own in the window - a tier-1 transition, a
    /// GC bookkeeping charge - which happens to whichever call it happens to and is
    /// not a property of the codec at all. So the assertion stays exact zero
    /// (§6.6.4 admits no threshold) without being flaky.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">the codec type</typeparam>
    /// <param name="construct">builds a fresh codec, outside the measured window</param>
    /// <param name="body">the complete encode or decode to measure</param>
    private static void AssertAllocatesNothing<T>(Func<T> construct, Action<T> body)
    {
        const int attempts = 5;
        var seen = new long[attempts];
        for (int i = 0; i < attempts; i++)
        {
            T codec = construct();
            long before = GC.GetAllocatedBytesForCurrentThread();
            body(codec);
            seen[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            if (seen[i] == 0)
            {
                return;
            }
        }
        Assert.Fail(
            "the codec allocated after construction on every attempt (bytes: " +
            string.Join(", ", seen) + "); CORELIB_PLAN §6.6.4 requires zero");
    }

    /// <summary>A sink that counts and nothing else, so measuring an encode measures the encoder.</summary>
    private sealed class CountingSink
    {
        public long Bytes;

        public void Write(byte[] data, int offset, int length) => Bytes += length;
    }

    /// <summary>A visitor that folds every callback into a counter: no storage, no allocation.</summary>
    private sealed class Checksum : IVisitor
    {
        public long Acc;

        public void Unsigned(int id, ulong value) => Acc += (long)value + id;

        public void Signed(int id, long value) => Acc += value + id;

        public void Fp32(int id, float value) => Acc += BitConverter.SingleToInt32Bits(value);

        public void Fp64(int id, double value) => Acc += BitConverter.DoubleToInt64Bits(value);

        public void String(int id, int total, int offset, byte[] data, int co, int cl) =>
            Acc += total + offset + cl;

        public void Blob(int id, int total, int offset, byte[] data, int co, int cl) =>
            Acc += total + offset + cl;

        public void FixlenBegin(int id, FixlenType subtype, int total) => Acc += total;

        public void ArrayBegin(int id, ArrayKind kind, int count) => Acc += count;

        public void SequenceBegin(int id) => Acc += id;

        public void SequenceEnd() => Acc++;
    }

    /// <summary>Mixed-width text, long enough to span any buffer under test.</summary>
    private static string MixedText(int repeats)
    {
        const string unit = "abc éß €中 \U0001F600\U00010348 ";
        var sb = new StringBuilder(unit.Length * repeats);
        for (int i = 0; i < repeats; i++)
        {
            sb.Append(unit);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A complete encode: every scalar and array writer, a blob, a string far
    /// larger than the buffer, and sequence nesting to <c>MAX_DEPTH</c>.
    /// </summary>
    private static void CompleteEncode(OStream os, Encodables e)
    {
        os.WriteUnsigned(1, 300);
        os.WriteSigned(2, -300);
        os.WriteBoolean(3, true);
        os.WriteFp32(4, 1.5f);
        os.WriteFp64(5, -2.5);
        os.WriteString(6, "short ascii");
        os.WriteString(7, e.Text);                       // spans the buffer
        os.WriteBlob(8, e.Blob);
        os.WriteFixlen(9, e.Utf8, 0, e.Utf8.Length, FixlenType.String);
        os.WriteArrayUnsigned(10, e.U8);
        os.WriteArrayUnsigned(11, e.U16);
        os.WriteArrayUnsigned(12, e.U32);
        os.WriteArrayUnsigned(13, e.U64);
        os.WriteArraySigned(14, e.I8);
        os.WriteArraySigned(15, e.I16);
        os.WriteArraySigned(16, e.I32);
        os.WriteArraySigned(17, e.I64);
        os.WriteArrayFp32(18, e.F32);
        os.WriteArrayFp64(19, e.F64);

        // Nesting to the format's limit: the pending run is what §6.0.1 requires to
        // be sized at construction, and 255 levels is the whole of it.
        for (int d = 0; d < 255; d++)
        {
            os.WriteSequenceBeginLazy(d);
        }
        os.WriteUnsigned(20, 1);                          // commits all 255 headers
        for (int d = 0; d < 255; d++)
        {
            os.WriteSequenceEnd();
        }

        // ... and once more where every level stays empty and is dropped again.
        for (int d = 0; d < 255; d++)
        {
            os.WriteSequenceBeginLazy(d);
        }
        for (int d = 0; d < 255; d++)
        {
            os.WriteSequenceEnd();
        }
        os.Flush();
    }

    /// <summary>Everything a <see cref="CompleteEncode"/> writes, allocated once up front.</summary>
    private sealed class Encodables
    {
        public readonly string Text = MixedText(200);
        public readonly byte[] Blob = new byte[4096];
        public readonly byte[] Utf8 = Encoding.UTF8.GetBytes("valid é \U0001F600");
        public readonly byte[] U8 = new byte[64];
        public readonly ushort[] U16 = new ushort[64];
        public readonly uint[] U32 = new uint[64];
        public readonly ulong[] U64 = new ulong[64];
        public readonly sbyte[] I8 = new sbyte[64];
        public readonly short[] I16 = new short[64];
        public readonly int[] I32 = new int[64];
        public readonly long[] I64 = new long[64];
        public readonly float[] F32 = new float[64];
        public readonly double[] F64 = new double[64];
    }

    /// <summary>
    /// §6.6.4's encode half. Every write path runs once on a throw-away encoder to
    /// settle the JIT, then a freshly constructed one is measured: from the moment
    /// construction returns, a complete encode must allocate exactly nothing.
    /// </summary>
    [Fact]
    public void AConstructedEncoderAllocatesNothingForAWholeMessage()
    {
        var e = new Encodables();
        var sink = new CountingSink();
        FlushSink write = sink.Write;                     // the delegate is setup, not encode

        // Warm-up on a SEPARATE instance: a one-time per-stream allocation on this
        // one must not be charged to - or hidden from - the measured instance.
        CompleteEncode(new OStream(new byte[64], 0, write), e);
        var buffer = new byte[64];

        AssertAllocatesNothing(() => new OStream(buffer, 0, write), os => CompleteEncode(os, e));

        Assert.True(sink.Bytes > 1000);                   // the encode really ran
    }

    /// <summary>
    /// The same, one-shot: a buffer large enough for the whole message and no sink,
    /// so no flush path runs at all.
    /// </summary>
    [Fact]
    public void AOneShotEncoderAllocatesNothingForAWholeMessage()
    {
        var e = new Encodables();
        var buffer = new byte[1 << 16];

        CompleteEncode(new OStream(new byte[1 << 16]), e);

        long used = 0;
        AssertAllocatesNothing(
            () => new OStream(buffer),
            os =>
            {
                CompleteEncode(os, e);
                used = os.BytesUsed;
            });

        Assert.True(used > 1000);
    }

    /// <summary>
    /// §6.6.4's decode half, in one <c>Feed</c> of the whole message.
    /// </summary>
    [Fact]
    public void AConstructedDecoderAllocatesNothingForAWholeMessage()
    {
        byte[] wire = WholeMessage();
        var visitor = new Checksum();

        new IStream().Feed(wire, visitor);                // warm-up, separate instance

        DecodeStatus status = DecodeStatus.Incomplete;
        AssertAllocatesNothing(() => new IStream(), istream => status = istream.Feed(wire, visitor));

        Assert.Equal(DecodeStatus.Complete, status);
    }

    /// <summary>
    /// And byte at a time - the chunk-straddling paths (a split varint, a split
    /// scalar, a payload delivered in one-byte pieces) are where a private
    /// reassembly accumulator would show up (§6.6.2).
    /// </summary>
    [Fact]
    public void AConstructedDecoderAllocatesNothingByteAtATime()
    {
        byte[] wire = WholeMessage();
        var visitor = new Checksum();

        FeedByteAtATime(new IStream(), wire, visitor);    // warm-up, separate instance

        DecodeStatus status = DecodeStatus.Incomplete;
        AssertAllocatesNothing(
            () => new IStream(),
            istream => status = FeedByteAtATime(istream, wire, visitor));

        Assert.Equal(DecodeStatus.Complete, status);
    }

    private static DecodeStatus FeedByteAtATime(IStream istream, byte[] wire, IVisitor visitor)
    {
        DecodeStatus status = DecodeStatus.Incomplete;
        for (int i = 0; i < wire.Length; i++)
        {
            status = istream.Feed(wire, i, 1, visitor);
        }
        return status;
    }

    /// <summary>The wire image a <see cref="CompleteEncode"/> produces.</summary>
    private static byte[] WholeMessage()
    {
        var buffer = new byte[1 << 16];
        var os = new OStream(buffer);
        CompleteEncode(os, new Encodables());
        return buffer[..os.BytesUsed];
    }
}
