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
    /// top-level sections; that shared shape is the point." This README carried a
    /// `## Strings &amp; UTF-8` chapter no other port has (issue #64) — its facts
    /// belong in the sections §9 already provides, not in one of their own.
    /// `## Feature flags` is not an invention: every port in the family carries
    /// it, so it is de-facto family shape.
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
                "Feature flags",           // family-wide
                "Build & test",            // §9.7
                "Benchmarks",              // §9.8
            },
            TopLevelSections());
    }

    /// <summary>
    /// Deleting the invented chapter must not delete what it said. The two facts
    /// it carried have a home in the §9 shape: the absent `SOFAB_STRICT_UTF8`
    /// knob belongs where a reader looks for build toggles, and the encode-side
    /// refusal belongs with the `WriteString` example that can trip it.
    /// </summary>
    [Fact]
    public void ReadmeKeepsTheUtf8FactsInTheSectionsSection9Provides()
    {
        if (!HaveTree) return;

        string flags = Section("## Feature flags");
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
}
