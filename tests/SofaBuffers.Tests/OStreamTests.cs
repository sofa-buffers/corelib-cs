/*
 * SofaBuffers C# - encoder tests (byte-exact vs. the C reference vectors).
 *
 * The expected byte arrays are copied verbatim from the C corelib reference
 * suite (test/c/test_ostream.c) to guarantee byte-for-byte interoperability.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using Xunit;

namespace SofaBuffers.Tests;

public class OStreamTests
{
    /// <summary>Encode via <paramref name="body"/> into a fresh buffer and return exactly the used bytes.</summary>
    private static byte[] Encode(Action<OStream> body)
    {
        var buf = new byte[256];
        var os = new OStream(buf);
        body(os);
        var outp = new byte[os.BytesUsed];
        Array.Copy(buf, outp, os.BytesUsed);
        return outp;
    }

    private static byte[] Bytes(params int[] values)
    {
        var outp = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            outp[i] = (byte)values[i];
        }
        return outp;
    }

    [Fact]
    public void UnsignedIdMin()
    {
        Assert.Equal(Bytes(0x00, 0x00), Encode(os => os.WriteUnsigned(0, 0)));
    }

    [Fact]
    public void UnsignedIdMax()
    {
        Assert.Equal(
            Bytes(0xF8, 0xFF, 0xFF, 0xFF, 0x3F, 0x00),
            Encode(os => os.WriteUnsigned(int.MaxValue, 0)));
    }

    [Fact]
    public void UnsignedMax()
    {
        // UINT64_MAX -> ten 0xFF payload bytes then 0x01.
        Assert.Equal(
            Bytes(0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01),
            Encode(os => os.WriteUnsigned(0, ulong.MaxValue)));
    }

    [Fact]
    public void SignedMin()
    {
        Assert.Equal(
            Bytes(0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01),
            Encode(os => os.WriteSigned(0, long.MinValue)));
    }

    [Fact]
    public void SignedMax()
    {
        Assert.Equal(
            Bytes(0x01, 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01),
            Encode(os => os.WriteSigned(0, long.MaxValue)));
    }

    [Fact]
    public void BooleanTrue()
    {
        Assert.Equal(Bytes(0x00, 0x01), Encode(os => os.WriteBoolean(0, true)));
    }

    [Fact]
    public void Fp32()
    {
        Assert.Equal(
            Bytes(0x02, 0x20, 0x56, 0x0E, 0x49, 0x40),
            Encode(os => os.WriteFp32(0, 3.1415f)));
    }

    [Fact]
    public void Fp64()
    {
        // The C reference widens a float literal: (double) 3.14159265f.
        Assert.Equal(
            Bytes(0x02, 0x41, 0x00, 0x00, 0x00, 0x60, 0xFB, 0x21, 0x09, 0x40),
            Encode(os => os.WriteFp64(0, (double)3.14159265f)));
    }

    [Fact]
    public void String()
    {
        Assert.Equal(
            Bytes(0x02, 0x62, 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0x43, 0x6F, 0x75, 0x63, 0x68, 0x21),
            Encode(os => os.WriteString(0, "Hello Couch!")));
    }

    [Fact]
    public void StringEmpty()
    {
        Assert.Equal(Bytes(0x02, 0x02), Encode(os => os.WriteString(0, "")));
    }

    [Fact]
    public void Blob()
    {
        Assert.Equal(
            Bytes(0x02, 0x2B, 0x01, 0x02, 0x03, 0x04, 0x05),
            Encode(os => os.WriteBlob(0, Bytes(0x01, 0x02, 0x03, 0x04, 0x05))));
    }

    [Fact]
    public void BlobEmpty()
    {
        Assert.Equal(Bytes(0x02, 0x03), Encode(os => os.WriteBlob(0, Array.Empty<byte>())));
    }

    [Fact]
    public void ArrayUnsigned32()
    {
        var a = new uint[] { 1, 2, 3, 0x80000000, 0xFFFFFFFF };
        Assert.Equal(
            Bytes(0x03, 0x05, 0x01, 0x02, 0x03, 0x80, 0x80, 0x80, 0x80, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F),
            Encode(os => os.WriteArrayUnsigned(0, a)));
    }

    [Fact]
    public void ArrayUnsigned16()
    {
        var a = new ushort[] { 1, 2, 3, 0, 0xFFFF };
        Assert.Equal(
            Bytes(0x03, 0x05, 0x01, 0x02, 0x03, 0x00, 0xFF, 0xFF, 0x03),
            Encode(os => os.WriteArrayUnsigned(0, a)));
    }

    [Fact]
    public void ArraySigned32()
    {
        var a = new int[] { -1, -2, -3, int.MinValue, int.MaxValue };
        Assert.Equal(
            Bytes(0x04, 0x05, 0x01, 0x03, 0x05, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0xFE, 0xFF, 0xFF, 0xFF, 0x0F),
            Encode(os => os.WriteArraySigned(0, a)));
    }

    [Fact]
    public void ArrayFp32()
    {
        var a = new float[] { 1.0f, 2.0f, 3.0f, -float.MaxValue, float.MaxValue };
        Assert.Equal(
            Bytes(0x05, 0x05, 0x20, 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x40, 0x00,
                  0x00, 0x40, 0x40, 0xFF, 0xFF, 0x7F, 0xFF, 0xFF, 0xFF, 0x7F, 0x7F),
            Encode(os => os.WriteArrayFp32(0, a)));
    }

    [Fact]
    public void ArrayFp64()
    {
        var a = new double[] { 1.0, 2.0, 3.0, -double.MaxValue, double.MaxValue };
        Assert.Equal(
            Bytes(0x05, 0x05, 0x41, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF0, 0x3F, 0x00,
                  0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00,
                  0x00, 0x08, 0x40, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xEF, 0xFF, 0xFF,
                  0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xEF, 0x7F),
            Encode(os => os.WriteArrayFp64(0, a)));
    }

    [Fact]
    public void NestedSequence()
    {
        Assert.Equal(
            Bytes(0x00, 0x2A, 0x0E, 0x00, 0x2A, 0x11, 0x53, 0x07, 0x11, 0x53),
            Encode(os =>
            {
                os.WriteUnsigned(0, 42);
                os.WriteSequenceBeginLazy(1);
                os.WriteUnsigned(0, 42);
                os.WriteSigned(2, -42);
                os.WriteSequenceEnd();
                os.WriteSigned(2, -42);
            }));
    }

    [Fact]
    public void NestedSequenceWithArray()
    {
        Assert.Equal(
            Bytes(0x00, 0x2A, 0x1E, 0x00, 0x2A, 0x1C, 0x03, 0x53, 0x55, 0x57, 0x07, 0x11, 0x53),
            Encode(os =>
            {
                os.WriteUnsigned(0, 42);
                os.WriteSequenceBeginLazy(3);
                os.WriteUnsigned(0, 42);
                os.WriteArraySigned(3, new int[] { -42, -43, -44 });
                os.WriteSequenceEnd();
                os.WriteSigned(2, -42);
            }));
    }

    // --- lazy sequence framing (MESSAGE_SPEC §2) ----------------------------

    /// <summary>
    /// An all-default sequence carries no information, so the field is omitted --
    /// where the old eager API would have written the two-byte empty frame
    /// <c>0E 07</c>.
    /// </summary>
    [Fact]
    public void LazySequenceWithoutContentEmitsNothing()
    {
        Assert.Equal(Array.Empty<byte>(), Encode(os =>
        {
            os.WriteSequenceBeginLazy(1);
            os.WriteSequenceEnd();
        }));
    }

    /// <summary>
    /// <c>EndKeep</c> forces a contentless frame onto the wire -- the array-element
    /// and explicit-empty cases of §2 / §5.1.
    /// </summary>
    [Fact]
    public void EndKeepFramesAContentlessSequence()
    {
        Assert.Equal(Bytes(0x0E, 0x07), Encode(os =>
        {
            os.WriteSequenceBeginLazy(1);
            os.WriteSequenceEndKeep();
        }));
    }

    /// <summary>
    /// Forcing a frame forces its ancestors too: the outer sequence got content
    /// (the inner frame), so it is framed as well.
    /// </summary>
    [Fact]
    public void EndKeepCommitsTheEnclosingRun()
    {
        Assert.Equal(Bytes(0x0E, 0x16, 0x07, 0x07), Encode(os =>
        {
            os.WriteSequenceBeginLazy(1);
            os.WriteSequenceBeginLazy(2);
            os.WriteSequenceEndKeep();
            os.WriteSequenceEnd();
        }));
    }

    /// <summary>With content the two closers agree -- the headers are already out.</summary>
    [Fact]
    public void EndKeepMatchesEndOnceContentExists()
    {
        byte[] withKeep = Encode(os =>
        {
            os.WriteSequenceBeginLazy(1);
            os.WriteUnsigned(0, 42);
            os.WriteSequenceEndKeep();
        });
        byte[] withEnd = Encode(os =>
        {
            os.WriteSequenceBeginLazy(1);
            os.WriteUnsigned(0, 42);
            os.WriteSequenceEnd();
        });
        Assert.Equal(Bytes(0x0E, 0x00, 0x2A, 0x07), withKeep);
        Assert.Equal(withKeep, withEnd);
    }

    /// <summary>
    /// One child field commits the whole held-back run, outermost header first, so
    /// a non-default leaf deep inside brings every enclosing frame back in wire
    /// order.
    /// </summary>
    [Fact]
    public void LazySequenceCommitsTheWholeRunOnFirstContent()
    {
        Assert.Equal(Bytes(0x0E, 0x16, 0x00, 0x2A, 0x07, 0x07), Encode(os =>
        {
            os.WriteSequenceBeginLazy(1);
            os.WriteSequenceBeginLazy(2);
            os.WriteUnsigned(0, 42);
            os.WriteSequenceEnd();
            os.WriteSequenceEnd();
        }));
    }

    /// <summary>
    /// Only the empty inner sequence drops; the outer one has content (the leaf)
    /// and is framed. This is the interleaving a naive "drop the whole run" would
    /// get wrong.
    /// </summary>
    [Fact]
    public void LazySequenceDropsOnlyTheEmptyInnerOne()
    {
        Assert.Equal(Bytes(0x0E, 0x00, 0x2A, 0x07), Encode(os =>
        {
            os.WriteSequenceBeginLazy(1);
            os.WriteSequenceBeginLazy(2);
            os.WriteSequenceEnd();
            os.WriteUnsigned(0, 42);
            os.WriteSequenceEnd();
        }));
    }

    /// <summary>
    /// A lazily framed sequence <i>after</i> content in the same scope, and the
    /// sibling order, stay intact.
    /// </summary>
    [Fact]
    public void LazySequenceAfterContentIsIndependent()
    {
        Assert.Equal(Bytes(0x00, 0x01, 0x10, 0x03), Encode(os =>
        {
            os.WriteUnsigned(0, 1);
            os.WriteSequenceBeginLazy(1);
            os.WriteSequenceEnd();
            os.WriteUnsigned(2, 3);
        }));
    }

    /// <summary>
    /// A pending run committed <i>across</i> a flush boundary yields exactly the
    /// bytes of the one-shot encode: a 3-byte output buffer flushes in the middle
    /// of this message and the result is byte-identical.
    /// </summary>
    /// <remarks>
    /// Note what this can <b>not</b> test: a flush landing while a header is still
    /// held back is unreachable by construction. Held-back ids are encoder state,
    /// so they occupy no buffer space, and the buffer only fills through a write --
    /// which commits the whole run before its first byte reaches the buffer. A
    /// pending run therefore can never straddle a flush; what a tiny buffer does
    /// exercise is the already-committed bytes being split across flushes, which
    /// is what this asserts.
    /// </remarks>
    [Fact]
    public void RunCommittedAcrossAFlushBoundaryMatchesTheOneShotBytes()
    {
        static void Body(OStream os)
        {
            os.WriteSequenceBeginLazy(1);
            os.WriteSequenceBeginLazy(2);
            os.WriteSequenceEnd();
            os.WriteUnsigned(0, 42);
            os.WriteSequenceEnd();
        }

        var produced = new System.IO.MemoryStream();
        var os = new OStream(new byte[3], 0, (d, o, l) => produced.Write(d, o, l));
        Body(os);
        os.Flush();

        Assert.Equal(Bytes(0x0E, 0x00, 0x2A, 0x07), produced.ToArray());
        Assert.Equal(Encode(Body), produced.ToArray());
    }

    /// <summary>
    /// The exact byte sequence the README's "Nested sequences" example claims.
    /// Documented bytes are easy to get wrong by hand (an id is shifted three bits
    /// left before the tag is or-ed in), so they are pinned here rather than
    /// trusted.
    /// </summary>
    [Fact]
    public void ReadmeNestedSequencesExample()
    {
        Assert.Equal(Bytes(0x26, 0x09, 0x05, 0x07, 0x06, 0x07), Encode(os =>
        {
            os.WriteSequenceBeginLazy(4);
            os.WriteSigned(1, -3);
            os.WriteSequenceEnd();

            os.WriteSequenceBeginLazy(5);
            os.WriteSequenceEnd();

            os.WriteSequenceBeginLazy(0);
            os.WriteSequenceEndKeep();
        }));
    }

    /// <summary>
    /// Nesting far deeper than the 32-level window this encoder used to have, and
    /// closing every level contentless, still emits <b>zero</b> bytes -- precisely
    /// what the old eager fallback got wrong. CORELIB_PLAN §6 requires an
    /// implementation that can allocate to hold back to the full <c>MAX_DEPTH</c>,
    /// so the pending run grows instead of framing eagerly past a bound.
    /// </summary>
    [Fact]
    public void DeepNestingClosedContentlessEmitsNothing()
    {
        Assert.Equal(Array.Empty<byte>(), Encode(os =>
        {
            for (int i = 0; i < 40; i++)
            {
                os.WriteSequenceBeginLazy(1);
            }
            for (int i = 0; i < 40; i++)
            {
                os.WriteSequenceEnd();
            }
        }));
    }

    /// <summary>
    /// The hold-back reaches the format's ceiling: <c>MAX_DEPTH</c> (255) nested
    /// sequences closed contentless emit nothing, and the same nesting with a
    /// single leaf at the bottom commits all 255 headers outermost-first -- so the
    /// run is still a contiguous suffix of the open sequences at maximum depth.
    /// </summary>
    [Fact]
    public void HoldBackReachesMaxDepth()
    {
        const int MaxDepth = 255;

        static byte[] EncodeBig(Action<OStream> body)
        {
            var buf = new byte[4096];
            var os = new OStream(buf);
            body(os);
            var outp = new byte[os.BytesUsed];
            Array.Copy(buf, outp, os.BytesUsed);
            return outp;
        }

        Assert.Equal(Array.Empty<byte>(), EncodeBig(os =>
        {
            for (int i = 0; i < MaxDepth; i++)
            {
                os.WriteSequenceBeginLazy(1);
            }
            for (int i = 0; i < MaxDepth; i++)
            {
                os.WriteSequenceEnd();
            }
        }));

        byte[] wire = EncodeBig(os =>
        {
            for (int i = 0; i < MaxDepth; i++)
            {
                os.WriteSequenceBeginLazy(1);
            }
            os.WriteUnsigned(0, 42);
            for (int i = 0; i < MaxDepth; i++)
            {
                os.WriteSequenceEnd();
            }
        });

        var expected = new byte[MaxDepth + 2 + MaxDepth];
        for (int i = 0; i < MaxDepth; i++)
        {
            expected[i] = 0x0E;                 // sequence start, id 1
            expected[MaxDepth + 2 + i] = 0x07;  // sequence end
        }
        expected[MaxDepth] = 0x00;              // the leaf: unsigned, id 0
        expected[MaxDepth + 1] = 0x2A;          // value 42
        Assert.Equal(expected, wire);
    }

    /// <summary>
    /// Invariant: <b>every</b> writer commits the pending run before its first
    /// byte. A writer that composed its header without going through the choke
    /// point would silently drop the enclosing frame -- data loss, not a style
    /// issue -- so every public write entry point is exercised here: scalar,
    /// boolean, both floats, fixlen, string, both blob overloads, all four
    /// unsigned and all four signed array widths, and both fixlen array kinds.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryWriter))]
    public void EveryWriterCommitsThePendingRun(string name, Action<OStream> write)
    {
        Assert.NotEqual("", name); // keeps the case name in the test output
        byte[] wire = Encode(os =>
        {
            os.WriteSequenceBeginLazy(1);
            write(os);
            os.WriteSequenceEnd();
        });
        Assert.True(wire.Length >= 3, name + " produced " + wire.Length + " bytes");
        Assert.Equal(0x0E, wire[0]);                 // the held-back header, committed
        Assert.Equal(0x07, wire[wire.Length - 1]);   // and its end marker
    }

    public static TheoryData<string, Action<OStream>> EveryWriter => new()
    {
        { "WriteUnsigned", os => os.WriteUnsigned(0, 1) },
        { "WriteSigned", os => os.WriteSigned(0, -1) },
        { "WriteBoolean", os => os.WriteBoolean(0, true) },
        { "WriteFp32", os => os.WriteFp32(0, 1.5f) },
        { "WriteFp64", os => os.WriteFp64(0, 1.5) },
        { "WriteFixlen", os => os.WriteFixlen(0, new byte[] { 1 }, 0, 1, FixlenType.Blob) },
        { "WriteString", os => os.WriteString(0, "x") },
        { "WriteString(empty)", os => os.WriteString(0, "") },
        { "WriteBlob", os => os.WriteBlob(0, new byte[] { 1 }) },
        { "WriteBlob(slice)", os => os.WriteBlob(0, new byte[] { 1, 2, 3 }, 1, 1) },
        { "WriteArrayUnsigned(u8)", os => os.WriteArrayUnsigned(0, new byte[] { 1 }) },
        { "WriteArrayUnsigned(u16)", os => os.WriteArrayUnsigned(0, new ushort[] { 1 }) },
        { "WriteArrayUnsigned(u32)", os => os.WriteArrayUnsigned(0, new uint[] { 1 }) },
        { "WriteArrayUnsigned(u64)", os => os.WriteArrayUnsigned(0, new ulong[] { 1 }) },
        { "WriteArrayUnsigned(empty)", os => os.WriteArrayUnsigned(0, Array.Empty<uint>()) },
        { "WriteArraySigned(i8)", os => os.WriteArraySigned(0, new sbyte[] { -1 }) },
        { "WriteArraySigned(i16)", os => os.WriteArraySigned(0, new short[] { -1 }) },
        { "WriteArraySigned(i32)", os => os.WriteArraySigned(0, new int[] { -1 }) },
        { "WriteArraySigned(i64)", os => os.WriteArraySigned(0, new long[] { -1 }) },
        { "WriteArraySigned(empty)", os => os.WriteArraySigned(0, Array.Empty<int>()) },
        { "WriteArrayFp32", os => os.WriteArrayFp32(0, new float[] { 1.5f }) },
        { "WriteArrayFp32(empty)", os => os.WriteArrayFp32(0, Array.Empty<float>()) },
        { "WriteArrayFp64", os => os.WriteArrayFp64(0, new double[] { 1.5 }) },
        { "WriteArrayFp64(empty)", os => os.WriteArrayFp64(0, Array.Empty<double>()) },
    };

    // --- error / argument handling -----------------------------------------

    [Fact]
    public void IdOverflowRejected()
    {
        var ex = Assert.Throws<SofabException>(
            () => new OStream(new byte[16]).WriteUnsigned(-1, 0));
        Assert.Equal(SofabError.Argument, ex.Error);
    }

    [Fact]
    public void BufferFullWithoutSink()
    {
        var ex = Assert.Throws<SofabException>(
            () => new OStream(new byte[2]).WriteUnsigned(0, ulong.MaxValue));
        Assert.Equal(SofabError.BufferFull, ex.Error);
    }
}
