/*
 * SofaBuffers C# - every example the InvalidMessage doc names really is
 * InvalidMessage (issue #65).
 *
 * `SofabError.InvalidMessage` carries a parenthesised list of examples in its
 * XML `<summary>`, and that list ships: it lands in `SofaBuffers.xml`, in
 * IntelliSense and on the DocFX site, so it is public API documentation. It had
 * drifted from both CORELIB_PLAN §4.7 and this decoder by naming a
 * "zero-length array" as malformed -- a zero-count array is a perfectly normal
 * encoding, `[ header ][ count=0 ]` (plus the fixlen_word for a fixlen array,
 * §4.8), which `IStream` decodes cleanly into a single `ArrayBegin(id, kind, 0)`.
 *
 * This is a lint-shaped test, not a behaviour test: it reads SofabError.cs from
 * the source tree, pulls the example list out of the InvalidMessage summary, and
 * drives every phrase in it through the decoder, requiring a `SofabException`
 * with `SofabError.InvalidMessage`. Each phrase needs an entry in `Cases` below,
 * so a newly documented example cannot be added without a wire fragment that
 * proves it -- and re-adding a phrase describing valid wire (the "zero-length
 * array" entry is kept for exactly that) fails here again.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SofaBuffers.Tests.Common;
using Xunit;
using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class InvalidMessageDocExamplesTests
{
    /// <summary>Repository root: this file lives in tests/SofaBuffers.Tests/.</summary>
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    private static string ErrorSource =>
        Path.Combine(RepoRoot(), "src", "SofaBuffers", "SofabError.cs");

    /// <summary>
    /// A wire fragment per documented example phrase. The key is the phrase as
    /// the doc spells it, normalized by <see cref="Normalize"/> (lower-cased, no
    /// leading article), so the doc may read "a reserved fixlen subtype" while
    /// the key stays "reserved fixlen subtype".
    /// </summary>
    private static readonly Dictionary<string, byte[]> Cases = new(StringComparer.Ordinal)
    {
        // Header varint with 11 continuation bytes: past the 64-bit value type.
        ["varint overflow"] = Bytes(0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
                                    0x80, 0x80, 0x80, 0x80, 0x80),

        // fixlen field (id 0), fixlen header 0x04 -> subtype 4, which is reserved.
        ["reserved fixlen subtype"] = Bytes(0x02, 0x04),

        // Unsigned array (id 0) with count 2^31 -- one past ARRAY_MAX (INT32_MAX).
        ["count above ARRAY_MAX"] = Bytes(0x03, 0x80, 0x80, 0x80, 0x80, 0x08),

        // 256 sequence starts: MAX_DEPTH is 255, so the last one is one too deep.
        ["nesting past MAX_DEPTH"] = Enumerable.Repeat((byte)0x06, 256).ToArray(),

        // A sequence end (0x07) with no sequence open.
        ["dangling sequence end"] = Bytes(0x07),

        // NOT malformed, and kept here on purpose: a zero-count unsigned array is
        // exactly [ header ][ count=0 ] (§4.7). If this phrase ever reappears in
        // the doc, this table hands it to the decoder and the assertion fails --
        // which is the regression this file guards.
        ["zero-length array"] = Bytes(0x03, 0x00),
        ["zero-count array"] = Bytes(0x03, 0x00),
    };

    /// <summary>Lower-case, drop a leading "a "/"an ", collapse inner whitespace.</summary>
    private static string Normalize(string phrase)
    {
        string s = Regex.Replace(phrase.Trim(), @"\s+", " ");
        foreach (string article in new[] { "a ", "an ", "the " })
        {
            if (s.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(article.Length);
                break;
            }
        }
        return s;
    }

    /// <summary>
    /// The doc comment attached to the <c>InvalidMessage</c> enum member, with
    /// the <c>///</c> prefixes and XML tags stripped.
    /// </summary>
    private static string InvalidMessageDoc()
    {
        string source = File.ReadAllText(ErrorSource);
        int member = source.IndexOf("\n    InvalidMessage,", StringComparison.Ordinal);
        Assert.True(member > 0, ErrorSource + " declares no InvalidMessage member");

        // Walk back over the run of `///` lines immediately above the member.
        List<string> lines = source.Substring(0, member).Split('\n').ToList();
        var doc = new List<string>();
        for (int i = lines.Count - 1; i >= 0 && lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal); i--)
        {
            doc.Insert(0, lines[i].TrimStart().Substring(3));
        }
        Assert.NotEmpty(doc);

        string text = string.Join(" ", doc);
        text = Regex.Replace(text, @"<[^>]+>", "");   // <see .../>, <c>...</c>, ...
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>The comma-separated phrases of the doc's parenthesised example list.</summary>
    private static IEnumerable<string> DocumentedExamples()
    {
        Match m = Regex.Match(InvalidMessageDoc(), @"\(([^()]*)\)");
        Assert.True(m.Success, "the InvalidMessage doc names no examples");
        return m.Groups[1].Value
            .Split(',')
            .Select(Normalize)
            .Where(p => p.Length > 0 && p != "...");
    }

    private sealed class IgnoreVisitor : IVisitor
    {
    }

    [Fact]
    public void EveryDocumentedExampleIsRejected()
    {
        string[] examples = DocumentedExamples().ToArray();
        Assert.NotEmpty(examples);

        foreach (string phrase in examples)
        {
            Assert.True(
                Cases.TryGetValue(phrase, out byte[]? wire),
                "the InvalidMessage doc names \"" + phrase + "\" as malformed, but this test "
                    + "has no wire fragment for it -- add one to Cases (and only document "
                    + "examples the decoder really rejects)");

            var ex = Record.Exception(() => new IStream().Feed(wire!, new IgnoreVisitor()));
            Assert.True(
                ex is SofabException se && se.Error == SofabError.InvalidMessage,
                "the InvalidMessage doc names \"" + phrase + "\" as malformed, but the decoder "
                    + "does not reject it: " + (ex?.ToString() ?? "no exception"));
        }
    }

    /// <summary>
    /// The behaviour the doc used to deny: a zero-count array of every kind is
    /// valid wire (§4.7 / §4.8) -- one <c>ArrayBegin</c> with count 0, no
    /// elements, status Complete, and the decoder is not latched Invalid.
    /// </summary>
    [Fact]
    public void ZeroCountArraysAreValidWire()
    {
        (byte[] Wire, string Event)[] cases =
        {
            (Bytes(0x03, 0x00), "arr:0:UNSIGNED:0"),        // unsigned array, count 0
            (Bytes(0x04, 0x00), "arr:0:SIGNED:0"),          // signed array, count 0
            (Bytes(0x05, 0x00, 0x20), "arr:0:FP32:0"),      // fixlen array, count 0 + fixlen_word
            (Bytes(0x05, 0x00, 0x41), "arr:0:FP64:0"),
        };

        foreach ((byte[] wire, string expected) in cases)
        {
            var visitor = new RecordingVisitor();
            var iss = new IStream();
            Assert.Equal(DecodeStatus.Complete, iss.Feed(wire, visitor));
            Assert.Equal(DecodeStatus.Complete, iss.Status);
            Assert.Equal(new[] { expected }, visitor.Events);
        }
    }
}
