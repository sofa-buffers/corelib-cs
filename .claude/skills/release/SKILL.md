---
name: release
description: Cut a release of the SofaBuffers C# corelib — bump the package version, merge the release PR, tag main, and publish the GitHub release. Use when the user asks to release, tag or bump the version of this library.
argument-hint: "<X.Y.Z>"
disable-model-invocation: true
---

# Release SofaBuffers.Corelib

Target version: `$ARGUMENTS` (bare `X.Y.Z`, no leading `v`). If it is empty, propose
one from the rules below and ask before doing anything.

## What a release is here

- **The git tag `vX.Y.Z` is the source of truth.** Everything else is brought in line
  with it *before* the tag is set.
- **The only version in the tree is `<Version>` in `src/SofaBuffers/SofaBuffers.csproj`.**
  Nothing else carries the package version — no `Directory.Build.props`, no
  `AssemblyInfo`, no version in README, `docs/docfx.json` or a changelog file.
  Tests and bench are `IsPackable=false` and have no version.
  - Do **not** touch `Sofab.ApiVersion` (`src/SofaBuffers/Sofab.cs`): that is the
    format's API version, fixed at `1` by CORELIB_PLAN §6, not the package version.
  - Re-check this with
    `rg -n '<(Version|VersionPrefix|PackageVersion|AssemblyVersion|FileVersion)>' --glob '*.{csproj,props,targets}'`
    — if another hit appears, it must be bumped too.
- **`.github/workflows/version-consistency.yml`** runs on every pushed `v*` tag and
  fails if the csproj `<Version>` differs from the tag. It is the gate, not a
  substitute for bumping first.
- **Nothing is published to NuGet.** There is no pack/push workflow and
  `SofaBuffers.Corelib` is not on nuget.org; a release is the tag plus the GitHub
  release. Don't push a package unless the user asks for it explicitly.
- **The version is family-wide.** All SofaBuffers corelibs (c-cpp, java, go, rs, ts,
  dart, …) and `sofabgen` release under the same number, usually for one spec change.
  Ask the user which version the family is moving to if it isn't obvious.

## Choosing the number (pre-1.0)

- **Minor** (`0.10.0 → 0.11.0`): anything that breaks public API or changes wire
  output — this is the pre-1.0 rule both earlier releases cite.
- **Patch** (`0.10.0 → 0.10.1`): fixes and additions with neither of those effects.
- Summarize what changed since the last tag to decide:
  `git log --oneline $(git describe --tags --abbrev=0)..origin/main`.

## Steps

1. **Preflight** — stop and report if any of these fail:
   - `git checkout main && git pull -p`; working tree clean.
   - `git tag -l vX.Y.Z` and `git ls-remote --tags origin vX.Y.Z` are both empty.
   - Latest `CI` and `Shared vectors` runs on `main` are green:
     `gh run list --branch main --limit 10`. A red `Shared vectors` means
     `assets/test_vectors.json` lags corelib-c-cpp — refresh it in its own PR first.
   - Local build/test passes:
     `dotnet build SofaBuffers.sln -c Release && dotnet test SofaBuffers.sln -c Release --no-build`.

2. **Bump on a release branch**
   ```sh
   git checkout -b release/vX.Y.Z
   sed -i 's|<Version>[^<]*</Version>|<Version>X.Y.Z</Version>|' src/SofaBuffers/SofaBuffers.csproj
   git diff   # exactly one line changed
   ```
   Commit as `chore(release): X.Y.Z`. The body says the tag is the source of truth
   and this brings the package manifest in line with the `vX.Y.Z` tag that follows,
   then names what is **breaking since the previous tag** (spec section / finding
   ids, e.g. `CORELIB_PLAN §4.8`, `Crucible F-0042`) and why that means a minor bump.

3. **PR and merge** — push the branch, open a PR titled `chore(release): X.Y.Z` with
   the same content ("Version bump only: …", then what's breaking). Merge only after
   CI is green **and the user has approved the merge**.

4. **Tag the merge commit on main** (annotated, message = release title and summary):
   ```sh
   git checkout main && git pull -p
   git log -1 --format='%h %s'   # must be the release PR's merge commit
   git tag -a vX.Y.Z -m "SofaBuffers corelib X.Y.Z" -m "<summary of breaking changes>"
   git push origin vX.Y.Z
   ```
   Confirm with the user before pushing the tag — a pushed tag is public and should
   never be moved or deleted afterwards. If something is wrong, fix it in `X.Y.(Z+1)`.

5. **Check the gate**: `gh run list --workflow version-consistency.yml --limit 1`
   must end in `success` for the tag.

6. **GitHub release** (after confirmation):
   ```sh
   gh release create vX.Y.Z --verify-tag --title vX.Y.Z --notes-file <notes.md>
   ```
   Notes follow the v0.10.0 release: one line that this aligns the library with the
   SofaBuffers family at **X.Y.Z** and the tag is the source of truth, then a
   **Breaking since vPREV** list (spec section / finding id, what changed in the C#
   API, why a consumer cares), then whether `sofabgen` changed in lockstep.
   `--verify-tag` stops `gh` from creating a lightweight tag on its own.

7. **Tidy up**: delete `release/vX.Y.Z` locally and on the remote if the merge
   didn't, and report the tag, the release URL and the gate result.
