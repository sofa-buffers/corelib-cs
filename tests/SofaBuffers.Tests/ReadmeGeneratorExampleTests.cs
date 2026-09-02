/*
 * SofaBuffers C# - the README's generated-object example (issue #62).
 *
 * CORELIB_PLAN §6.1.1 closes the generated surface to `encode` / `decode` /
 * `try_decode` / `serialize` / `deserialize` / `decoder`, casing adapted per
 * language and *nothing else*: `marshal`, `unmarshal`, `to_bytes`, `from_bytes`,
 * `serialize_to`, `decode_from` and `decode_into` are named there as spellings a
 * port MUST NOT invent. §9.5 additionally requires the README's Generator
 * example to show the one-shot pair *and* the streaming `serialize` / `decoder()`
 * path, since the streaming half is what the corelib exists for.
 *
 * Two kinds of test, because the defect had two halves:
 *   - a lint over README.md: no name outside the closed set, and the generator
 *     section actually spells the streaming leg out;
 *   - a behaviour test over a stand-in that mirrors the README's sample, so the
 *     documented code is executed rather than merely read - one-shot, then the
 *     same object streamed out through a 1-byte buffer and back in one byte at a
 *     time, both byte-for-byte identical to the one-shot result.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class ReadmeGeneratorExampleTests
{
    // ---------------------------------------------------------------- README

    /// <summary>Path of the repository README, resolved from this file's location.</summary>
    private static string ReadmePath([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "README.md"));

    /// <summary>
    /// The README text, or <c>null</c> when this suite was built from a source
    /// drop rather than the repo (a packaged replay has no README to lint, so the
    /// guard stands down instead of failing).
    /// </summary>
    private static string? Readme()
    {
        string path = ReadmePath();
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>
    /// Every spelling §6.1.1 rules out, in the casings a C# port could plausibly
    /// reach for. Matched case-insensitively on a word boundary, so prose and
    /// code are both covered.
    /// </summary>
    private static readonly string[] ForbiddenNames =
    {
        "marshal", "unmarshal",
        "to_bytes", "from_bytes", "toBytes", "fromBytes",
        "serialize_to", "serializeTo",
        "decode_from", "decodeFrom", "decode_into", "decodeInto",
    };

    [Fact]
    public void ReadmeNamesNothingOutsideTheClosedGeneratedSurface()
    {
        string? readme = Readme();
        if (readme is null)
        {
            return;
        }

        var offenders = new List<string>();
        foreach (string name in ForbiddenNames)
        {
            foreach (Match m in Regex.Matches(readme, @"\b" + name + @"\b", RegexOptions.IgnoreCase))
            {
                int line = 1;
                for (int i = 0; i < m.Index; i++)
                {
                    if (readme[i] == '\n')
                    {
                        line++;
                    }
                }
                offenders.Add("README.md:" + line + ": " + m.Value);
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>The body of the README's <c>### Code generator</c> section.</summary>
    private static string GeneratorSection(string readme)
    {
        int start = readme.IndexOf("### Code generator", StringComparison.Ordinal);
        Assert.True(start >= 0, "README.md has no '### Code generator' section (§9.5)");
        int end = readme.IndexOf("\n## ", start, StringComparison.Ordinal);
        return end < 0 ? readme.Substring(start) : readme.Substring(start, end - start);
    }

    [Fact]
    public void GeneratorSectionShowsBothTheOneShotAndTheStreamingPath()
    {
        string? readme = Readme();
        if (readme is null)
        {
            return;
        }
        string section = GeneratorSection(readme);

        // The one-shot pair (§6.1.1), and the fallible form the backend emits.
        Assert.Contains("Encode()", section, StringComparison.Ordinal);
        Assert.Contains("Decode(", section, StringComparison.Ordinal);
        Assert.Contains("TryDecode(", section, StringComparison.Ordinal);

        // The streaming leg §9.5 asks for: serialize(ostream) driven over a sink,
        // and decoder() fed in chunks.
        Assert.Contains("Serialize(OStream", section, StringComparison.Ordinal);
        Assert.Contains("FlushSink", section, StringComparison.Ordinal);
        Assert.Contains("Decoder()", section, StringComparison.Ordinal);
        Assert.Contains(".Feed(", section, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- the sample, run

    /// <summary>
    /// The README's generated stand-in, kept in sync with the sample there and
    /// with what <c>sofabgen --lang csharp</c> emits: <c>Serialize</c> writes the
    /// fields, <c>Encode</c>/<c>Decode</c>/<c>TryDecode</c> are the one-shot
    /// wrappers over it, and the nested <c>Decoder</c> feeds chunks of any size.
    /// </summary>
    private sealed class Point
    {
        public long X, Y;
        public const int MaxSize = 32;

        public void Serialize(OStream os)
        {
            os.WriteSigned(1, X);
            os.WriteSigned(2, Y);
        }

        public byte[] Encode()
        {
            var buf = new byte[MaxSize];
            var os = new OStream(buf);
            Serialize(os);
            var outp = new byte[os.BytesUsed];
            Array.Copy(buf, outp, os.BytesUsed);
            return outp;
        }

        public static Point Decode(byte[] data)
        {
            var m = new Point();
            new IStream().Feed(data, 0, data.Length, new Visitor(m));
            return m;
        }

        public static DecodeStatus TryDecode(byte[] data, out Point msg)
        {
            msg = new Point();
            return new IStream().Feed(data, 0, data.Length, new Visitor(msg));
        }

        private sealed class Visitor : IVisitor
        {
            private readonly Point _m;
            public Visitor(Point m) => _m = m;

            public void Signed(int id, long v)
            {
                switch (id)
                {
                    case 1: _m.X = v; break;
                    case 2: _m.Y = v; break;
                }
            }
        }

        public sealed class Decoder
        {
            private readonly Point _m = new Point();
            private readonly IStream _is = new IStream();
            private readonly Visitor _v;

            public Decoder() => _v = new Visitor(_m);

            public DecodeStatus Feed(byte[] chunk, int off, int len) => _is.Feed(chunk, off, len, _v);

            public Point Message => _m;
        }
    }

    /// <summary>The bytes the README claims for <c>Point { X = 3, Y = 4 }</c>.</summary>
    private static byte[] PointWire() => Bytes(0x09, 0x06, 0x11, 0x08);

    [Fact]
    public void OneShotEncodeDecodeRoundTrips()
    {
        var p = new Point { X = 3, Y = 4 };

        byte[] wire = p.Encode();
        Assert.Equal(PointWire(), wire);

        Point got = Point.Decode(wire);
        Assert.Equal(3, got.X);
        Assert.Equal(4, got.Y);

        Assert.Equal(DecodeStatus.Complete, Point.TryDecode(wire, out Point tried));
        Assert.Equal(3, tried.X);
        Assert.Equal(4, tried.Y);
    }

    /// <summary>
    /// The streaming-out half: <c>Serialize</c> over an <c>OStream</c> that owns
    /// only a one-byte scratch buffer plus a <c>FlushSink</c> emits exactly the
    /// one-shot bytes, so nothing about the wire depends on the buffer size.
    /// </summary>
    [Fact]
    public void SerializeStreamsThroughAOneByteBuffer()
    {
        var p = new Point { X = 3, Y = 4 };

        using var outStream = new MemoryStream();
        var os = new OStream(new byte[Sofab.MinOutputBuffer], 0, outStream.Write);
        p.Serialize(os);
        os.Flush();

        Assert.Equal(PointWire(), outStream.ToArray());
        Assert.Equal(p.Encode(), outStream.ToArray());
    }

    /// <summary>
    /// The streaming-in half: the generated <c>Decoder</c> fed one byte at a time
    /// assembles the same object, reporting <c>Incomplete</c> mid-field and
    /// <c>Complete</c> once the bytes end on a field boundary (§5.2).
    /// </summary>
    [Fact]
    public void DecoderAssemblesTheObjectOneByteAtATime()
    {
        byte[] wire = new Point { X = 3, Y = 4 }.Encode();

        var dec = new Point.Decoder();
        DecodeStatus st = DecodeStatus.Complete;
        for (int i = 0; i < wire.Length; i++)
        {
            st = dec.Feed(wire, i, 1);
            // A header byte alone leaves the decoder inside a field.
            Assert.Equal(i % 2 == 0 ? DecodeStatus.Incomplete : DecodeStatus.Complete, st);
        }

        Assert.Equal(DecodeStatus.Complete, st);
        Assert.Equal(3, dec.Message.X);
        Assert.Equal(4, dec.Message.Y);
    }
}
