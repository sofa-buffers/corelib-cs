/*
 * SofaBuffers C# - the shared test helpers stay shared (issue #61).
 *
 * Two helpers are used by most files in this suite: `Bytes(params int[])`, which
 * builds a wire fragment from byte literals, and `Encode(Action<OStream>)`, which
 * runs an encode body and returns exactly the bytes it produced. Both used to be
 * copy-pasted per file -- eight and four copies respectively -- and the copies had
 * already drifted (256- vs 64- vs 4096-byte buffers, `int` vs `byte` parameters).
 * Every copy is a place a change to the encoder surface has to be made again.
 *
 * These are lint-shaped tests, not behaviour tests: they fail when a declaration
 * of either helper reappears outside Common/, and when the conformance suite's
 * SkippingTokenVisitor stops deriving from TokenVisitor and starts re-stating the
 * ~60 lines it only decorates.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace SofaBuffers.Tests;

public class TestHelperHygieneTests
{
    /// <summary>
    /// A declaration -- not a call -- of a method returning <c>byte[]</c> named
    /// <c>Bytes</c> or <c>Encode</c>. A call site never has the return type in
    /// front of the name, so <c>byte[] wire = Encode(...)</c> does not match.
    /// </summary>
    private static readonly Regex Declaration =
        new(@"byte\[\]\s+(Bytes|Encode)\s*\(", RegexOptions.Compiled);

    /// <summary>The only file allowed to declare them.</summary>
    private const string Home = "TestBytes.cs";

    /// <summary>Directory holding this file, i.e. the test project root.</summary>
    private static string SourceDirectory([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path)!;

    private static IEnumerable<string> SourceFiles()
    {
        string root = SourceDirectory();
        // Built from a source drop rather than the repo (e.g. a packaged replay):
        // there is nothing to lint, so the guard stands down instead of failing.
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
            : Enumerable.Empty<string>();
    }

    [Fact]
    public void BytesAndEncodeAreDeclaredOnlyInCommon()
    {
        var offenders = new List<string>();
        bool sawHome = false;

        foreach (string file in SourceFiles())
        {
            string name = Path.GetFileName(file);
            if (name == Path.GetFileName(ThisFile()))
            {
                continue; // this file spells the helper names out on purpose
            }
            foreach (Match m in Declaration.Matches(File.ReadAllText(file)))
            {
                if (name == Home)
                {
                    sawHome = true;
                }
                else
                {
                    offenders.Add(name + ": " + m.Groups[1].Value);
                }
            }
        }

        Assert.Empty(offenders);

        // Guard against passing vacuously: if the shared helpers were deleted or
        // renamed, "no local copies" would be true for the wrong reason.
        if (SourceFiles().Any())
        {
            Assert.True(sawHome, "expected Common/" + Home + " to declare Bytes and Encode");
        }
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    [Fact]
    public void SkippingTokenVisitorDerivesFromTokenVisitor()
    {
        Type owner = typeof(TestVectorsConformanceTests);
        Type? token = owner.GetNestedType("TokenVisitor", BindingFlags.NonPublic);
        Type? skipping = owner.GetNestedType("SkippingTokenVisitor", BindingFlags.NonPublic);

        Assert.NotNull(token);
        Assert.NotNull(skipping);
        Assert.Equal(token, skipping!.BaseType);
    }
}
