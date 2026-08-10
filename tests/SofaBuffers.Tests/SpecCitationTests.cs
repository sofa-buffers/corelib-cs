/*
 * SofaBuffers C# - source comments cite the normative specs, not the generator's
 * design document (issue #66).
 *
 * This port is written against the `documentation` repository's normative pair:
 * CORELIB_PLAN.md (the corelib contract) and MESSAGE_SPEC.md (schema type to
 * wire structure), with BENCH_SPEC.md owning the benchmark tooling. ARCHITECTURE.md
 * is a different repository's living design document — it describes the *generator*
 * (`sofabgen`), not this runtime — and its section numbering is its own. A comment
 * citing it sends a reader to the wrong repo, and a reviewer checking the claim
 * against §13's conformance checklist cannot verify it.
 *
 * These are lint-shaped tests, not behaviour tests: they read this suite's and the
 * library's own sources and fail when a comment names a non-normative document, and
 * when the three citations that were retargeted lose the sections that carry them.
 *
 * Scope: `src/` and `tests/`. `bench/` is deliberately not covered here — its two
 * ARCHITECTURE.md citations sit in the benchmark tooling, whose alignment with
 * BENCH_SPEC.md is tracked separately; folding them in would put this guard in the
 * way of that work.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace SofaBuffers.Tests;

public class SpecCitationTests
{
    /// <summary>Repository root: this file lives in tests/SofaBuffers.Tests/.</summary>
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    /// <summary>This file spells the forbidden names out on purpose.</summary>
    private static string ThisFile([CallerFilePath] string path = "") => path;

    /// <summary>
    /// A citation of a document that is not this repository's specification.
    /// `ARCHITECTURE.md` and a bare `PLAN.md` are the generator repo's; the
    /// lookbehind keeps `CORELIB_PLAN.md` — which ends in those same eight
    /// characters — out of the match. "the architecture spec" is the same citation
    /// with the filename left off. The word `architecture` on its own is not an
    /// offence (a CPU architecture is a legitimate thing to name); it only counts
    /// when a section sign or the word `spec`/`document` turns it into a citation.
    /// </summary>
    private static readonly Regex Foreign = new(
        @"ARCHITECTURE(\.md|\s*§)|(?<![A-Za-z_])PLAN\.md|architecture (spec|document|doc)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Every C# source of the library and this suite, in path order.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        string root = RepoRoot();
        foreach (string area in new[] { "src", "tests" })
        {
            string dir = Path.Combine(root, area);
            // Built from a source drop rather than the repo (e.g. a packaged
            // replay): there is nothing to lint, so the guard stands down.
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                         .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// A comment line without its leading marker (<c>*</c>, <c>//</c>, <c>///</c>)
    /// and surrounding space, so two consecutive lines can be joined into the one
    /// sentence they hold: a wrapped citation reads "per the architecture / spec"
    /// across the break and would otherwise slip past a per-line match.
    /// </summary>
    private static string Unwrap(string line) =>
        Regex.Replace(line.Trim(), @"^(///|//|\*)\s*", string.Empty);

    [Fact]
    public void NoSourceCitesTheGeneratorsDesignDocument()
    {
        var offenders = new List<string>();

        foreach (string file in SourceFiles())
        {
            if (Path.GetFileName(file) == Path.GetFileName(ThisFile()))
            {
                continue;
            }
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string head = Unwrap(lines[i]);
                string window = i + 1 < lines.Length ? head + " " + Unwrap(lines[i + 1]) : head;
                Match m = Foreign.Match(window);
                // A match starting past the join belongs to the next line and is
                // reported when that line is the head, so each offence lands once.
                if (m.Success && m.Index < Math.Max(head.Length, 1))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {m.Value}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The retargeted citations: the file, the document its comments must name,
    /// and the sections of that document which actually state what the file
    /// claims. Pinning them here is what keeps the guard above from passing
    /// vacuously — deleting the comments outright would otherwise satisfy it.
    /// </summary>
    public static TheoryData<string, string, string[]> Citations() => new()
    {
        // API_VERSION = 1 stands in the §6.2 constants table; §13's checklist
        // repeats it as "API version constant/getter returns `1`".
        { Path.Combine("tests", "SofaBuffers.Tests", "SofabTests.cs"), "CORELIB_PLAN", new[] { "§6.2" } },
        // The shared vectors are mandated by §7.1 and land in assets/ per §8.
        { Path.Combine("tests", "SofaBuffers.Tests", "TestVectorsConformanceTests.cs"), "CORELIB_PLAN", new[] { "§7.1", "§8" } },
        // "All public symbols live under the `sofab` namespace (§6)."
        { Path.Combine("src", "SofaBuffers", "Sofab.cs"), "CORELIB_PLAN", new[] { "§6" } },
    };

    [Theory]
    [MemberData(nameof(Citations))]
    public void RetargetedCommentsNameTheNormativeSection(string relative, string document, string[] sections)
    {
        string file = Path.Combine(RepoRoot(), relative);
        if (!File.Exists(file))
        {
            return; // source drop, not the repo
        }

        string text = File.ReadAllText(file);
        Assert.Contains(document, text, StringComparison.Ordinal);
        foreach (string section in sections)
        {
            // The document is named once and its sections then referred to by
            // number, which is how the rest of this suite cites them; so the
            // section is looked for on its own, bounded so §8 does not find §8.1.
            Assert.Matches(Regex.Escape(section) + @"(?![\d.])", text);
        }
    }
}
