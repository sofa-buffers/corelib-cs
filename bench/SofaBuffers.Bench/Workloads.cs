/*
 * SofaBuffers C# - the BENCH_SPEC workload set, defined once.
 *
 * BENCH_SPEC is a cross-language contract: the same messages, built from the
 * same literal values, driven the same way, so the numbers from every port are
 * directly comparable. That only holds if a workload is defined in exactly one
 * place -- Bench (throughput) and Callgrind (instructions/op) must measure the
 * same code, or their two tables describe two different libraries.
 *
 * So the datasets and the one-operation bodies live here, in BENCH_SPEC's own
 * order, and the tools are thin drivers over Workloads.All(): Bench times every
 * row, Callgrind repeats one row by name. The row labels the harness parses are
 * part of a workload's definition too, so a renamed row cannot get out of step
 * with the code that produced it.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Text;

namespace SofaBuffers.Bench;

internal static class Workloads
{
    /// <summary>Elements in the <c>u64 array (1000)</c> dataset.</summary>
    internal const int N = 1000;

    /// <summary>
    /// The one magic number in BENCH_SPEC's datasets: the <c>u64</c> array holds
    /// <c>i * GOLDEN</c> and the blob payload its low byte, so both derive from
    /// the same constant in every port.
    /// </summary>
    internal const ulong Golden = 0x9E37_79B9_7F4A_7C15UL;

    /// <summary><c>blob 1MB</c> payload length, so MB/s reads against <c>MB = 1e6</c>.</summary>
    internal const int BlobLength = 1_000_000;

    /// <summary>
    /// Encoded size of the <c>blob 1MB</c> message: a one-byte header
    /// <c>(1 &lt;&lt; 3) | 2</c>, a four-byte <c>fixlen_word</c>
    /// <c>(1000000 &lt;&lt; 3) | 3</c> and the payload. A cross-port parity check,
    /// as the perf message's 170 is.
    /// </summary>
    internal const int BlobEncoded = BlobLength + 5;

    /// <summary>
    /// Buffer size for the streaming <c>blob 1MB</c> rows -- fixed by BENCH_SPEC
    /// at 4096 rather than taken from this port's own sizing, so the rows stay
    /// comparable across languages. <c>MIN_OUTPUT_BUFFER</c> does not enter into
    /// it: it is at most 20, so 4096 always satisfies it.
    /// </summary>
    internal const int StreamBuffer = 4096;

    /// <summary>
    /// One cycle of the composite string field: <c>a</c>, <c>ä</c>, <c>€</c> and
    /// U+1D11E -- 1-, 2-, 3- and 4-byte UTF-8, ten bytes in all. Written as
    /// escapes so the bytes cannot depend on how a tool re-encodes this file.
    /// </summary>
    internal const string CompositeText = "a\u00E4\u20AC\uD834\uDD1E";

    /// <summary>Repetitions of <see cref="CompositeText"/>, giving 320 UTF-8 bytes.</summary>
    internal const int CompositeRepeats = 32;

    /// <summary>Elements in the composite message's wrapper array.</summary>
    internal const int CompositeItems = 64;

    /// <summary>The <c>typical</c> message's u16 array, hoisted out of the op.</summary>
    private static readonly ushort[] TypicalArray = { 10, 20, 30, 40 };

    /// <summary>
    /// One measurable workload.
    /// </summary>
    /// <param name="Name">the key <c>bench/run_callgrind.sh</c> drives it by</param>
    /// <param name="Label">the row label BENCH_SPEC's output grammar prescribes</param>
    /// <param name="Bytes">encoded size of the message, the row's MB/s numerator</param>
    /// <param name="Body">exactly one operation; its result feeds a blackhole</param>
    internal sealed record Workload(string Name, string Label, int Bytes, Func<long> Body);

    /// <summary>Decode sink that folds every value into a checksum (defeats elision).</summary>
    internal sealed class Checksum : IVisitor
    {
        internal long Acc;

        public void Unsigned(int id, ulong v) { Acc += (long)v ^ id; }

        public void Signed(int id, long v) { Acc += v ^ id; }

        public void Fp32(int id, float v) { Acc += BitConverter.SingleToInt32Bits(v); }

        public void Fp64(int id, double v) { Acc += BitConverter.DoubleToInt64Bits(v); }

        public void String(int id, int total, int offset, byte[] d, int o, int l) { Acc += l; }

        public void Blob(int id, int total, int offset, byte[] d, int o, int l) { Acc += l; }

        public void ArrayBegin(int id, ArrayKind kind, int count) { /* no-op */ }
    }

    /// <summary>
    /// Sink for the streaming <c>blob 1MB</c> row. BENCH_SPEC is explicit that it
    /// <b>consumes and discards</b>: accumulating the bytes would charge the
    /// streaming row a copy the one-shot row never pays, and I/O is not
    /// deterministic under Callgrind. Folding one byte per call is the minimum
    /// that keeps the call from being optimised away. It never calls
    /// <see cref="OStream.BufferSet"/>, so it is a <i>copying</i> sink and the
    /// encoder resumes in the same buffer (CORELIB_PLAN §5.1) -- which is what
    /// the row measures.
    /// </summary>
    internal sealed class Discard
    {
        internal byte Acc;

        internal void Flush(byte[] data, int offset, int length)
        {
            if (length > 0)
            {
                Acc ^= data[offset];
            }
        }
    }

    /// <summary>
    /// <c>decode: composite skip-all</c>: a visitor that overrides nothing. In a
    /// push port that is what "materialize nothing" means -- the decoder still
    /// walks every header, count and payload length, but no value reaches a
    /// destination. Its distance from <c>decode: composite</c> is what
    /// not-decoding is worth.
    /// </summary>
    private sealed class SkipAll : IVisitor
    {
    }

    internal static ulong[] MakeU64Array()
    {
        var a = new ulong[N];
        for (int i = 0; i < N; i++)
        {
            a[i] = (ulong)i * Golden;
        }
        return a;
    }

    /// <summary><c>b[i] = (i * GOLDEN) &amp; 0xFF</c>, exactly 1,000,000 bytes.</summary>
    internal static byte[] MakeBlob()
    {
        var b = new byte[BlobLength];
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = (byte)((ulong)i * Golden);
        }
        return b;
    }

    /// <summary><c>"item-0" .. "item-63"</c>, the composite wrapper array's elements.</summary>
    internal static string[] MakeItems()
    {
        var items = new string[CompositeItems];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = "item-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return items;
    }

    /// <summary><see cref="CompositeText"/> repeated <see cref="CompositeRepeats"/> times.</summary>
    internal static string MakeText()
    {
        var sb = new StringBuilder(CompositeText.Length * CompositeRepeats);
        for (int i = 0; i < CompositeRepeats; i++)
        {
            sb.Append(CompositeText);
        }
        return sb.ToString();
    }

    /// <summary>A small mixed message: scalars, a float, a short string, an array, a sequence.</summary>
    internal static void EncodeTypical(OStream os)
    {
        os.WriteUnsigned(1, 0xDEAD_BEEFUL);
        os.WriteSigned(2, -12345);
        os.WriteBoolean(3, true);
        os.WriteFp32(4, 3.14159f);
        os.WriteString(5, "sofab");
        os.WriteArrayUnsigned(6, TypicalArray);
        os.WriteSequenceBeginLazy(7);
        os.WriteUnsigned(1, 99);
        os.WriteSigned(2, -7);
        os.WriteSequenceEnd();
    }

    /// <summary>
    /// The <c>composite</c> message: every encoder path the flat datasets miss.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>id 1 -- the suite's only <b>wrapper array</b>
    /// (MESSAGE_SPEC §5.1): one field header per element, element id = array
    /// index, so ids 0..15 take a one-byte header and 16..63 a two-byte
    /// one.</description></item>
    /// <item><description>id 2 -- 320 UTF-8 bytes covering 1-, 2-, 3- and 4-byte
    /// sequences, so the §6.4 validator runs on something that is not ASCII (and,
    /// in a UTF-16 runtime such as this one, on a surrogate pair).</description></item>
    /// <item><description>id 3 -- nesting at depth 3, so the lazy hold-back run
    /// grows past the single level <c>typical</c> and <c>perf</c> reach.</description></item>
    /// <item><description>id 4 -- a struct equal to its declared default: every
    /// child is then equal to its own default and omitted, so the sequence never
    /// receives content and <see cref="OStream.WriteSequenceEnd"/> discards the
    /// held-back frame (MESSAGE_SPEC §2). The one field in the suite the encoder
    /// must <i>not</i> write.</description></item>
    /// <item><description>id 130 -- the suite's only two-byte field header,
    /// <c>(130 &lt;&lt; 3) | 0</c>.</description></item>
    /// </list>
    /// </remarks>
    internal static void EncodeComposite(OStream os, string[] items, string text)
    {
        os.WriteSequenceBeginLazy(1);
        for (int i = 0; i < items.Length; i++)
        {
            os.WriteString(i, items[i]);
        }
        os.WriteSequenceEnd();

        os.WriteString(2, text);

        os.WriteSequenceBeginLazy(3);
        os.WriteSequenceBeginLazy(1);
        os.WriteSequenceBeginLazy(1);
        os.WriteUnsigned(1, 7);
        os.WriteSequenceEnd();
        os.WriteSequenceEnd();
        os.WriteSigned(2, -1);
        os.WriteSequenceEnd();

        os.WriteSequenceBeginLazy(4);
        os.WriteSequenceEnd();

        os.WriteUnsigned(130, 0xDEAD_BEEFUL);
    }

    /// <summary>Encode once into a scratch buffer of <paramref name="room"/> bytes -&gt; the exact wire bytes.</summary>
    private static byte[] WireOf(int room, Action<OStream> what)
    {
        var buf = new byte[room];
        var os = new OStream(buf);
        what(os);
        var wire = new byte[os.BytesUsed];
        Array.Copy(buf, wire, wire.Length);
        return wire;
    }

    /// <summary>
    /// Every workload, in the order BENCH_SPEC's output grammar lists them.
    /// </summary>
    /// <remarks>
    /// All setup -- building the datasets, encoding the decode inputs and
    /// allocating the encode targets -- happens here, so an operation is the
    /// codec call and nothing else. <c>encode: blob 1MB passthrough</c> is
    /// BENCH_SPEC's one optional row and is absent: this port implements no
    /// pass-through (CORELIB_PLAN §5.1 makes it a MAY), so the row is omitted
    /// entirely rather than printed as a placeholder.
    /// </remarks>
    internal static List<Workload> All()
    {
        ulong[] src = MakeU64Array();
        byte[] blob = MakeBlob();
        string[] items = MakeItems();
        string text = MakeText();

        byte[] u64Wire = WireOf(N * 11 + 16, os => os.WriteArrayUnsigned(1, src));
        byte[] typWire = WireOf(256, EncodeTypical);
        byte[] blobWire = WireOf(BlobEncoded, os => os.WriteBlob(1, blob));
        byte[] compWire = WireOf(4096, os => EncodeComposite(os, items, text));

        // Reused encode targets: allocation belongs to the setup, not to the op.
        var encU64Out = new byte[N * 11 + 16];
        var encTypOut = new byte[256];
        var encBlobOut = new byte[BlobEncoded]; // sized by hand, per BENCH_SPEC
        var encBlobScratch = new byte[StreamBuffer];
        var encCompOut = new byte[compWire.Length];
        var discard = new Discard();
        // The delegate is built once: a method-group conversion allocates, and
        // that allocation is setup, not part of the measured operation.
        FlushSink discardSink = discard.Flush;
        var skipAll = new SkipAll();

        return new List<Workload>
        {
            new("encode_u64_array", "encode: u64 array (1000)", u64Wire.Length, () =>
            {
                var os = new OStream(encU64Out);
                os.WriteArrayUnsigned(1, src);
                return os.BytesUsed;
            }),
            new("encode_typical", "encode: typical message", typWire.Length, () =>
            {
                var os = new OStream(encTypOut);
                EncodeTypical(os);
                return os.BytesUsed;
            }),
            // The floor: one contiguous write into a buffer that holds the whole
            // message, with no sink and so no flush logic at all.
            new("encode_blob_oneshot", "encode: blob 1MB one-shot", BlobEncoded, () =>
            {
                var os = new OStream(encBlobOut);
                os.WriteBlob(1, blob);
                return os.BytesUsed;
            }),
            // The same bytes through 245 flushes of a 4096-byte buffer. The gap to
            // the row above is the divisible-run path (CORELIB_PLAN §5.1) -- the
            // only place in this suite where it runs at all.
            new("encode_blob_streaming", "encode: blob 1MB streaming", BlobEncoded, () =>
            {
                var os = new OStream(encBlobScratch, 0, discardSink);
                os.WriteBlob(1, blob);
                return os.Flush() + discard.Acc;
            }),
            new("encode_composite", "encode: composite", compWire.Length, () =>
            {
                var os = new OStream(encCompOut);
                EncodeComposite(os, items, text);
                return os.BytesUsed;
            }),
            new("decode_u64_array", "decode: u64 array (1000)", u64Wire.Length, () =>
            {
                var c = new Checksum();
                new IStream().Feed(u64Wire, c);
                return c.Acc;
            }),
            new("decode_typical", "decode: typical message", typWire.Length, () =>
            {
                var c = new Checksum();
                new IStream().Feed(typWire, c);
                return c.Acc;
            }),
            // Fed in 4096-byte chunks: the streaming decode surface, not one feed
            // of a megabyte.
            new("decode_blob", "decode: blob 1MB", blobWire.Length, () =>
            {
                var c = new Checksum();
                var istream = new IStream();
                for (int off = 0; off < blobWire.Length; off += StreamBuffer)
                {
                    istream.Feed(blobWire, off, Math.Min(StreamBuffer, blobWire.Length - off), c);
                }
                return c.Acc;
            }),
            new("decode_composite", "decode: composite", compWire.Length, () =>
            {
                var c = new Checksum();
                new IStream().Feed(compWire, c);
                return c.Acc;
            }),
            new("decode_composite_skip", "decode: composite skip-all", compWire.Length, () =>
            {
                var istream = new IStream();
                return (long)istream.Feed(compWire, skipAll);
            }),
        };
    }
}
