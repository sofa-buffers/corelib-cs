/*
 * SofaBuffers C# - the README keeps the family's shape and its facts stay true
 * (issues #63, #64).
 *
 * CORELIB_PLAN §9 opens by demanding that "every fact, command, version number,
 * dependency, feature flag, and API name the README states must match the code
 * as it stands today". The prose facts are already executed elsewhere in this
 * suite (ReadmeGeneratorExampleTests, ReadmeNestedSequencesExample); what was
 * never checked is the *build-level* prose, and it drifted: the README claimed a
 * single `net9.0` target and an SDK-9 requirement after every project had gone
 * multi-target, and its two `dotnet run` benchmark lines were unrunnable as
 * written -- a multi-TFM project refuses to run without `--framework`.
 *
 * §9 also fixes the *shape*: "do not change the section ordering and do not
 * invent new top-level sections". This README had grown a `## Strings & UTF-8`
 * chapter no other port carries, so that guard is checked here too — together
 * with the facts that chapter held, which had to survive the move into the
 * sections §9 provides.
 *
 * Shape alone is not enough for a README that is about to be shortened: a
 * chapter can keep its heading and lose the fact a reader came for, and nothing
 * the compiler sees notices. So the checks come in two halves.
 *
 *   Shape   — §9.1 the centered header block; §9.2 the badge block's CI /
 *             coverage / Docs badges, in that order; §9 the exact `## ` list, in
 *             order; §9.4 no API-documentation chapter at any heading level.
 *   Content — §9.5 the Usage chapter still shows every example the plan lists;
 *             §9.6 MIN_OUTPUT_BUFFER stated *inside* the memory chapter; §6.4
 *             the port's UTF-8 position; and every in-document link resolving to
 *             a heading that still exists.
 *
 * §6.1.1's closed generated-object name set is guarded too, by
 * ReadmeGeneratorExampleTests.ReadmeNamesNothingOutsideTheClosedGeneratedSurface;
 * it is not repeated here.
 *
 * These are lint-shaped tests, not behaviour tests. They read the README, the
 * three .csproj files and bench/run_callgrind.sh from the source tree and fail
 * when the documented section list, target frameworks, SDK requirement,
 * benchmark command lines, tool inventory or Callgrind workload list stop
 * matching what is actually in the tree.
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

public class ReadmeFactsTests
{
    /// <summary>Repository root: this file lives in tests/SofaBuffers.Tests/.</summary>
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    /// <summary>
    /// Built from a source drop rather than the repo (e.g. a packaged replay):
    /// there is nothing to lint, so the guards stand down instead of failing.
    /// </summary>
    private static bool HaveTree => File.Exists(Path.Combine(RepoRoot(), "README.md"));

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static string Readme() => Read("README.md");

    private static readonly string[] Projects =
    {
        Path.Combine("src", "SofaBuffers", "SofaBuffers.csproj"),
        Path.Combine("tests", "SofaBuffers.Tests", "SofaBuffers.Tests.csproj"),
        Path.Combine("bench", "SofaBuffers.Bench", "SofaBuffers.Bench.csproj"),
    };

    /// <summary>The `net9.0;net10.0` list of one project file, in file order.</summary>
    private static string[] TargetFrameworks(string project)
    {
        Match m = Regex.Match(Read(project.Split(Path.DirectorySeparatorChar)),
                              @"<TargetFrameworks?>([^<]+)</TargetFrameworks?>");
        Assert.True(m.Success, project + " declares no TargetFramework(s)");
        return m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).ToArray();
    }

    /// <summary>Major version of a `netX.Y` moniker, e.g. 10 for `net10.0`.</summary>
    private static int Major(string tfm) =>
        int.Parse(Regex.Match(tfm, @"^net(\d+)\.").Groups[1].Value);

    /// <summary>
    /// The text of a `## Heading` / `### Heading` section, up to the next heading
    /// of the same or a shallower level (so a `## ` section carries its `### `
    /// subsections, and a `### ` one stops at its sibling).
    /// </summary>
    private static string Section(string heading)
    {
        string readme = Readme();
        int start = readme.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "README has no " + heading + " section");
        int level = heading.TakeWhile(c => c == '#').Count();

        foreach ((int index, int found) in Headings(readme))
        {
            if (index > start && found <= level)
            {
                return readme.Substring(start, index - start);
            }
        }
        return readme.Substring(start);
    }

    /// <summary>
    /// Offset and level of every Markdown heading, skipping fenced code blocks —
    /// a shell comment such as `# workloads: ...` inside a ```bash fence is not a
    /// heading, and treating it as one would cut a section short.
    /// </summary>
    private static IEnumerable<(int Index, int Level)> Headings(string text)
    {
        bool fenced = false;
        int index = 0;
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }
            else if (!fenced)
            {
                int level = line.TakeWhile(c => c == '#').Count();
                if (level > 0 && level < line.Length && line[level] == ' ')
                {
                    yield return (index, level);
                }
            }
            index += line.Length + 1;
        }
    }

    /// <summary>Every `## Heading` in the README, in file order.</summary>
    private static string[] TopLevelSections()
    {
        string readme = Readme();
        return Headings(readme)
            .Where(h => h.Level == 2)
            .Select(h => readme.Substring(h.Index).Split('\n')[0].Substring(3).Trim())
            .ToArray();
    }

    /// <summary>
    /// CORELIB_PLAN §9: "Do not change the section ordering and do not invent new
    /// top-level sections; that shared shape is the point." The list below is
    /// §9's, and only the first entry's wording is per-port
    /// (`## SofaBuffers &lt;Language&gt; library`). Two chapters this README once
    /// carried are not on it: `## Strings &amp; UTF-8` (issue #64) and
    /// `## Feature flags` — the latter was believed to be de-facto family shape
    /// until corelib-go#125 and corelib-cpp#122 removed it from those ports,
    /// leaving C# the only one with an eighth chapter. A section that has facts
    /// worth keeping is demoted to a `###` subsection of the chapter it belongs
    /// to, never added as a row here.
    /// </summary>
    [Fact]
    public void ReadmeTopLevelSectionsAreTheSharedFamilyShape()
    {
        if (!HaveTree) return;

        Assert.Equal(
            new[]
            {
                "SofaBuffers C# library",  // §9.2
                "Why this design",         // §9.3
                "Usage",                   // §9.5
                "Memory handling",         // §9.6
                "Build & test",            // §9.7
                "Benchmarks",              // §9.8
            },
            TopLevelSections());
    }

    /// <summary>
    /// Deleting an invented chapter must not delete what it said. The facts the
    /// two carried have a home in the §9 shape: the port's UTF-8 position with
    /// the rest of what a reader needs before choosing the library, and the
    /// encode-side refusal with the `WriteString` example that can trip it.
    ///
    /// §6.4 does <em>not</em> oblige this port to have a `SOFAB_STRICT_UTF8`
    /// knob, so no check here demands one: C# `string` is a Unicode string type,
    /// which "cannot hold non-UTF-8 bytes", so §6.4 makes such targets "always
    /// strict" and lets them "omit it entirely (documented as always-ON)".
    /// Only byte-container targets MUST expose the option — corelib-go and
    /// corelib-c are the ports whose guards check for a live knob. What §6.4
    /// does require of this port is the <em>documentation</em> of that position,
    /// which is what the two assertions below hold on to.
    /// </summary>
    [Fact]
    public void ReadmeKeepsTheUtf8FactsInTheSectionsSection9Provides()
    {
        if (!HaveTree) return;

        string flags = Section("### Feature flags");
        Assert.Contains("SOFAB_STRICT_UTF8", flags, StringComparison.Ordinal);
        Assert.Contains("always strict", flags, StringComparison.Ordinal);

        // Encode side: the refusal, and that it is not a silent U+FFFD swap.
        string serialize = Section("### Serialize\n");
        Assert.Contains("unpaired surrogate", serialize, StringComparison.Ordinal);
        Assert.Contains("U+FFFD", serialize, StringComparison.Ordinal);
        Assert.Contains("SofabError.Argument", serialize, StringComparison.Ordinal);

        // Decode side: the corelib transcodes nothing; generated code judges.
        string deserialize = Section("### Deserialize\n");
        Assert.Contains("InvalidMessage", deserialize, StringComparison.Ordinal);
        Assert.Contains("strict/fatal", deserialize, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every project multi-targets the same list, which is what makes the
    /// README's single claim about "the solution" a claim about all three. If
    /// they ever diverge the README sentence stops being expressible and this
    /// says so before the reader finds out.
    /// </summary>
    [Fact]
    public void AllProjectsTargetTheSameFrameworks()
    {
        if (!HaveTree) return;

        string[] first = TargetFrameworks(Projects[0]);
        Assert.NotEmpty(first);
        foreach (string p in Projects.Skip(1))
        {
            Assert.Equal(first, TargetFrameworks(p));
        }
    }

    /// <summary>
    /// The README must name every framework the projects target -- it used to
    /// say "the library targets `net9.0`" while all three projects had been
    /// building `net9.0;net10.0` for some time.
    /// </summary>
    [Fact]
    public void ReadmeNamesEveryTargetFramework()
    {
        if (!HaveTree) return;

        string readme = Readme();
        foreach (string tfm in TargetFrameworks(Projects[0]))
        {
            Assert.Contains(tfm, readme, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// `dotnet restore` resolves *all* TargetFrameworks in the solution (the
    /// reason ci.yml installs both SDKs in every leg), so the minimum SDK is the
    /// newest framework's, not the oldest. Both places that state the
    /// requirement have to say so.
    /// </summary>
    [Fact]
    public void ReadmeRequiresTheSdkTheNewestTargetFrameworkNeeds()
    {
        if (!HaveTree) return;

        int newest = TargetFrameworks(Projects[0]).Select(Major).Max();
        var claimsAnSdk = new Regex(@"SDK\s+(\d+)");

        foreach (string heading in new[] { "### Requirements", "## Build & test" })
        {
            string section = Section(heading);
            int[] claimed = claimsAnSdk.Matches(section)
                .Select(m => int.Parse(m.Groups[1].Value)).ToArray();
            Assert.NotEmpty(claimed);
            Assert.All(claimed, major => Assert.Equal(newest, major));
        }
    }

    /// <summary>
    /// A `dotnet run` line against a multi-TFM project fails with "Your project
    /// targets multiple frameworks" unless it names one. Every such command the
    /// tree documents -- README and the bench entry point's usage header -- has
    /// to name a framework the project actually has.
    /// </summary>
    [Fact]
    public void DocumentedDotnetRunCommandsNameAFramework()
    {
        if (!HaveTree) return;

        string[] tfms = TargetFrameworks(Projects[2]);
        var names = new Regex(@"(?:-f|--framework)[= ]+(\S+)");
        var docs = new[] { "README.md", Path.Combine("bench", "SofaBuffers.Bench", "Program.cs") };

        int seen = 0;
        foreach (string doc in docs)
        {
            foreach (string raw in Read(doc.Split(Path.DirectorySeparatorChar)).Split('\n'))
            {
                string line = raw.Trim();
                if (!line.Contains("dotnet run", StringComparison.Ordinal) ||
                    !line.Contains("SofaBuffers.Bench", StringComparison.Ordinal))
                {
                    continue;
                }
                seen++;
                Match m = names.Match(line);
                Assert.True(m.Success, doc + ": multi-target project, no framework: " + line);
                Assert.Contains(m.Groups[1].Value, tfms);
            }
        }

        // Guard against passing vacuously if the commands are ever reworded away.
        Assert.True(seen >= 2, "expected the documented perf and bench command lines");
    }

    /// <summary>
    /// CORELIB_PLAN §10: every corelib ships *three* benchmark tools. The
    /// Benchmarks section has to introduce all three, not two with the third
    /// mentioned as an afterthought.
    /// </summary>
    [Fact]
    public void BenchmarksSectionIntroducesAllThreeTools()
    {
        if (!HaveTree) return;

        string section = Section("## Benchmarks");
        string intro = section.Substring(0, section.IndexOf("```", StringComparison.Ordinal));

        foreach (string tool in new[] { "perf", "bench", "run_callgrind.sh" })
        {
            Assert.Contains(tool, intro, StringComparison.Ordinal);
        }
        Assert.Matches(@"\b[Tt]hree\b", intro);
    }

    /// <summary>
    /// The workload list the README prints next to `run_callgrind.sh` is the
    /// list that script actually runs.
    /// </summary>
    [Fact]
    public void ReadmeCallgrindWorkloadsMatchTheScript()
    {
        if (!HaveTree) return;

        // `WORKLOADS="${WORKLOADS:-a b c}"`: a default the environment can
        // override, so a caller can measure one row without editing the script.
        Match declared = Regex.Match(Read("bench", "run_callgrind.sh"),
                                     @"WORKLOADS=""\$\{WORKLOADS:-([^}]*)\}""");
        Assert.True(declared.Success, "run_callgrind.sh declares no WORKLOADS");
        string[] script = declared.Groups[1].Value
            .Split(new[] { ' ', '\t', '\n', '\r', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();

        Match listed = Regex.Match(Section("## Benchmarks"), @"workloads:([^\n]*)");
        Assert.True(listed.Success, "README lists no Callgrind workloads");
        string[] documented = listed.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0)
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();

        Assert.Equal(script, documented);
    }

    // ------------------------------------------------------------ §9 shape

    /// <summary>
    /// Level and text of every Markdown heading, fenced code skipped — the
    /// text-carrying twin of <see cref="Headings(string)"/>.
    /// </summary>
    private static IEnumerable<(int Level, string Text)> HeadingLines()
    {
        string readme = Readme();
        foreach ((int index, int level) in Headings(readme))
        {
            string line = readme.Substring(index).Split('\n')[0];
            yield return (level, line.Substring(level).Trim());
        }
    }

    /// <summary>
    /// §9.1 fixes a generic header block every port reproduces verbatim: the
    /// centered logo, the `# SofaBuffers` title, the two-line tagline and a link
    /// back to the organization. It is the one part of the README that is not
    /// about this port at all, so nothing in the C# tree can notice it going
    /// missing.
    /// </summary>
    [Fact]
    public void ReadmeOpensWithTheGenericHeaderBlock()
    {
        if (!HaveTree) return;

        string readme = Readme();
        Assert.Contains("<p align=\"center\"><img src=\"assets/sofabuffers_logo.png\"", readme,
                        StringComparison.Ordinal);
        Assert.Contains("\n# SofaBuffers\n", readme, StringComparison.Ordinal);
        Assert.Contains("<b>Structured Objects For Anyone</b><br>", readme, StringComparison.Ordinal);
        Assert.Contains("<i>... so optimized, feels amazing.</i>", readme, StringComparison.Ordinal);
        Assert.Contains("https://github.com/sofa-buffers", readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// §9.2 opens the library section with badges, "CI, coverage, and a Docs
    /// badge" in that order, ahead of the GitHub link and the summary. The Docs
    /// badge is load-bearing beyond decoration: §9.4 makes it the README's only
    /// pointer to the API reference, so losing it strands every per-symbol
    /// detail this README is allowed to leave out.
    ///
    /// Extra badges are fine (this port publishes a branch-coverage one beside
    /// the line-coverage one); the three §9.2 names must be present and in
    /// §9.2's relative order.
    /// </summary>
    [Fact]
    public void ReadmeBadgeBlockLeadsWithCiCoverageAndDocs()
    {
        if (!HaveTree) return;

        string section = Section("## SofaBuffers C# library");
        // The badge block is everything up to the first blank line after the
        // heading: §9.2 puts it before the GitHub link and the prose summary.
        string[] lines = section.Split('\n');
        var badges = new List<string>();
        bool started = false;
        foreach (string line in lines.Skip(1))
        {
            Match m = Regex.Match(line.Trim(), @"^\[!\[([^\]]+)\]");
            if (m.Success)
            {
                started = true;
                badges.Add(m.Groups[1].Value);
            }
            else if (started)
            {
                break;
            }
        }

        Assert.NotEmpty(badges);
        string[] ranked = badges
            .Where(b => b is "CI" or "Coverage" or "Docs")
            .ToArray();
        Assert.Equal(new[] { "CI", "Coverage", "Docs" }, ranked);
    }

    /// <summary>
    /// §9.4: "There is no API-documentation chapter." The Docs badge is the
    /// single entry point, so a `## API reference` would not become legal by
    /// being demoted — the check runs at every heading level.
    /// </summary>
    [Fact]
    public void ReadmeHasNoApiDocumentationChapter()
    {
        if (!HaveTree) return;

        string[] forbidden = { "api reference", "api documentation", "api docs", "source documentation" };
        foreach ((int level, string text) in HeadingLines())
        {
            Assert.DoesNotContain(text.ToLowerInvariant(), forbidden);
        }
    }

    // ---------------------------------------------------------- §9 content

    /// <summary>
    /// §9.5 lists the examples every port's Usage chapter carries: simple
    /// encode, simple decode, streaming a message larger than the buffer, the
    /// OStream and IStream wrappers, and the generator path. Each one is a use
    /// case, not prose — dropping a heading here drops the use case with it. The
    /// wording is the family's; only the code inside is per-language.
    /// </summary>
    [Fact]
    public void UsageShowsEveryExampleThePlanLists()
    {
        if (!HaveTree) return;

        string usage = Section("## Usage");
        foreach (string example in new[]
                 {
                     "### Serialize\n",         // §9.5 simple encode + OStream
                     "### Serialize stream\n",  // §9.5 larger than the buffer
                     "### Deserialize\n",       // §9.5 simple decode + IStream
                     "### Deserialize stream\n",
                     "### Code generator\n",    // §9.5 the generated-object path
                 })
        {
            Assert.Contains(example, usage, StringComparison.Ordinal);
        }

        // Every example is runnable code, so each heading owns a fenced block.
        Assert.True(usage.Split("```csharp").Length - 1 >= 5,
                    "the Usage chapter lost a runnable C# example");
    }

    /// <summary>
    /// §9.6 puts `MIN_OUTPUT_BUFFER` in the memory chapter specifically: it is
    /// the number a caller needs before it can size a streaming buffer, and the
    /// memory chapter is where they go to find out who allocates what, so
    /// stating it anywhere else does not reach them. The constant's *value* is
    /// checked against the code by MinOutputBufferTests; what is checked here is
    /// that the README states it, in that chapter.
    /// </summary>
    [Fact]
    public void MemoryChapterStatesMinOutputBuffer()
    {
        if (!HaveTree) return;

        string memory = Section("## Memory handling");
        Assert.Contains("MIN_OUTPUT_BUFFER", memory, StringComparison.Ordinal);
        Assert.Contains("MinOutputBuffer", memory, StringComparison.Ordinal);
    }

    /// <summary>
    /// A heading that moves takes its anchor with it, which is the cheapest way
    /// for a restructuring to break navigation while breaking nothing a build
    /// can see. Every `](#anchor)` must name a heading the document still has.
    /// </summary>
    [Fact]
    public void EveryInDocumentLinkResolvesToAHeading()
    {
        if (!HaveTree) return;

        var anchors = HeadingLines().Select(h => GitHubAnchor(h.Text)).ToHashSet(StringComparer.Ordinal);
        MatchCollection links = Regex.Matches(Readme(), @"\]\(#([^)]+)\)");
        Assert.NotEmpty(links);   // a vacuous pass would mean the scan broke
        foreach (Match link in links)
        {
            Assert.Contains(link.Groups[1].Value, anchors);
        }
    }

    /// <summary>Slugifies a heading the way GitHub does: lowercase, punctuation
    /// dropped, spaces to hyphens.</summary>
    private static string GitHubAnchor(string title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
            {
                sb.Append(c);
            }
            else if (c == ' ')
            {
                sb.Append('-');
            }
        }
        return sb.ToString();
    }
}
