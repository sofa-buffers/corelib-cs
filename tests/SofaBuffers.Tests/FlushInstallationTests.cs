/*
 * SofaBuffers C# - CORELIB_PLAN 5.1: what a returning flush callback leaves behind.
 *
 * A sink that copies returns without installing a buffer and the encoder resumes
 * in the active buffer at 0; a sink that takes the buffer installs a replacement
 * with BufferSet, and the start offset of *that installation* is where the
 * encoder resumes - including when the same buffer is handed back to re-arm the
 * header reservation for every flushed packet.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using Xunit;

namespace SofaBuffers.Tests;

public class FlushInstallationTests
{
    private const int HeaderRoom = 3;
    private const byte Reserved = 0xEE;

    /// <summary>Encode the same fields in one pass into a large buffer, for comparison.</summary>
    private static byte[] Reference(int fields)
    {
        var buf = new byte[4096];
        var os = new OStream(buf);
        for (int i = 1; i <= fields; i++)
        {
            os.WriteUnsigned(i, (ulong)i);
        }
        return buf[..os.BytesUsed];
    }

    /// <summary>Concatenate the payload of every packet, skipping each one's reserved prefix.</summary>
    private static byte[] Payload(List<byte[]> packets, int prefix)
    {
        var outp = new List<byte>();
        foreach (byte[] p in packets)
        {
            Assert.True(p.Length >= prefix, "packet shorter than its own header room");
            outp.AddRange(p[prefix..]);
        }
        return outp.ToArray();
    }

    /// <summary>
    /// A taking sink hands the filled buffer on and installs the other one with
    /// header room. Every packet - not just the first, which the constructor
    /// installs - must carry that reservation untouched, and the payload must be
    /// the one-shot encoding.
    /// </summary>
    [Fact]
    public void TakingSinkKeepsItsInstallationOffsetOnEveryFlush()
    {
        var a = new byte[8];
        var b = new byte[8];
        a.AsSpan().Fill(Reserved);
        b.AsSpan().Fill(Reserved);

        var packets = new List<byte[]>();
        var usedAfterInstall = new List<int>();
        OStream? os = null;
        FlushSink sink = (data, offset, length) =>
        {
            packets.Add(data[offset..(offset + length)]);
            // Taking sink: `data` now belongs to the transport; install the other
            // buffer, re-arming the framing-header reservation.
            byte[] next = ReferenceEquals(data, a) ? b : a;
            next.AsSpan().Fill(Reserved);
            os!.BufferSet(next, HeaderRoom);
            usedAfterInstall.Add(os.BytesUsed);
        };

        os = new OStream(a, HeaderRoom, sink);
        for (int i = 1; i <= 12; i++)
        {
            os.WriteUnsigned(i, (ulong)i);
        }
        os.Flush();

        Assert.True(packets.Count >= 3, $"expected several packets, got {packets.Count}");
        foreach (int used in usedAfterInstall)
        {
            Assert.Equal(HeaderRoom, used);
        }
        for (int i = 0; i < packets.Count; i++)
        {
            byte[] p = packets[i];
            for (int j = 0; j < HeaderRoom; j++)
            {
                Assert.True(
                    p[j] == Reserved,
                    $"packet {i}: encoder wrote 0x{p[j]:x2} into reserved byte {j}");
            }
        }
        Assert.Equal(Reference(12), Payload(packets, HeaderRoom));
    }

    /// <summary>
    /// The "one framing header per packet" pattern of CORELIB_PLAN 5.1: the sink
    /// copies out, stamps its header over the reserved prefix and re-installs the
    /// *same* buffer at the same offset to re-arm the reservation.
    /// </summary>
    [Fact]
    public void SameBufferReinstalledGivesHeaderRoomInEveryPacket()
    {
        var buf = new byte[8];
        var packets = new List<byte[]>();
        OStream? os = null;
        FlushSink sink = (data, offset, length) =>
        {
            // Stamp a framing header into the reserved prefix, then take a copy of
            // the packet and re-arm the reservation on the same storage.
            data[offset] = 0xAB;
            data[offset + 1] = 0xCD;
            data[offset + 2] = 0xEF;
            packets.Add(data[offset..(offset + length)]);
            os!.BufferSet(data, HeaderRoom);
        };

        os = new OStream(buf, HeaderRoom, sink);
        for (int i = 1; i <= 12; i++)
        {
            os.WriteUnsigned(i, (ulong)i);
        }
        os.Flush();

        Assert.True(packets.Count >= 3, $"expected several packets, got {packets.Count}");
        foreach (byte[] p in packets)
        {
            Assert.Equal(new byte[] { 0xAB, 0xCD, 0xEF }, p[..HeaderRoom]);
        }
        Assert.Equal(Reference(12), Payload(packets, HeaderRoom));
    }

    /// <summary>
    /// A copying sink returns without installing anything: the active buffer stays
    /// active and the encoder resumes at 0, emitting exactly the one-shot bytes.
    /// </summary>
    [Fact]
    public void CopyingSinkResumesAtZero()
    {
        var buf = new byte[8];
        var packets = new List<byte[]>();
        var os = new OStream(buf, 0, (data, offset, length) =>
            packets.Add(data[offset..(offset + length)]));

        for (int i = 1; i <= 12; i++)
        {
            os.WriteUnsigned(i, (ulong)i);
        }
        os.Flush();

        Assert.Equal(0, os.BytesUsed);
        foreach (byte[] p in packets[..^1])
        {
            Assert.Equal(buf.Length, p.Length); // every packet but the tail is a full buffer
        }
        Assert.Equal(Reference(12), Payload(packets, 0));
    }

    /// <summary>
    /// The final <see cref="OStream.Flush"/> is a flush like any other: a sink that
    /// takes that buffer too installs a replacement, and the encoder resumes there.
    /// </summary>
    [Fact]
    public void InstallationDuringFinalFlushSurvives()
    {
        var a = new byte[16];
        var b = new byte[16];
        OStream? os = null;
        int calls = 0;
        FlushSink sink = (data, offset, length) =>
        {
            calls++;
            os!.BufferSet(ReferenceEquals(data, a) ? b : a, 5);
        };

        os = new OStream(a, 0, sink);
        os.WriteUnsigned(1, 7);
        int used = os.Flush();

        Assert.Equal(1, calls);
        Assert.Equal(2, used);
        Assert.Equal(5, os.BytesUsed); // the installation's offset, not 0
    }

    /// <summary>
    /// An installation is consumed by the flush it was made in: a later flush the
    /// sink returns from without installing resumes at 0 (CORELIB_PLAN 5.1).
    /// </summary>
    [Fact]
    public void InstallationIsConsumedByItsOwnFlush()
    {
        var a = new byte[8];
        var b = new byte[8];
        var packets = new List<byte[]>();
        OStream? os = null;
        int calls = 0;
        FlushSink sink = (data, offset, length) =>
        {
            packets.Add(data[offset..(offset + length)]);
            if (calls++ == 0)
            {
                os!.BufferSet(b, HeaderRoom); // take: install with header room
                Assert.Equal(HeaderRoom, os.BytesUsed);
            }
        };

        os = new OStream(a, 0, sink);
        for (int i = 1; i <= 12; i++)
        {
            os.WriteUnsigned(i, (ulong)i);
        }
        os.Flush();

        Assert.True(packets.Count >= 3, $"expected several packets, got {packets.Count}");
        // Packet 0 came from the constructor's installation at 0; packet 1 from the
        // sink's installation at HeaderRoom; every packet after that from a flush
        // the sink returned from without installing, so it resumed at 0.
        var payload = new List<byte>();
        payload.AddRange(packets[0]);
        payload.AddRange(packets[1][HeaderRoom..]);
        for (int i = 2; i < packets.Count; i++)
        {
            payload.AddRange(packets[i]);
        }
        Assert.Equal(Reference(12), payload.ToArray());
    }

    /// <summary>
    /// A large raw payload flushes through <c>PushRaw</c>; a taking sink's
    /// installation offset must survive there too.
    /// </summary>
    [Fact]
    public void PushRawHonoursTheInstallationOffset()
    {
        var a = new byte[8];
        var b = new byte[8];
        a.AsSpan().Fill(Reserved);
        b.AsSpan().Fill(Reserved);

        var packets = new List<byte[]>();
        OStream? os = null;
        FlushSink sink = (data, offset, length) =>
        {
            packets.Add(data[offset..(offset + length)]);
            byte[] next = ReferenceEquals(data, a) ? b : a;
            next.AsSpan().Fill(Reserved);
            os!.BufferSet(next, HeaderRoom);
        };

        var blob = new byte[40];
        for (int i = 0; i < blob.Length; i++)
        {
            blob[i] = (byte)(i + 1);
        }

        os = new OStream(a, HeaderRoom, sink);
        os.WriteBlob(1, blob);
        os.Flush();

        Assert.True(packets.Count >= 3, $"expected several packets, got {packets.Count}");
        foreach (byte[] p in packets)
        {
            for (int j = 0; j < HeaderRoom; j++)
            {
                Assert.Equal(Reserved, p[j]);
            }
        }

        var oneShot = new byte[128];
        var ref1 = new OStream(oneShot);
        ref1.WriteBlob(1, blob);
        Assert.Equal(oneShot[..ref1.BytesUsed], Payload(packets, HeaderRoom));
    }
}
