/*
 * SofaBuffers C# - CORELIB_PLAN §5.1: a string payload is a *divisible* run, so
 * WriteString must stream it through whatever room the active buffer has left
 * instead of materializing the transcoded payload (issue #60).
 *
 * §5.1 is normative that "the output buffer may be arbitrarily smaller than the
 * message: what bounds memory is the buffer, not the message". A `string` is the
 * one payload that has to be transcoded, and transcoding it into a temporary
 * byte[] would put the *message* back in charge of peak memory: a 64 MB string
 * through a 16-byte sink buffer would allocate 64 MB. These tests pin both
 * halves - the bytes are identical to the one-shot encoding down to a one-byte
 * buffer (MIN_OUTPUT_BUFFER), and producing them costs no payload-sized
 * allocation, on the sink path and on the sinkless BufferFull path alike.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.IO;
using System.Text;
using Xunit;

namespace SofaBuffers.Tests;

public class StringStreamingAllocationTests
{
    /// <summary>A sink that only counts, so measuring an encode measures the encoder.</summary>
    private sealed class CountingSink
    {
        public long Bytes;

        public void Write(byte[] data, int offset, int length) => Bytes += length;
    }

    /// <summary>
    /// A text with every UTF-8 width in it (1/2/3/4 bytes, the last as a
    /// surrogate pair), repeated until it is comfortably larger than any buffer
    /// under test.
    /// </summary>
    private static string BigMixedText(int repeats)
    {
        const string unit = "abc éß €中 \U0001F600\U00010348 ";
        var sb = new StringBuilder(unit.Length * repeats);
        for (int i = 0; i < repeats; i++)
        {
            sb.Append(unit);
        }
        return sb.ToString();
    }

    private static byte[] OneShot(int id, string text)
    {
        var buf = new byte[Encoding.UTF8.GetByteCount(text) + 32];
        var os = new OStream(buf);
        os.WriteString(id, text);
        return buf[..os.BytesUsed];
    }

    private static byte[] Streamed(int id, string text, int bufferSize)
    {
        var produced = new MemoryStream();
        var os = new OStream(new byte[bufferSize], 0, produced.Write);
        os.WriteString(id, text);
        os.Flush();
        return produced.ToArray();
    }

    /// <summary>
    /// The core regression: a payload far larger than the buffer must not cost a
    /// payload-sized allocation. On the pre-fix encoder this allocated the whole
    /// transcoded string (~1.7 MB here) on every call.
    /// </summary>
    [Fact]
    public void LargeStringThroughASmallBufferDoesNotAllocateThePayload()
    {
        string text = BigMixedText(50_000);          // ~1.7 MB of UTF-8
        long payload = Encoding.UTF8.GetByteCount(text);
        Assert.True(payload > 1_000_000);

        var sink = new CountingSink();
        FlushSink write = sink.Write;

        // Warm up the JIT on a SEPARATE encoder. Warming up on the instance that is
        // about to be measured would perform any one-time per-stream allocation
        // inside the warm-up window and hide it (CORELIB_PLAN §6.6.4) - which is
        // what this test used to do, and why it read `< 64 KiB` instead of zero.
        var warm = new OStream(new byte[16], 0, write);
        warm.WriteString(7, BigMixedText(4));
        warm.Flush();
        sink.Bytes = 0;

        var os = new OStream(new byte[16], 0, write);
        long before = GC.GetAllocatedBytesForCurrentThread();
        os.WriteString(7, text);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        os.Flush();

        Assert.Equal(0, allocated);
        // Everything reached the sink: the payload plus the id byte and the
        // fixlen_word varint in front of it.
        Assert.InRange(sink.Bytes - payload, 2, 16);
    }

    /// <summary>
    /// Sinkless, the payload cannot be written at all: the buffer fills and the
    /// call reports <see cref="SofabError.BufferFull"/>. Transcoding the whole
    /// string first would be pure waste - a megabyte allocated to then throw.
    /// </summary>
    [Fact]
    public void SinklessBufferFullDoesNotAllocateThePayload()
    {
        string text = BigMixedText(50_000);

        // Warm-up on a separate encoder, for the reason given above; the throw
        // itself allocates the exception object, so the measured window here can
        // only be bounded, not zero.
        var warm = new OStream(new byte[64]);
        warm.WriteUnsigned(1, 1);
        Assert.Throws<SofabException>(() => warm.WriteString(7, text));

        var os = new OStream(new byte[64]);
        os.WriteUnsigned(1, 1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<SofabException>(() => os.WriteString(7, text));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(SofabError.BufferFull, ex.Error);
        Assert.True(
            allocated < 4 * 1024,
            $"a rejected WriteString allocated {allocated} bytes before reporting BufferFull");
    }

    /// <summary>
    /// Correctness half: the streamed bytes equal the one-shot bytes for every
    /// buffer size from <c>MIN_OUTPUT_BUFFER</c> upwards, including the sizes
    /// that cannot hold a single 4-byte rune, so a UTF-8 sequence - and the
    /// surrogate pair behind it - is split across a flush.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(4096)]
    public void StreamedBytesMatchTheOneShotEncoding(int bufferSize)
    {
        string text = BigMixedText(400);
        Assert.Equal(OneShot(7, text), Streamed(7, text, bufferSize));
    }

    /// <summary>
    /// The split is exercised at every offset of a 4-byte rune: a buffer of n
    /// bytes preceded by k filler bytes puts the astral character's boundary at
    /// a different position in each case, and the transcoder must carry the
    /// half-consumed surrogate pair across the flush.
    /// </summary>
    [Fact]
    public void SurrogatePairSplitsAtEveryFlushOffset()
    {
        for (int fill = 0; fill < 8; fill++)
        {
            for (int bufferSize = 1; bufferSize <= 8; bufferSize++)
            {
                string text = new string('.', fill) + "\U0001F600\U0001F601\U0001F602" + "éx";

                var one = new byte[256];
                var direct = new OStream(one);
                direct.WriteString(3, text);

                var produced = new MemoryStream();
                var os = new OStream(new byte[bufferSize], 0, produced.Write);
                os.WriteString(3, text);
                os.Flush();

                Assert.Equal(one[..direct.BytesUsed], produced.ToArray());
            }
        }
    }

    /// <summary>
    /// A string that fits the remaining buffer exactly still takes the in-place
    /// path, and one byte less still produces identical bytes - the boundary
    /// between the two code paths must not be observable on the wire.
    /// </summary>
    [Fact]
    public void TheFitBoundaryIsNotObservable()
    {
        string text = "é€\U0001F600 tail";                 // 2+3+4+5 = 14 bytes
        int payload = Encoding.UTF8.GetByteCount(text);
        byte[] expected = OneShot(7, text);

        for (int slack = -3; slack <= 3; slack++)
        {
            int size = payload + 2 + slack;
            if (size < 1)
            {
                continue;
            }
            Assert.Equal(expected, Streamed(7, text, size));
        }
    }

    /// <summary>
    /// An unpaired surrogate is refused before anything is written, whatever the
    /// buffer position - the strict-UTF-8 refusal (§6.4) must not become
    /// non-atomic because the value happened to land on the streaming path.
    /// </summary>
    [Fact]
    public void UnpairedSurrogateIsStillRefusedAtomicallyOnTheStreamingPath()
    {
        var produced = new MemoryStream();
        var os = new OStream(new byte[4], 0, produced.Write);
        string bad = new string('x', 200) + "\ud800" + new string('y', 200);

        var ex = Assert.Throws<SofabException>(() => os.WriteString(1, bad));
        Assert.Equal(SofabError.Argument, ex.Error);
        Assert.Equal(0, os.BytesUsed);
        Assert.Empty(produced.ToArray());

        // The stream is still usable and writes the next field from the start.
        os.WriteString(1, "ok");
        os.Flush();
        Assert.Equal(OneShot(1, "ok"), produced.ToArray());
    }
}
