/*
 * SofaBuffers C# - CI builds and tests both configurations, with caching (issue #67).
 *
 * CORELIB_PLAN §12.1 lists the required steps of `ci.yml`: step 2 sets the runtime
 * up "with caching enabled", step 4 builds "in both debug and release
 * configurations", and step 5 runs the full test suite. The workflow used to pass
 * `-c Release` literally in both the build and the test step, so nothing in CI ever
 * compiled Debug: a `Debug`-only compile break (`#if DEBUG` code, `Debug.Assert`
 * argument drift) could reach `main` unbuilt, and the length checks guarding the
 * `Unsafe.Add` / `MemoryMarshal.GetArrayDataReference` arithmetic in OStream.cs and
 * IStream.cs were only ever exercised by the Release JIT. Nor did the setup step
 * restore anything from a cache.
 *
 * These are lint-shaped tests, not behaviour tests: they read
 * `.github/workflows/ci.yml` out of the source tree, expand the matrix the
 * build-and-test job declares, and fail when the configurations its `dotnet build`
 * and `dotnet test` invocations actually run in stop covering both Debug and
 * Release, when a matrix leg can cancel its siblings, when the dependency cache
 * disappears again, or when the workflow stops running the whole solution on both
 * push and pull_request (which is how the shared conformance vectors reach CI --
 * they have no workflow of their own). Reading YAML with regexes is enough here
 * because the assertions only concern those commands, the trigger block and the
 * matrix keys feeding them.
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

public class CiWorkflowTests
{
    /// <summary>Repository root: this file lives in tests/SofaBuffers.Tests/.</summary>
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    private static string WorkflowPath() =>
        Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml");

    /// <summary>
    /// Built from a source drop rather than the repo (e.g. a packaged replay):
    /// there is no workflow to lint, so the guards stand down instead of failing.
    /// </summary>
    private static bool HaveWorkflow => File.Exists(WorkflowPath());

    /// <summary>The job that compiles the solution and runs the xUnit suite.</summary>
    private const string BuildTestJob = "build-test";

    /// <summary>
    /// The lines of one job under `jobs:`, from its `  &lt;name&gt;:` key up to the
    /// next key at the same indentation (i.e. the next job).
    /// </summary>
    private static string[] JobBlock(string job)
    {
        string[] lines = File.ReadAllLines(WorkflowPath());
        int start = Array.FindIndex(lines, l => l.StartsWith("  " + job + ":", StringComparison.Ordinal));
        Assert.True(start >= 0, "ci.yml has no `" + job + ":` job");

        int end = lines.Length;
        for (int i = start + 1; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"^  [A-Za-z_][\w-]*:"))
            {
                end = i;
                break;
            }
        }

        return lines[start..end];
    }

    /// <summary>
    /// The values of one matrix key, written either inline (<c>key: [a, b]</c>) or
    /// as a block sequence (<c>key:</c> followed by <c>- a</c> lines).
    /// </summary>
    private static string[] MatrixValues(string[] block, string key)
    {
        for (int i = 0; i < block.Length; i++)
        {
            Match inline = Regex.Match(block[i], @"^\s+" + key + @":\s*\[(?<items>[^\]]*)\]");
            if (inline.Success)
            {
                return inline.Groups["items"].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            if (!Regex.IsMatch(block[i], @"^\s+" + key + @":\s*(#.*)?$"))
            {
                continue;
            }

            var items = new List<string>();
            for (int j = i + 1; j < block.Length; j++)
            {
                Match item = Regex.Match(block[j], @"^\s+-\s*(?<item>\S+)");
                if (!item.Success)
                {
                    break;
                }

                items.Add(item.Groups["item"].Value);
            }

            return items.ToArray();
        }

        return Array.Empty<string>();
    }

    /// <summary>A `-c &lt;config&gt;` argument: a literal, or a `${{ matrix.key }}` reference.</summary>
    private static readonly Regex ConfigurationArgument = new(
        @"-c\s+(?<literal>[A-Za-z]\w*)|-c\s+\$\{\{\s*matrix\.(?<key>\w+)\s*\}\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Every configuration the given `dotnet &lt;verb&gt;` invocations of a job run
    /// in, with matrix references expanded to the values the job declares.
    /// </summary>
    private static SortedSet<string> ConfigurationsOf(string[] block, string verb)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string line in block.Where(l => l.Contains("dotnet " + verb, StringComparison.Ordinal)))
        {
            foreach (Match m in ConfigurationArgument.Matches(line))
            {
                if (m.Groups["literal"].Success)
                {
                    found.Add(m.Groups["literal"].Value);
                }
                else
                {
                    found.UnionWith(MatrixValues(block, m.Groups["key"].Value));
                }
            }
        }

        return found;
    }

    /// <summary>§12.1 step 4: build in both debug and release configurations.</summary>
    [Fact]
    public void CiBuildsBothDebugAndRelease()
    {
        if (!HaveWorkflow)
        {
            return;
        }

        Assert.Equal(
            new[] { "Debug", "Release" },
            ConfigurationsOf(JobBlock(BuildTestJob), "build").ToArray());
    }

    /// <summary>
    /// §12.1 step 5: the full test suite runs — and it runs in every configuration
    /// step 4 builds, so the Debug leg is exercised rather than merely compiled.
    /// </summary>
    [Fact]
    public void CiRunsTheSuiteInEveryConfigurationItBuilds()
    {
        if (!HaveWorkflow)
        {
            return;
        }

        string[] block = JobBlock(BuildTestJob);
        Assert.Equal(
            ConfigurationsOf(block, "build").ToArray(),
            ConfigurationsOf(block, "test").ToArray());
    }

    /// <summary>
    /// §12.1: "set `fail-fast: false` so a failure on one leg does not cancel the
    /// remaining legs" — with two matrix keys there are four legs to keep visible.
    /// </summary>
    [Fact]
    public void CiMatrixLegsDoNotCancelEachOther()
    {
        if (!HaveWorkflow)
        {
            return;
        }

        Assert.Contains(JobBlock(BuildTestJob), l => Regex.IsMatch(l, @"^\s+fail-fast:\s*false\s*$"));
    }

    /// <summary>
    /// §13's checklist item "CI builds and tests on push and PR": the workflow
    /// fires on both events, and the test step runs the whole solution rather
    /// than a hand-picked project.
    /// </summary>
    /// <remarks>
    /// The shared conformance vectors (assets/test_vectors.json, replayed by
    /// TestVectorsConformanceTests) have no CI entry point of their own — they
    /// ride the solution-wide `dotnet test`, which is what makes the skip
    /// scenario of §7.2 item 7 a gate on every push and every pull request. A
    /// test step narrowed to one project, or a trigger losing one of the two
    /// events, would take the vectors out of CI without any test going red, so
    /// the two conditions are asserted here.
    /// </remarks>
    [Fact]
    public void CiRunsTheWholeSolutionOnPushAndPullRequest()
    {
        if (!HaveWorkflow)
        {
            return;
        }

        string[] lines = File.ReadAllLines(WorkflowPath());
        int on = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^on:\s*$"));
        Assert.True(on >= 0, "ci.yml has no `on:` block");

        // The trigger block runs to the next top-level key.
        int end = Array.FindIndex(lines, on + 1, l => Regex.IsMatch(l, @"^[A-Za-z_][\w-]*:"));
        string[] triggers = lines[on..(end < 0 ? lines.Length : end)];

        Assert.Contains(triggers, l => Regex.IsMatch(l, @"^\s+push:"));
        Assert.Contains(triggers, l => Regex.IsMatch(l, @"^\s+pull_request:"));

        Assert.Contains(
            JobBlock(BuildTestJob),
            l => l.Contains("dotnet test", StringComparison.Ordinal)
                && l.Contains("SofaBuffers.sln", StringComparison.Ordinal));
    }

    /// <summary>
    /// §12.1 step 2: set the runtime up "with caching enabled". `setup-dotnet`'s
    /// built-in cache needs a lock file this repo does not carry, so the NuGet
    /// package folder is cached directly; either spelling satisfies the step.
    /// </summary>
    [Fact]
    public void CiCachesRestoredDependencies()
    {
        if (!HaveWorkflow)
        {
            return;
        }

        string[] block = JobBlock(BuildTestJob);
        bool setupDotnetCache = block.Any(l => Regex.IsMatch(l, @"^\s+cache:\s*true\s*$"));
        bool packageFolderCache =
            block.Any(l => l.Contains("actions/cache@", StringComparison.Ordinal)) &&
            block.Any(l => l.Contains(".nuget/packages", StringComparison.Ordinal));

        Assert.True(
            setupDotnetCache || packageFolderCache,
            "ci.yml restores dependencies without a cache (CORELIB_PLAN §12.1 step 2)");
    }
}
