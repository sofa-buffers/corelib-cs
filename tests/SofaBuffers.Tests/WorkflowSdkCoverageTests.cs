/*
 * SofaBuffers C# - every workflow job that restores the solution installs an SDK
 * for every target framework (issue #68).
 *
 * CORELIB_PLAN §12.1 step 2 and §12.2 step 2 both require the workflow to set the
 * runtime up itself; §12.1 step 3 and §12.2 step 3 then install/restore the
 * dependencies. For .NET those two steps are coupled in a way that is easy to get
 * wrong: `dotnet restore` resolves *all* TargetFrameworks in the solution no matter
 * which one a later `-f` narrows the build to, so an SDK older than the newest TFM
 * cannot restore at all (NETSDK1045). `docs.yml` used to pin `9.0.x` alone while all
 * three projects target `net9.0;net10.0`; it survived only because the hosted
 * `ubuntu-latest` image happens to ship a .NET 10 SDK that `setup-dotnet` leaves in
 * place. On a runner without that accident the restore fails, DocFX (which reads the
 * XML doc comments through MSBuild) never runs, and the published API reference plus
 * the README's Docs badge go stale silently.
 *
 * These are lint-shaped tests, not behaviour tests: they read the workflow files and
 * the `.csproj` TargetFrameworks out of the source tree, and fail when any job that
 * invokes `dotnet restore`/`build`/`test` sets up a set of SDKs that does not cover
 * every framework the solution targets. Covering all workflows rather than `docs.yml`
 * alone keeps the two files from drifting apart in either direction.
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

public class WorkflowSdkCoverageTests
{
    /// <summary>Repository root: this file lives in tests/SofaBuffers.Tests/.</summary>
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    private static string WorkflowsDir() =>
        Path.Combine(RepoRoot(), ".github", "workflows");

    /// <summary>
    /// Built from a source drop rather than the repo (e.g. a packaged replay):
    /// there are no workflows and no projects to lint, so the guards stand down
    /// instead of failing.
    /// </summary>
    private static bool HaveTree =>
        Directory.Exists(WorkflowsDir()) &&
        File.Exists(Path.Combine(RepoRoot(), "src", "SofaBuffers", "SofaBuffers.csproj"));

    /// <summary>Every project of the solution, in the order the solution lists them.</summary>
    private static readonly string[][] Projects =
    {
        new[] { "src", "SofaBuffers", "SofaBuffers.csproj" },
        new[] { "tests", "SofaBuffers.Tests", "SofaBuffers.Tests.csproj" },
        new[] { "bench", "SofaBuffers.Bench", "SofaBuffers.Bench.csproj" },
    };

    /// <summary>
    /// Every framework the solution targets, e.g. <c>net9.0</c>, <c>net10.0</c> —
    /// the union over the projects, because a restore of the solution resolves all
    /// of them.
    /// </summary>
    private static string[] SolutionTargetFrameworks()
    {
        var tfms = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string[] parts in Projects)
        {
            string path = Path.Combine(RepoRoot(), Path.Combine(parts));
            if (!File.Exists(path))
            {
                continue;
            }

            Match m = Regex.Match(
                File.ReadAllText(path),
                @"<TargetFrameworks?>(?<tfms>[^<]+)</TargetFrameworks?>");
            if (m.Success)
            {
                tfms.UnionWith(m.Groups["tfms"].Value
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return tfms.ToArray();
    }

    /// <summary>Each job under a workflow's <c>jobs:</c> key, with its own lines.</summary>
    private static IEnumerable<(string Name, string[] Lines)> Jobs(string workflow)
    {
        string[] lines = File.ReadAllLines(workflow);

        int jobsAt = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^jobs:\s*(#.*)?$"));
        if (jobsAt < 0)
        {
            yield break;
        }

        var starts = new List<int>();
        for (int i = jobsAt + 1; i < lines.Length; i++)
        {
            // A job key sits at exactly two spaces of indentation; everything
            // inside a job (`name:`, `runs-on:`, `steps:`) is deeper.
            if (Regex.IsMatch(lines[i], @"^  [A-Za-z_][\w-]*:\s*(#.*)?$"))
            {
                starts.Add(i);
            }
        }

        for (int s = 0; s < starts.Count; s++)
        {
            int start = starts[s];
            int end = s + 1 < starts.Count ? starts[s + 1] : lines.Length;
            yield return (lines[start].Trim().TrimEnd(':'), lines[start..end]);
        }
    }

    /// <summary>
    /// The `dotnet` verbs that trigger a restore of the whole solution — and so
    /// need an SDK for every TargetFramework in it, even when a `-f` narrows what
    /// is built or tested afterwards.
    /// </summary>
    private static readonly Regex RestoringCommand = new(
        @"dotnet\s+(restore|build|test|run|publish|pack)\b",
        RegexOptions.Compiled);

    private static bool Restores(string[] job) => job.Any(l => RestoringCommand.IsMatch(l));

    /// <summary>
    /// The versions a job's `setup-dotnet` steps install: a single scalar
    /// (<c>dotnet-version: '9.0.x'</c>) or a block scalar listing one per line.
    /// </summary>
    private static string[] SdkVersions(string[] job)
    {
        var versions = new List<string>();

        for (int i = 0; i < job.Length; i++)
        {
            Match m = Regex.Match(job[i], @"^(?<indent>\s*)dotnet-version:\s*(?<value>.*?)\s*(#.*)?$");
            if (!m.Success)
            {
                continue;
            }

            string value = m.Groups["value"].Value;
            if (value.Length > 0 && value[0] is not ('|' or '>'))
            {
                versions.Add(value.Trim('\'', '"'));
                continue;
            }

            int indent = m.Groups["indent"].Value.Length;
            for (int j = i + 1; j < job.Length; j++)
            {
                if (job[j].Trim().Length == 0)
                {
                    continue;
                }

                if (job[j].Length - job[j].TrimStart().Length <= indent)
                {
                    break;
                }

                versions.Add(job[j].Trim().Trim('\'', '"'));
            }
        }

        return versions.ToArray();
    }

    /// <summary>
    /// Whether an SDK version selector — `10.0.x`, `10.0.100`, `10.x` — can serve a
    /// target framework such as `net10.0`.
    /// </summary>
    private static bool Covers(string version, string tfm)
    {
        Match t = Regex.Match(tfm, @"^net(?<major>\d+)\.(?<minor>\d+)$");
        if (!t.Success)
        {
            return true;   // not a .NET (Core) TFM: no SDK band to match
        }

        string major = t.Groups["major"].Value;
        string minor = t.Groups["minor"].Value;

        return version.StartsWith(major + "." + minor + ".", StringComparison.Ordinal) ||
               version == major + "." + minor ||
               version == major + ".x";
    }

    /// <summary>
    /// §12.1/§12.2 steps 2–3: a job that restores the solution must set up an SDK
    /// for every framework the solution targets, because `dotnet restore` resolves
    /// all of them.
    /// </summary>
    [Fact]
    public void EveryRestoringJobInstallsAnSdkForEveryTargetFramework()
    {
        if (!HaveTree)
        {
            return;
        }

        string[] tfms = SolutionTargetFrameworks();
        Assert.NotEmpty(tfms);

        int checkedJobs = 0;
        foreach (string workflow in Directory.GetFiles(WorkflowsDir(), "*.yml").OrderBy(p => p, StringComparer.Ordinal))
        {
            foreach ((string name, string[] job) in Jobs(workflow))
            {
                if (!Restores(job))
                {
                    continue;
                }

                checkedJobs++;
                string[] versions = SdkVersions(job);
                foreach (string tfm in tfms)
                {
                    Assert.True(
                        versions.Any(v => Covers(v, tfm)),
                        Path.GetFileName(workflow) + ": job `" + name + "` restores the solution but " +
                        "installs no SDK for " + tfm + " (has: " +
                        (versions.Length == 0 ? "none" : string.Join(", ", versions)) + ")");
                }
            }
        }

        Assert.True(checkedJobs > 0, "no workflow job runs a restoring `dotnet` command");
    }

    /// <summary>
    /// §12.2 step 2: the docs job pins the runtime itself rather than relying on
    /// whatever the hosted image happens to ship.
    /// </summary>
    [Fact]
    public void DocsWorkflowSetsUpItsOwnRuntime()
    {
        if (!HaveTree)
        {
            return;
        }

        string docs = Path.Combine(WorkflowsDir(), "docs.yml");
        Assert.True(File.Exists(docs), "docs.yml is missing (CORELIB_PLAN §12.2)");

        (string Name, string[] Lines)[] building =
            Jobs(docs).Where(j => Restores(j.Lines)).ToArray();
        Assert.NotEmpty(building);

        foreach ((string name, string[] job) in building)
        {
            Assert.True(
                job.Any(l => l.Contains("actions/setup-dotnet@", StringComparison.Ordinal)),
                "docs.yml: job `" + name + "` runs dotnet without setting the SDK up");
        }
    }
}
