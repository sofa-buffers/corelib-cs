/*
 * SofaBuffers C# - WriteString path coverage.
 *
 * WriteString transcodes a short all-ASCII string itself and defers everything
 * else to the runtime's UTF-8 encoder. Which path applies is decided BEFORE
 * anything is committed — held-back sequence headers included — so a string that
 * turns out to be unencodable (§6.4) leaves the stream exactly as it found it.
 * These tests pin that hand-off down: the two paths must produce identical bytes,
 * an abandoned attempt must leave no trace in the output, a refusal must be
 * atomic, and neither the length boundary nor a buffer too small for the fast
 * path may change what is encoded.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace SofaBuffers.Tests;

public class AsciiStringPathTests
{
    private sealed class StringSink : IVisitor
    {
        public readonly List<byte> Bytes = new();
        public int Id = -1;

        public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
        {
            Id = id;
            Bytes.AddRange(new ReadOnlySpan<byte>(data, chunkOffset, chunkLength).ToArray());
        }
    }

    /// <summary>The wire bytes the format requires for <paramref name="text"/> at <paramref name="id"/>.</summary>
    private static byte[] Expected(int id, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        var wire = new List<byte>();
        Varint(wire, ((ulong)(uint)id << 3) | 0x02);          // T_FIXLEN
        Varint(wire, ((ulong)payload.Length << 3) | 0x02);    // string subtype
        wire.AddRange(payload);
        return wire.ToArray();
    }

    private static void Varint(List<byte> into, ulong v)
    {
        while (v >= 0x80)
        {
            into.Add((byte)(v | 0x80));
            v >>= 7;
        }
        into.Add((byte)v);
    }

    private static byte[] Encode(int bufferSize, Action<OStream> body)
    {
        var buf = new byte[bufferSize];
        var os = new OStream(buf);
        body(os);
        var wire = new byte[os.BytesUsed];
        Array.Copy(buf, wire, wire.Length);
        return wire;
    }

    public static IEnumerable<object[]> Strings()
    {
        // Around the length bound at which the scalar ASCII loop hands over.
        foreach (int n in new[] { 0, 1, 5, 63, 95, 96, 97, 128, 400 })
        {
            yield return new object[] { new string('a', n) };
        }

        // A non-ASCII char at each interesting position: first, interior, last —
        // every one of them must abandon the fast path cleanly.
        yield return new object[] { "ünf" };
        yield return new object[] { "aünf" };
        yield return new object[] { "abcdefghijé" };
        yield return new object[] { new string('a', 95) + "é" };
        yield return new object[] { new string('a', 60) + "中" + new string('b', 30) };
        yield return new object[] { "\U0001F600" };
        yield return new object[] { new string('a', 90) + "\U0001F600" };
        yield return new object[] { "" };   // the ASCII boundary itself
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void EncodesToTheSameBytesWhicheverPathIsTaken(string text)
    {
        Assert.Equal(Expected(6, text), Encode(4096, os => os.WriteString(6, text)));
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void RoundTrips(string text)
    {
        byte[] wire = Encode(4096, os => os.WriteString(6, text));

        var sink = new StringSink();
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, sink));
        Assert.Equal(6, sink.Id);
        Assert.Equal(Encoding.UTF8.GetBytes(text), sink.Bytes.ToArray());
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void ABuffertooSmallForTheFastPathEncodesTheSame(string text)
    {
        // The fast path needs room for the two words plus the payload; a buffer
        // sized exactly to the message denies it that margin, so the general path
        // runs instead and must produce the same bytes.
        byte[] expected = Expected(6, text);
        Assert.Equal(expected, Encode(expected.Length, os => os.WriteString(6, text)));
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void AnAbandonedFastPathLeavesTheStreamIntact(string text)
    {
        // A field before and after the string: an abandoned ASCII attempt writes
        // into the buffer before it gives up, so anything already encoded must
        // survive and everything after it must still land where it belongs.
        byte[] wire = Encode(4096, os =>
        {
            os.WriteUnsigned(1, 300);
            os.WriteString(6, text);
            os.WriteUnsigned(2, 7);
        });

        var expected = new List<byte> { 0x08, 0xAC, 0x02 };   // id 1, unsigned 300
        expected.AddRange(Expected(6, text));
        expected.AddRange(new byte[] { 0x10, 0x07 });          // id 2, unsigned 7
        Assert.Equal(expected.ToArray(), wire);
    }

    [Theory]
    [MemberData(nameof(Strings))]
    public void AStringInsideALazySequenceStillFramesIt(string text)
    {
        // Taking the fast path commits the held-back sequence headers, so an
        // abandoned attempt must not frame the sequence twice.
        byte[] wire = Encode(4096, os =>
        {
            os.WriteSequenceBeginLazy(3);
            os.WriteString(6, text);
            os.WriteSequenceEnd();
        });

        var expected = new List<byte> { 0x1E };                // id 3, sequence start
        expected.AddRange(Expected(6, text));
        expected.Add(0x07);                                    // sequence end
        Assert.Equal(expected.ToArray(), wire);
    }

    /// <summary>
    /// Strings §6.4 refuses, at and around the ASCII fast path's length bound.
    /// Passed around in-process, never as xunit theory data: serializing a theory
    /// argument round-trips it through UTF-8 and would replace the very unpaired
    /// surrogate under test with U+FFFD, leaving a perfectly encodable string.
    /// </summary>
    private static IEnumerable<string> Unencodable()
    {
        yield return "\ud800";                        // lone high surrogate
        yield return "a\ud800b";                      // interior, 3 chars
        yield return "abc\udfff";                     // lone low surrogate, last
        yield return new string('a', 95) + "\ud800";  // exactly at the fast-path bound
        yield return new string('a', 96) + "\ud800";  // one past it
        yield return new string('a', 200) + "\ud800"; // general path
        yield return "é\ud800";                       // non-ASCII and unpaired
    }

    [Fact]
    public void ARefusedStringWritesNothing()
    {
        // CORELIB_PLAN §6.4: a string that cannot be encoded as valid UTF-8 is
        // refused with Argument. The refusal is atomic — the stream must be
        // byte-for-byte what it was before the call, whichever path would have
        // handled the value.
        foreach (string text in Unencodable())
        {
            var buf = new byte[4096];
            var os = new OStream(buf);
            os.WriteUnsigned(1, 300);
            int before = os.BytesUsed;

            var ex = Assert.Throws<SofabException>(() => os.WriteString(6, text));
            Assert.Equal(SofabError.Argument, ex.Error);
            Assert.Equal(before, os.BytesUsed);

            // ... and the stream stays usable: the next field lands where it belongs.
            os.WriteUnsigned(2, 7);
            Assert.Equal(new byte[] { 0x08, 0xAC, 0x02, 0x10, 0x07 }, Take(buf, os));
        }
    }

    [Fact]
    public void ARefusedStringLeavesAHeldBackSequenceUnframed()
    {
        // The refusal must not have committed the held-back header: recovering
        // from it by skipping the field and closing the sequence leaves an
        // all-default sequence OMITTED, as MESSAGE_SPEC §2 requires — not framed
        // empty (26 07), which no other port emits for the same schema value.
        foreach (string text in Unencodable())
        {
            var buf = new byte[4096];
            var os = new OStream(buf);
            os.WriteSequenceBeginLazy(4);

            Assert.Throws<SofabException>(() => os.WriteString(1, text));
            Assert.Equal(0, os.BytesUsed);

            os.WriteSequenceEnd();
            Assert.Equal(0, os.BytesUsed);
            Assert.Equal(Array.Empty<byte>(), Take(buf, os));
        }
    }

    [Fact]
    public void ARefusedStringLeavesAnEnclosingSequenceIntact()
    {
        // Same, one level in and with content after the refusal: the held-back
        // header is committed by the field that follows, exactly once.
        foreach (string text in Unencodable())
        {
            byte[] wire = Encode(4096, os =>
            {
                os.WriteSequenceBeginLazy(4);
                Assert.Throws<SofabException>(() => os.WriteString(1, text));
                os.WriteUnsigned(2, 7);
                os.WriteSequenceEnd();
            });

            Assert.Equal(new byte[] { 0x26, 0x10, 0x07, 0x07 }, wire);
        }
    }

    [Fact]
    public void AValidNonAsciiShortStringStillEncodesInsideALazySequence()
    {
        // The guard that decides the path must not change what a *valid* short
        // non-ASCII string encodes to, nor when the held-back header commits.
        foreach (string text in new[] { "é", "grüße", "日本語", "😀", "a\0é" })
        {
            byte[] wire = Encode(4096, os =>
            {
                os.WriteSequenceBeginLazy(4);
                os.WriteString(1, text);
                os.WriteSequenceEnd();
            });

            var expected = new List<byte> { 0x26 };            // id 4, sequence start
            expected.AddRange(Expected(1, text));
            expected.Add(0x07);                                // sequence end
            Assert.Equal(expected.ToArray(), wire);
        }
    }

    /// <summary>Snapshot of everything written so far.</summary>
    private static byte[] Take(byte[] buffer, OStream os)
    {
        byte[] wire = new byte[os.BytesUsed];
        Array.Copy(buffer, wire, wire.Length);
        return wire;
    }
}
