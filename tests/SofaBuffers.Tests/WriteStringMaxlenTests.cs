/*
 * SofaBuffers C# - OStream.WriteString(id, text, maxlen): the bounded string
 * writer generated code calls for a field whose schema declares a `maxlen`.
 *
 * The bound is the caller's (generated code passes the schema literal; this
 * library holds none, CORELIB_PLAN §6.2.1). It is measured in UTF-8 BYTES, and a
 * value over it is refused with SofabError.Argument before any byte is written --
 * on the ASCII fast path, on the transcoding path, and on the buffer-spanning
 * path alike -- exactly like the invalid-UTF-8 refusal of §6.4.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.IO;
using System.Text;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class WriteStringMaxlenTests
{
    private static void AssertRefused(string text, int maxlen)
    {
        var os = new OStream(new byte[4096]);
        var ex = Assert.Throws<SofabException>(() => os.WriteString(3, text, maxlen));
        Assert.Equal(SofabError.Argument, ex.Error);
        Assert.Equal(0, os.BytesUsed);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("xxxx", 4)]                // ASCII fast path, exactly at the bound
    [InlineData("éé", 4)]        // 2 chars, 4 UTF-8 bytes: transcoding path
    [InlineData("\U0001F600", 4)]          // surrogate pair: 2 chars, 4 bytes
    [InlineData("ab", 100)]
    public void ValueAtOrUnderTheBoundEncodesLikeTheUnboundedOverload(string text, int maxlen)
    {
        Assert.Equal(
            Encode(os => os.WriteString(3, text)),
            Encode(os => os.WriteString(3, text, maxlen)));
    }

    [Fact]
    public void AsciiOverByOneIsRefused() =>
        AssertRefused("xxxxx", 4); // ASCII fast path: 5 bytes > 4

    [Fact]
    public void AsciiFarOverIsRefusedWithoutMeasuring() =>
        AssertRefused(new string('x', 600), 4); // text.Length alone is over the bound

    [Fact]
    public void MultiByteOverIsRefusedInTheMeasuringPass()
    {
        // 4 chars (not over by length) but 5 UTF-8 bytes: only the measured
        // byte count can refuse it, and a cut at 4 would land inside the é.
        AssertRefused("xxxé", 4); // 5 UTF-8 bytes > 4
        // 40 chars, 80 bytes against 60: the transcoding path's count.
        AssertRefused(new string('é', 40), 60); // 80 UTF-8 bytes > 60
    }

    [Fact]
    public void SurrogatePairCountsFourBytes() =>
        AssertRefused("\U0001F600", 3); // a supplementary character is 4 UTF-8 bytes

    [Fact]
    public void LongNonAsciiAtTheBoundEncodes()
    {
        // Past the ASCII fast path's length limit, so the transcoding path measures.
        string text = new string('é', 200);
        Assert.Equal(
            Encode(1024, os => os.WriteString(3, text)),
            Encode(1024, os => os.WriteString(3, text, 400)));
        AssertRefused(text, 399); // 400 bytes > 399
    }

    [Fact]
    public void NegativeMaxlenIsAnArgumentError()
    {
        var os = new OStream(new byte[16]);
        var ex = Assert.Throws<SofabException>(() => os.WriteString(0, "", -1));
        Assert.Equal(SofabError.Argument, ex.Error);
        Assert.Contains("maxlen -1", ex.Message);
        Assert.Equal(0, os.BytesUsed);
    }

    [Fact]
    public void RefusalMessageNamesBothLengths()
    {
        var os = new OStream(new byte[16]);
        var ex = Assert.Throws<SofabException>(() => os.WriteString(0, "xxxé", 4));
        Assert.Contains("5", ex.Message);
        Assert.Contains("maxlen 4", ex.Message);
    }

    [Fact]
    public void InvalidUtf8IsStillRefusedUnderTheBound()
    {
        var os = new OStream(new byte[16]);
        var ex = Assert.Throws<SofabException>(() => os.WriteString(0, "a\ud800", 10));
        Assert.Equal(SofabError.Argument, ex.Error);
        Assert.Contains("invalid UTF-8", ex.Message);
        Assert.Equal(0, os.BytesUsed);
    }

    [Fact]
    public void RefusalDoesNotCommitAHeldBackSequenceHeader()
    {
        // The refusal is atomic: a lazily opened frame stays held back, so the
        // empty sequence still vanishes when it is closed.
        var buf = new byte[64];
        var os = new OStream(buf);
        os.WriteSequenceBeginLazy(5);
        Assert.Throws<SofabException>(() => os.WriteString(0, "xxxxx", 4));
        Assert.Throws<SofabException>(() => os.WriteString(0, "xxxé", 4));
        os.WriteSequenceEnd();
        Assert.Equal(0, os.BytesUsed);
    }

    [Fact]
    public void RefusalOnTheBufferSpanningPathFlushesNothing()
    {
        // A two-byte buffer with a sink takes the transcoding path for any value;
        // the bound is checked before the header, so nothing reaches the sink.
        var produced = new MemoryStream();
        var os = new OStream(new byte[2], 0, (d, o, l) => produced.Write(d, o, l));
        Assert.Throws<SofabException>(() => os.WriteString(1, "hello world", 10));
        os.Flush();
        Assert.Equal(0, produced.Length);

        // ...and a value at the bound streams through the same small buffer.
        os.WriteString(1, "helloé", 7);
        os.Flush();
        byte[] want = Encode(o => o.WriteString(1, "helloé"));
        Assert.Equal(want, produced.ToArray());
        Assert.Equal(7, Encoding.UTF8.GetByteCount("helloé"));
    }
}
