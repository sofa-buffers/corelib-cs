/*
 * SofaBuffers C# - WriteString path coverage.
 *
 * WriteString transcodes a short all-ASCII string itself and defers everything
 * else to the runtime's UTF-8 encoder. The fast path writes the header, the
 * length word and the payload before it can know the string is ASCII throughout,
 * and abandons all of it — without advancing the write position — the moment it
 * meets a non-ASCII char. These tests pin that hand-off down: the two paths must
 * produce identical bytes, an abandoned attempt must leave no trace in the
 * output, and neither the length boundary nor a buffer too small for the fast
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
        // The fast path commits the held-back sequence headers before it writes,
        // so an abandoned attempt must not frame the sequence twice.
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
}
