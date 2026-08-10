/*
 * SofaBuffers C# - the devcontainer image can run the tools the repo ships (issue #69).
 *
 * CORELIB_PLAN §10 makes `bench/run_callgrind.sh` one of the three mandatory
 * benchmark tools -- the only one that is machine-independent, and the one §13's
 * conformance checklist requires to be "present and *runnable*". §11 makes the
 * `.devcontainer/` the working environment of the repo. Those two clauses meet in
 * one place: a tool is only runnable in the container if the image installs what
 * it invokes. It did not -- the Dockerfile installed `ca-certificates`, `curl`,
 * `gnupg` and `git` but not `valgrind`, so the script the README documents as the
 * way to get `Ir/op` numbers died on `valgrind: command not found` in the very
 * image the repo ships.
 *
 * These are lint-shaped tests, not behaviour tests. Rather than hard-coding
 * "valgrind", they derive the requirement from the scripts themselves: a
 * `if ! command -v <tool>` preflight that exits is a script declaring a hard
 * external dependency, and every such tool must be installed by the Dockerfile.
 * Adding a tool-dependent script, or dropping a package from the image, then
 * fails here instead of at a contributor's prompt.
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

public class DevcontainerToolchainTests
{
    /// <summary>Repository root: this file lives in tests/SofaBuffers.Tests/.</summary>
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    private static string DockerfilePath() =>
        Path.Combine(RepoRoot(), ".devcontainer", "Dockerfile");

    /// <summary>
    /// Built from a source drop rather than the repo (e.g. a packaged replay):
    /// there is nothing to lint, so the guards stand down instead of failing.
    /// </summary>
    private static bool HaveTree =>
        File.Exists(DockerfilePath()) && File.Exists(Path.Combine(RepoRoot(), "README.md"));

    /// <summary>The shell scripts the repo ships: `*.sh` at the root and under `bench/`.</summary>
    private static string[] ShellScripts()
    {
        var roots = new[] { RepoRoot(), Path.Combine(RepoRoot(), "bench") };
        return roots
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.sh"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The external tools a script hard-requires: the subjects of a negated
    /// `command -v` preflight (`if ! command -v valgrind ...; then ... exit 1`).
    /// An un-negated `command -v` is an optional extra (coverage.sh's
    /// `reportgenerator`) and is deliberately not collected.
    /// </summary>
    private static IEnumerable<string> HardRequirements(string script) =>
        Regex.Matches(script, @"!\s*command\s+-v\s+([A-Za-z0-9_.+-]+)")
            .Select(m => m.Groups[1].Value);

    /// <summary>
    /// The apt packages the Dockerfile installs. Line continuations are folded
    /// first, then every `apt-get install` run contributes its non-flag operands
    /// up to the end of that command (`&amp;&amp;`, `;`, a pipe or the line end).
    /// </summary>
    private static HashSet<string> AptPackages()
    {
        string[] raw = File.ReadAllLines(DockerfilePath());
        var logical = new List<string>();
        string current = "";
        foreach (string line in raw)
        {
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string trimmed = line.TrimEnd();
            if (trimmed.EndsWith("\\", StringComparison.Ordinal))
            {
                current += trimmed[..^1] + " ";
                continue;
            }

            logical.Add(current + trimmed);
            current = "";
        }
        if (current.Length > 0)
        {
            logical.Add(current);
        }

        var packages = new HashSet<string>(StringComparer.Ordinal);
        foreach (string command in logical)
        {
            foreach (Match install in Regex.Matches(command, @"apt-get\s+install\b"))
            {
                string tail = command[(install.Index + install.Length)..];
                foreach (string token in tail.Split(
                             new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token is "&&" or "||" or ";" or "|" || token.StartsWith(">", StringComparison.Ordinal))
                    {
                        break;
                    }
                    if (token.StartsWith("-", StringComparison.Ordinal))
                    {
                        continue;   // a flag, e.g. -y / --no-install-recommends
                    }

                    packages.Add(token.TrimEnd(';'));
                }
            }
        }

        return packages;
    }

    /// <summary>
    /// Every tool a shipped script refuses to run without is installed by the
    /// devcontainer image, so `bash bench/run_callgrind.sh` works in the
    /// environment the repo hands a contributor (CORELIB_PLAN §10, §11, §13).
    /// </summary>
    [Fact]
    public void DevcontainerInstallsEveryToolTheShippedScriptsRequire()
    {
        if (!HaveTree) return;

        HashSet<string> installed = AptPackages();
        int required = 0;

        foreach (string path in ShellScripts())
        {
            foreach (string tool in HardRequirements(File.ReadAllText(path)))
            {
                required++;
                Assert.True(
                    installed.Contains(tool),
                    Path.GetFileName(path) + " needs `" + tool +
                    "`, but .devcontainer/Dockerfile never installs it");
            }
        }

        // Guard against passing vacuously if the preflight checks are reworded.
        Assert.True(required >= 1, "no script declared a hard external tool dependency");
    }

    /// <summary>
    /// The machine-independent tool of CORELIB_PLAN §10 is the one that needs an
    /// external program; naming it explicitly keeps the derivation above honest.
    /// </summary>
    [Fact]
    public void DevcontainerInstallsValgrind()
    {
        if (!HaveTree) return;

        Assert.Contains("valgrind", File.ReadAllText(Path.Combine(RepoRoot(), "bench", "run_callgrind.sh")),
            StringComparison.Ordinal);
        Assert.Contains("valgrind", AptPackages());
    }

    /// <summary>
    /// CORELIB_PLAN §9: the README states every dependency. The Benchmarks
    /// section documents `run_callgrind.sh`, so it says what that command needs
    /// and where a ready-made environment with it comes from.
    /// </summary>
    [Fact]
    public void ReadmeBenchmarksSectionDocumentsTheValgrindDependency()
    {
        if (!HaveTree) return;

        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        int start = readme.IndexOf("## Benchmarks", StringComparison.Ordinal);
        Assert.True(start >= 0, "README has no `## Benchmarks` section");
        int next = readme.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string section = next < 0 ? readme[start..] : readme[start..next];

        Assert.Contains("valgrind", section, StringComparison.Ordinal);
        Assert.Contains(".devcontainer", section, StringComparison.Ordinal);
    }
}
