/*
 * SofaBuffers C# - shared byte-fragment and encode helpers for tests.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;

namespace SofaBuffers.Tests.Common;

/// <summary>
/// The two helpers nearly every test file needs: <see cref="Bytes"/> to spell a
/// wire fragment out as byte literals, and <see cref="Encode(Action{OStream})"/>
/// to run an encode body and get back exactly the bytes it produced. Import them
/// unqualified with <c>using static SofaBuffers.Tests.Common.TestBytes;</c>.
/// </summary>
public static class TestBytes
{
    /// <summary>
    /// Default scratch buffer for <see cref="Encode(Action{OStream})"/>: large
    /// enough for every fragment the unit tests build, small enough to stay a
    /// stack-sized allocation. Tests that deliberately probe a buffer boundary
    /// pass their own size to <see cref="Encode(int, Action{OStream})"/>.
    /// </summary>
    public const int DefaultBufferSize = 256;

    /// <summary>Builds a byte array from <c>int</c> literals, so call sites can write <c>0xF8</c> without a cast.</summary>
    public static byte[] Bytes(params int[] values)
    {
        var outp = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            outp[i] = (byte)values[i];
        }
        return outp;
    }

    /// <summary>
    /// Wire bytes for one <c>string</c> field carrying <paramref name="payload"/>
    /// verbatim, assembled by hand rather than by the encoder.
    /// </summary>
    /// <remarks>
    /// <c>OStream.WriteFixlen(..., FixlenType.String)</c> validates its byte range
    /// as UTF-8 and refuses one that is not (CORELIB_PLAN §6.4.1, MESSAGE_SPEC §8),
    /// so it cannot build the malformed messages the decode-side UTF-8 tests need.
    /// Those messages are legal wire format — the defect is in the payload, not in
    /// the framing — so they are spelled out here instead: field header, then the
    /// <c>fixlen_word</c> <c>(length &lt;&lt; 3) | 0x2</c>, then the bytes.
    /// </remarks>
    /// <param name="id">field id</param>
    /// <param name="payload">the payload bytes, valid UTF-8 or not</param>
    public static byte[] RawStringField(int id, byte[] payload)
    {
        var outp = new List<byte>();
        Varint(outp, ((ulong)(uint)id << 3) | 0x2);             // T_FIXLEN
        Varint(outp, ((ulong)(uint)payload.Length << 3) | 0x2); // subtype string
        outp.AddRange(payload);
        return outp.ToArray();
    }

    /// <summary>Append <paramref name="value"/> as a base-128 varint, low group first.</summary>
    private static void Varint(List<byte> outp, ulong value)
    {
        while (value >= 0x80)
        {
            outp.Add((byte)(value | 0x80));
            value >>= 7;
        }
        outp.Add((byte)value);
    }

    /// <summary>Encode via <paramref name="body"/> into a fresh buffer and return exactly the used bytes.</summary>
    public static byte[] Encode(Action<OStream> body) => Encode(DefaultBufferSize, body);

    /// <summary>
    /// Encode via <paramref name="body"/> into a fresh buffer of exactly
    /// <paramref name="bufferSize"/> bytes and return the used prefix. No flush
    /// sink is installed, so overflowing the buffer throws -- which is the point
    /// for tests that pick a size on purpose.
    /// </summary>
    public static byte[] Encode(int bufferSize, Action<OStream> body)
    {
        var buf = new byte[bufferSize];
        var os = new OStream(buf);
        body(os);
        var outp = new byte[os.BytesUsed];
        Array.Copy(buf, outp, os.BytesUsed);
        return outp;
    }
}
