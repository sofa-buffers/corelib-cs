/*
 * SofaBuffers C# - the decode-side half of the strict UTF-8 contract:
 * sofab.Utf8.Decode (CORELIB_PLAN §6.4, MESSAGE_SPEC §7/§8).
 *
 * The encode side is covered by StrictUtf8Tests (a string C# cannot hold is
 * refused at WriteString). This file covers the other direction, which until now
 * lived only in generated code: raw wire bytes become a C# string, and bytes that
 * are not valid UTF-8 become the INVALID outcome instead of a repaired string.
 *
 * The distinction the whole helper exists for is that .NET's default UTF-8
 * decoder is LOSSY -- it substitutes U+FFFD for malformed input, which §8 forbids
 * in every mode -- so a consumer that converts first and inspects afterwards can
 * never reject anything. Every case below therefore asserts a rejection, not a
 * repaired value.
 *
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class Utf8DecodeTests
{
    [Fact]
    public void DecodesAsciiAndMultiByteText()
    {
        byte[] ascii = Encoding.UTF8.GetBytes("hello");
        Assert.Equal("hello", Utf8.Decode(ascii, 0, ascii.Length));

        // Two, three and four byte sequences in one payload.
        const string mixed = "héllo — wörld 😀";
        byte[] utf8 = Encoding.UTF8.GetBytes(mixed);
        Assert.Equal(mixed, Utf8.Decode(utf8, 0, utf8.Length));
    }

    [Fact]
    public void EmptyRangeIsTheEmptyString()
    {
        // A `string` field of length 0 is delivered as a single callback with
        // total == 0 (IVisitor.String), so this is an ordinary value, not an edge.
        Assert.Equal(string.Empty, Utf8.Decode(Bytes(0x41, 0x42), 1, 0));
        Assert.Equal(string.Empty, Utf8.Decode(Bytes(), 0, 0));
    }

    [Fact]
    public void DecodesOnlyTheNamedRange()
    {
        // The caller passes the payload's declared `total` out of a chunk that may
        // carry more than the payload -- the bytes around it are another field's,
        // and are neither decoded nor judged. The bracketing bytes here are a lone
        // continuation and a truncated lead, so a decoder reading one byte too far
        // in either direction fails this.
        byte[] data = Bytes(0x80, 0x68, 0x69, 0xE2, 0x82);
        Assert.Equal("hi", Utf8.Decode(data, 1, 2));
    }

    /// <summary>
    /// Byte sequences that are not valid UTF-8, each with the rule it breaks.
    /// Strict means the Unicode scalar encoding and nothing else, so every one of
    /// these is a rejected message rather than a string with a U+FFFD in it.
    /// </summary>
    public static TheoryData<string, byte[]> Malformed() => new()
    {
        { "bare continuation byte", Bytes(0x80) },
        { "continuation after ASCII", Bytes(0x41, 0xBF) },
        { "overlong NUL (modified UTF-8)", Bytes(0xC0, 0x80) },
        { "overlong two-byte form", Bytes(0xC1, 0xBF) },
        { "overlong three-byte form", Bytes(0xE0, 0x80, 0xAF) },
        { "overlong four-byte form", Bytes(0xF0, 0x80, 0x80, 0xAF) },
        { "surrogate U+D800", Bytes(0xED, 0xA0, 0x80) },
        { "surrogate U+DFFF", Bytes(0xED, 0xBF, 0xBF) },
        { "above U+10FFFF (F4 90)", Bytes(0xF4, 0x90, 0x80, 0x80) },
        { "above U+10FFFF (F5 lead)", Bytes(0xF5, 0x80, 0x80, 0x80) },
        { "no such lead byte", Bytes(0xFF) },
        { "truncated three-byte sequence", Bytes(0xE2, 0x82) },
        { "truncated four-byte sequence", Bytes(0xF0, 0x9F, 0x98) },
        { "lead byte followed by ASCII", Bytes(0xE2, 0x41, 0x41) },
        { "defect after a valid prefix", Bytes(0x68, 0x69, 0xED, 0xA0, 0x80) },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedBytesAreRejectedAsInvalidMessage(string rule, byte[] payload)
    {
        var e = Assert.Throws<SofabException>(() => Utf8.Decode(payload, 0, payload.Length));

        // The §5.2 INVALID outcome, and a message that names the field kind: a
        // caller catching this has to be able to tell it from a buffer error.
        Assert.Equal(SofabError.InvalidMessage, e.Error);
        Assert.Contains("invalid UTF-8", e.Message, System.StringComparison.Ordinal);
        Assert.NotEmpty(rule);
    }

    [Fact]
    public void MalformedBytesAreNeverRepairedIntoAValue()
    {
        // The point of the helper, stated as a test: the same bytes through the
        // default (lossy) decoder produce a string with U+FFFD in it and no error
        // at all. That silent repair is what MESSAGE_SPEC §8 forbids.
        byte[] surrogate = Bytes(0xED, 0xA0, 0x80);
        Assert.Contains("�", Encoding.UTF8.GetString(surrogate), System.StringComparison.Ordinal);
        Assert.Throws<SofabException>(() => Utf8.Decode(surrogate, 0, surrogate.Length));
    }

    [Fact]
    public void AnEncodedReplacementCharacterIsOrdinaryText()
    {
        // U+FFFD is a perfectly valid code point; only the *substitution* of it for
        // malformed bytes is forbidden. A peer that genuinely sent one gets it back.
        byte[] payload = Bytes(0xEF, 0xBF, 0xBD);
        Assert.Equal("�", Utf8.Decode(payload, 0, payload.Length));
    }

    [Fact]
    public void NulAndOtherControlBytesAreOrdinaryText()
    {
        // A `string` payload is a byte range, not a C string: an embedded NUL is
        // data, and carries no terminator meaning (MESSAGE_SPEC §7).
        byte[] payload = Bytes(0x61, 0x00, 0x62);
        Assert.Equal("a\0b", Utf8.Decode(payload, 0, payload.Length));
    }
}
