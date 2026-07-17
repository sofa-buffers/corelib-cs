/*
 * SofaBuffers C# - strict UTF-8 tests (issue #85).
 *
 * C# `string` is a Unicode type, so SofaBuffers is ALWAYS strict here: there is
 * no SOFAB_STRICT_UTF8 knob to toggle (CORELIB_PLAN §6.4 — "Unicode-string
 * targets are always strict"). The corelib's contribution is the ENCODE side:
 * OStream.WriteString refuses a `string` that cannot be encoded as valid UTF-8
 * (a value carrying an unpaired surrogate) with SofabError.Argument instead of
 * the default Encoding.UTF8 behaviour of silently substituting U+FFFD, which
 * MESSAGE_SPEC §8 forbids ("no silent replacement, ever").
 *
 * The DECODE side is unchanged: the corelib hands raw string bytes to the
 * visitor without transcoding, and the strict-decode INVALID outcome is produced
 * by GENERATED code that materializes the string with a strict/fatal decoder.
 * The direct-strict-decode tests below model exactly that generated step.
 *
 * These tests also replay the shared, language-agnostic negative vectors
 * (assets/test_vectors.json -> top-level "invalid_utf8"; tracks corelib-c-cpp#97).
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SofaBuffers.Tests;

public class StrictUtf8Tests
{
    /// <summary>The strict/fatal UTF-8 decoder generated code uses to materialize a string.</summary>
    private static readonly UTF8Encoding StrictDecode =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>LEB128 varint, low 7 bits first — mirrors the encoder's framing.</summary>
    private static IEnumerable<byte> Varint(ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            yield return b;
        }
        while (value != 0);
    }

    private static byte[] Encode(Action<OStream> body)
    {
        var buf = new byte[256];
        var os = new OStream(buf);
        body(os);
        var outp = new byte[os.BytesUsed];
        Array.Copy(buf, outp, os.BytesUsed);
        return outp;
    }

    // --- unit tests: encode side -------------------------------------------

    [Fact]
    public void UnpairedHighSurrogateRejected()
    {
        // "\ud800" is a lone UTF-16 high surrogate: no valid UTF-8 encoding.
        var os = new OStream(new byte[32]);
        var ex = Assert.Throws<SofabException>(() => os.WriteString(1, "\ud800"));
        Assert.Equal(SofabError.Argument, ex.Error);
        // Nothing must have been written to the stream (rejected before the header).
        Assert.Equal(0, os.BytesUsed);
    }

    [Fact]
    public void UnpairedLowSurrogateRejected()
    {
        var os = new OStream(new byte[32]);
        var ex = Assert.Throws<SofabException>(() => os.WriteString(1, "abc\udfffxyz"));
        Assert.Equal(SofabError.Argument, ex.Error);
        Assert.Equal(0, os.BytesUsed);
    }

    [Fact]
    public void SurrogateRejectedOnBufferSpanningPath()
    {
        // A tiny buffer + flush sink forces the buffer-spanning branch of
        // WriteString; the strict check must still reject before emitting bytes.
        var produced = new MemoryStream();
        var os = new OStream(new byte[2], 0, (d, o, l) => produced.Write(d, o, l));
        var ex = Assert.Throws<SofabException>(() => os.WriteString(1, "hello world \ud834"));
        Assert.Equal(SofabError.Argument, ex.Error);
    }

    [Fact]
    public void ValidStringsEncodeByteIdentically()
    {
        // The strict codec must produce exactly the same bytes as the previous
        // (default Encoding.UTF8) encoder for every valid string.
        foreach (string s in new[]
        {
            "",
            "hello",
            "grüße",              // 2-byte sequences
            "日本語",              // 3-byte sequences
            "😀🎉",               // 4-byte sequences (well-formed surrogate pairs)
            "mixed: aé中\U0001F600!",
        })
        {
            // Ground truth: id-1 T_FIXLEN header, (len<<3)|string-subtype length
            // word, then the DEFAULT Encoding.UTF8 bytes for the valid string.
            byte[] reference = Encoding.UTF8.GetBytes(s);
            var expected = new List<byte> { 0x0A }; // id 1, T_FIXLEN
            expected.AddRange(Varint(((ulong)reference.Length << 3) | 0x02));
            expected.AddRange(reference);

            Assert.Equal(expected.ToArray(), Encode(os => os.WriteString(1, s)));
        }
    }

    [Fact]
    public void EmbeddedNulRoundTrips()
    {
        // U+0000 is valid UTF-8 (a single 0x00 byte) and the wire is length-framed,
        // so an embedded NUL must encode verbatim and round-trip (MESSAGE_SPEC §8).
        string s = "a\0b\0c";
        byte[] wire = Encode(os => os.WriteString(7, s));

        var visitor = new StringVisitor();
        new IStream().Feed(wire, visitor);

        Assert.Single(visitor.Strings);
        (int id, byte[] bytes) = visitor.Strings[0];
        Assert.Equal(7, id);
        Assert.Equal(Encoding.UTF8.GetBytes(s), bytes);
        // Generated code would materialize it with the strict decoder; NUL is fine.
        Assert.Equal(s, StrictDecode.GetString(bytes));
    }

    // --- shared negative vectors (invalid_utf8) ----------------------------

    private sealed record InvalidVec(string Name, byte[] StringHex, byte[] SerializedHex);

    private static readonly List<InvalidVec> InvalidUtf8Vectors = LoadInvalidUtf8();

    private static List<InvalidVec> LoadInvalidUtf8()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "test_vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var list = new List<InvalidVec>();
        if (doc.RootElement.TryGetProperty("invalid_utf8", out JsonElement arr))
        {
            foreach (JsonElement v in arr.EnumerateArray())
            {
                list.Add(new InvalidVec(
                    v.GetProperty("name").GetString()!,
                    Convert.FromHexString(v.GetProperty("string_hex").GetString()!),
                    Convert.FromHexString(v.GetProperty("serialized_hex").GetString()!)));
            }
        }
        return list;
    }

    public static IEnumerable<object[]> InvalidUtf8Names =>
        InvalidUtf8Vectors.Select(v => new object[] { v.Name });

    private static InvalidVec Vec(string name) => InvalidUtf8Vectors.First(v => v.Name == name);

    [Fact]
    public void InvalidUtf8VectorsPresent()
    {
        // Guard against a stale/positive-only vectors file: the negative group
        // (tracks corelib-c-cpp#97) must be carried in this repo's assets.
        Assert.NotEmpty(InvalidUtf8Vectors);
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8Names))]
    public void InvalidUtf8PayloadDecodeRejects(string name)
    {
        // decode_outcome:"invalid" — the raw payload bytes are not valid UTF-8, so
        // generated code materializing the string with the strict/fatal decoder
        // must reject them. The corelib itself does not transcode; this models the
        // generated decode step directly on the string_hex payload.
        InvalidVec v = Vec(name);
        Assert.ThrowsAny<DecoderFallbackException>(() => StrictDecode.GetString(v.StringHex));
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8Names))]
    public void InvalidUtf8WireDecodeRejects(string name)
    {
        // Same, driven through the whole wire message: the corelib decodes the
        // serialized_hex and hands the raw string bytes to the visitor UNCHANGED
        // (decode is unaffected by strictness); strict materialization of those
        // extracted bytes is what yields INVALID.
        InvalidVec v = Vec(name);
        var visitor = new StringVisitor();
        new IStream().Feed(v.SerializedHex, visitor);

        Assert.Single(visitor.Strings);
        byte[] extracted = visitor.Strings[0].Bytes;
        Assert.Equal(v.StringHex, extracted); // bytes passed through verbatim
        Assert.ThrowsAny<DecoderFallbackException>(() => StrictDecode.GetString(extracted));
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8Names))]
    public void InvalidUtf8EncodeRejectsWhereRepresentable(string name)
    {
        // encode_outcome:"invalid_argument". A C# `string` is UTF-16, so it can
        // only *hold* the invalid payloads that correspond to an unpaired
        // surrogate (the lone-surrogate vectors); the overlong / out-of-range /
        // stray-byte payloads are byte-container-only invalidity with no C# string
        // preimage, so encode-reject is exercised "where the corelib can". For the
        // representable ones, WriteString must refuse with SofabError.Argument.
        InvalidVec v = Vec(name);
        if (!TryAsUnpairedSurrogate(v.StringHex, out string surrogate))
        {
            return;
        }
        var os = new OStream(new byte[32]);
        var ex = Assert.Throws<SofabException>(() => os.WriteString(0, surrogate));
        Assert.Equal(SofabError.Argument, ex.Error);
        Assert.Equal(0, os.BytesUsed);
    }

    [Fact]
    public void AtLeastOneVectorDrivesEncodeReject()
    {
        // Sanity: the "where the corelib can" encode-reject path is actually
        // reachable from the shared vectors (the two lone-surrogate cases).
        Assert.Contains(InvalidUtf8Vectors, v => TryAsUnpairedSurrogate(v.StringHex, out _));
    }

    /// <summary>
    /// If <paramref name="utf8"/> is a 3-byte sequence decoding to a UTF-16
    /// surrogate code point (U+D800..U+DFFF), yield the equivalent C# string
    /// holding that single unpaired surrogate char, which .NET can represent but
    /// cannot strictly encode back to UTF-8.
    /// </summary>
    private static bool TryAsUnpairedSurrogate(byte[] utf8, out string surrogate)
    {
        surrogate = "";
        if (utf8.Length == 3 && (utf8[0] & 0xF0) == 0xE0
            && (utf8[1] & 0xC0) == 0x80 && (utf8[2] & 0xC0) == 0x80)
        {
            int cp = ((utf8[0] & 0x0F) << 12) | ((utf8[1] & 0x3F) << 6) | (utf8[2] & 0x3F);
            if (cp >= 0xD800 && cp <= 0xDFFF)
            {
                surrogate = ((char)cp).ToString();
                return true;
            }
        }
        return false;
    }

    /// <summary>Collects decoded string fields, coalescing chunks, as (id, bytes).</summary>
    private sealed class StringVisitor : IVisitor
    {
        public readonly List<(int Id, byte[] Bytes)> Strings = new();
        private readonly MemoryStream _pending = new();
        private int _id;
        private int _total;
        private bool _open;

        public void Unsigned(int id, ulong v) { }
        public void Signed(int id, long v) { }
        public void Fp32(int id, float v) { }
        public void Fp64(int id, double v) { }
        public void Blob(int id, int total, int offset, byte[] d, int o, int l) { }
        public void ArrayBegin(int id, ArrayKind kind, int count) { }
        public void SequenceBegin(int id) { }
        public void SequenceEnd() { }

        public void String(int id, int total, int offset, byte[] d, int o, int l)
        {
            if (!_open)
            {
                _open = true;
                _id = id;
                _total = total;
                _pending.SetLength(0);
            }
            _pending.Write(d, o, l);
            if (_pending.Length >= _total)
            {
                Strings.Add((_id, _pending.ToArray()));
                _open = false;
            }
        }
    }
}
