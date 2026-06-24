/*
 * SofaBuffers C# - a visitor that overrides nothing must silently drop every
 * field kind (the "not interested" / skip behaviour), exercising every default
 * no-op method.
 *
 * SPDX-License-Identifier: MIT
 */

using Xunit;

namespace SofaBuffers.Tests;

public class VisitorDefaultsTests
{
    /// <summary>A visitor overriding nothing: every default no-op interface method runs.</summary>
    private sealed class IgnoreAll : IVisitor
    {
    }

    [Fact]
    public void DefaultVisitorIgnoresEveryFieldKind()
    {
        // Encode a message touching every field kind the decoder can emit.
        var buf = new byte[256];
        var os = new OStream(buf);
        os.WriteUnsigned(1, 42);
        os.WriteSigned(2, -42);
        os.WriteBoolean(3, true);
        os.WriteFp32(4, 1.5f);
        os.WriteFp64(5, 2.5);
        os.WriteString(6, "hi");
        os.WriteBlob(7, new byte[] { 1, 2, 3 });
        os.WriteArrayUnsigned(8, new uint[] { 1, 2 });
        os.WriteArraySigned(9, new int[] { -1, -2 });
        os.WriteArrayFp64(10, new double[] { 1.0, 2.0 });
        os.WriteSequenceBegin(11);
        os.WriteUnsigned(1, 7);
        os.WriteSequenceEnd();
        int used = os.BytesUsed;

        // A bare visitor overrides nothing: all default no-op methods run.
        var ignoreAll = new IgnoreAll();
        var ex = Record.Exception(() => new IStream().Feed(buf, 0, used, ignoreAll));
        Assert.Null(ex);
    }
}
