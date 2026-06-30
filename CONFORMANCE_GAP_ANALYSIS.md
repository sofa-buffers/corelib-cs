# SofaBuffers `corelib-cs` — Conformance Gap Analysis & Remediation Plan

Audit of the C# core-library port (`/workspace/corelib-cs`) against the
language-independent specification (`CORELIB_PLAN.md`), with special attention to
the **§13 Conformance Checklist**. Each checklist item is classified
**PASS / PARTIAL / GAP** with concrete file/line evidence, followed by an
actionable remediation plan for every PARTIAL/GAP.

> Method note: the audit is by source inspection. No .NET SDK is available in this
> environment (`which dotnet` → not found), so the test suite was **not executed**;
> test conformance (item 12) is assessed from the test sources and the committed
> build artifacts, not a live run.

> Scope note: this document is the **only** change introduced by the audit branch.
> No existing file was modified, renamed, or deleted.

## Spec revision

This is a **refresh** of the prior audit against an updated `CORELIB_PLAN.md`
(updated spec commit `dcb85d6`, 2026-06-30). The substantive change: **zero-length
arrays and empty sequences are now explicitly legal on the wire.**

* **§4.7** — `element_count` range is now `0 .. 2,147,483,647` (was `1..`). A
  **zero-count integer array** (unsigned or signed) is valid and fully specified as
  exactly `[ header_varint ] [ element_count_varint = 0 ]`, nothing after.
  Absent-vs-empty is now a *code-generator* concern, **not** a wire-level one.
* **§4.8** — a **zero-count fixlen array** (fp32/fp64) carries **no `fixlen_word`
  and no payload** — exactly `[ header_varint ] [ element_count_varint = 0 ]`.
* **§4.9** — an **empty sequence** (`sequence start` immediately followed by its
  `0x07` end) is legal and well-formed; a decoder **MUST** accept it.

### What changed vs previous revision

* **Item 6 (arrays §4.7–4.8): PASS → GAP.** The previous audit *praised* the port
  for rejecting empty arrays on both encode and decode ("Empty array rejected on
  encode and decode"). Under the updated spec that rejection is **non-conformant**.
  The encoder throws on zero-count arrays (`OStream.cs:322`, `431`, `453`) and the
  decoder throws `InvalidMessage` on `count == 0` (`IStream.cs:338`, `387`, `828`).
  This is now the single full GAP.
* **Item 12 (test vectors): PASS → PARTIAL.** The test suite actively asserts the
  now-non-conformant behavior (`EncoderOverloadsTests.cs:135 EmptyArraysRejected`),
  and the shared `assets/test_vectors.json` carries **no** zero-count array vector,
  so the new §4.7–4.8 rule is both contradicted and uncovered. (Empty-sequence
  vectors *are* present — see below.)
* **Item 7 (sequence framing §4.9): still PARTIAL, but for a narrower reason.** The
  newly-added "empty sequence MUST be accepted" requirement is **met** — the encoder
  emits begin/end with no non-empty restriction and the decoder accepts a
  begin-immediately-followed-by-end, with three shipped vectors covering it
  (`empty_sequence`, `nested_empty_sequences`, `empty_sequence_between_fields`,
  `assets/test_vectors.json:659–698`). Item 7 remains PARTIAL **only** because the
  normative `MAX_DEPTH = 255` cap is still unimplemented (unchanged finding).
* **Unchanged, carried forward:** item 7 MAX_DEPTH (PARTIAL), item 14 README install
  command (PARTIAL), item 18 `docs.yml` PR trigger (PARTIAL), and all 13 PASS items
  not touched by the spec delta.

## Summary

| Status | Count |
|--------|-------|
| PASS | 13 |
| PARTIAL | 4 |
| GAP | 1 |
| **Total** | **18** |

The port remains substantially conformant on the wire format, codecs, all 8 wire
types, fixlen handling, streaming encode/decode, error model, assets, benchmarks,
devcontainer, and CI/docs workflows. The spec change introduces **one full GAP**:
the port rejects zero-count arrays on both encode and decode (and the tests pin that
rejection), which the updated §4.7–4.8 now make illegal. Empty sequences (§4.9) are
already handled correctly. The three remaining PARTIALs are carried forward:
(1) `MAX_DEPTH = 255` is neither defined nor enforced; (2) the README omits the
explicit install command; (3) `docs.yml` also triggers on pull requests despite the
spec's "push to main only".

## Per-Checklist-Item Results

| # | Checklist item (§13) | Status | Evidence | Notes |
|---|----------------------|--------|----------|-------|
| 1 | All public symbols under `sofab` namespace (§6) | PASS | `src/SofaBuffers/*.cs` all `namespace sofab;`; `SofaBuffers.csproj:11` `<RootNamespace>sofab</RootNamespace>`; package id `SofaBuffers` (`.csproj:16`) | Namespace `sofab`, package `SofaBuffers` — exactly the split §6 requires. |
| 2 | API version constant/getter returns `1` (§6) | PASS | `src/SofaBuffers/Sofab.cs:24` `public const int ApiVersion = 1;` | Public, integer, value 1. |
| 3 | Varint & zig-zag encode/decode match §4.1–4.2 | PASS | `OStream.WriteVarint` (`OStream.cs:168`); `IStream.ReadVarint` (`IStream.cs:482`) & `VarintPush` (`IStream.cs:547`) with overflow guard `shift >= VALUE_BITS(64)`; `WireFormat.ZigzagEncode/Decode` (`WireFormat.cs:62,72`) | LEB128 little-endian; overflow → `InvalidMessage`. |
| 4 | Field header `(id<<3)\|type` and all 8 wire types (§4.3) | PASS | `OStream.WriteIdType` (`OStream.cs:192`); tags `T_*` 0x0–0x7 (`WireFormat.cs:22-43`); decoder dispatch `IStream.cs:202-250` & `592-635` covers all 8 | Unknown tag → `InvalidMessage`. |
| 5 | Fixlen word `(len<<3)\|subtype`, LE floats, UTF-8 no terminator, blobs (§4.6) | PASS | `OStream.WriteFixlen` (`OStream.cs:243`), `WriteFp32/64` LE (`OStream.cs:257,271`), `WriteString` via `Encoding.UTF8.GetBytes` no NUL (`OStream.cs:285`), `WriteBlob` (`OStream.cs:294`); decode `FastFixlen`/`StepFixlenLen` (`IStream.cs:254,694`); `FixlenType` 0x0–0x3 (`FixlenType.cs`) | Floats compared by bit pattern in tests; reserved subtype → `InvalidMessage`. Zero-length string/blob handled (`IStream.cs:297,737`). |
| 6 | Integer arrays + fixlen arrays with single shared fixlen word; no dynamic subtypes in fixlen arrays; **`element_count` may be `0`** (§4.7–4.8) | **GAP** | **Encode rejects zero-count:** `WriteArrayHeader` throws `Argument` "empty array" on `count <= 0` (`OStream.cs:322`); `WriteArrayFp32` (`OStream.cs:431`) / `WriteArrayFp64` (`OStream.cs:453`) throw on `length == 0`. **Decode rejects zero-count:** `FastVarintArray` (`IStream.cs:338`), `FastFixlenArray` (`IStream.cs:387`), and `StepArrayCount` (`IStream.cs:828`) all `throw InvalidMessage "array count"` when `count == 0`. The non-empty parts (single shared fixlen word `OStream.cs:435-437,457-459`; dynamic-subtype rejection `IStream.cs:438,733`) are correct. | Under updated §4.7–4.8 a zero-count array is **valid**; the port refuses it on both sides, so a conformant peer's empty array is unencodable here and undecodable here. Also, even if the `count==0` guard were lifted, the fixlen-array path would still try to read a `fixlen_word` (`IStream.cs:396`) that a zero-count fixlen array (§4.8) does **not** emit. See Remediation #1. |
| 7 | Sequence framing, fresh scope, single-byte `0x07` end, **empty sequence accepted**, skip-by-walking with depth tracking, **reject nesting beyond `MAX_DEPTH = 255`** (§4.9) | **PARTIAL** | Framing/`0x07` end (`OStream.cs:478-487`); **empty sequence supported** — begin/end carry no non-empty restriction, decoder accepts begin→end (`IStream.cs:232-247,615-632`), covered by vectors `empty_sequence`/`nested_empty_sequences`/`empty_sequence_between_fields` (`assets/test_vectors.json:659-698`); depth-balance + skip-by-walking (`IStream.cs:232-247`, tests `IStreamTests.cs:96,123`); **but** `_depth` is `ulong` capped only at `ulong.MaxValue` (`IStream.cs:82,233,616`); no `MAX_DEPTH` constant anywhere; encoder never tracks depth | Empty-sequence requirement (§4.9) is **met**. Remaining gap: the normative 255 cap is unenforced on both decode and encode; no `MAX_DEPTH` constant defined. See Remediation #2. |
| 8 | Streaming encode into smaller-than-message buffer via flush/sink + mid-stream buffer swap (§5.1) | PASS | `OStream` flush sink + `PushByte` spill (`OStream.cs:100,140`), `BufferSet` swap (`OStream.cs:117`), start offset (`OStream.cs:63`); tests encode through 1/3/7-byte buffers (`TestVectorsConformanceTests.cs:431`) | Buffer-full with no sink → `BufferFull`. |
| 9 | Streaming decode via `feed` of arbitrary chunks, push/pull, lazy field binding, auto-skip (§5.2) | PASS | `IStream.Feed` byte-resumable state machine (`IStream.cs:115`); visitor push model (`IVisitor.cs`); byte-at-a-time tests (`TestVectorsConformanceTests.cs:484`); skip via default no-op visitor (`VisitorDefaultsTests.cs`) | String/blob payloads delivered as input-buffer views (zero-copy). |
| 10 | Result/error reporting per §6.3 baseline (or idiomatic exceptions) | PASS | `SofabError {Argument, Usage, BufferFull, InvalidMessage}` (`SofabError.cs`); `SofabException : IOException` carrying `.Error` (`SofabException.cs`); success = normal return | Idiomatic C# exceptions, allowed by §6.3. Naming adapted (`Argument`≈`InvalidArgument`, `Usage`≈`UsageError`). `Usage` is defined but not raised by the core (no typed-read path) — acceptable. |
| 11 | Streaming primitives sufficient for a thin generated-object layer that also streams; one-shot helpers as thin wrappers (§6.1) | PASS | Encode hooks: buffer+`FlushSink`+`BufferSet` (`OStream.cs`); decode hooks: `Feed`+`IVisitor` incl. `SequenceBegin/End` for nested descent (`IVisitor.cs:107,112`); layering rationale in `README.md:261-269` | Primitives are sufficient; the generated-object layer itself lives in the `generator` repo (per §6.1), so no `serialize()/deserialize()` helper is expected in the corelib. |
| 12 | All shared test vectors pass encode+decode, plus chunked, roundtrip, malformed, skip (§7) | **PARTIAL** | All 67 shipped vectors in `assets/test_vectors.json` replayed for encode/chunked-encode/decode/byte-by-byte/roundtrip/skip (`TestVectorsConformanceTests.cs`), incl. the three empty-sequence vectors; malformed branches (`DecoderErrorsTests.cs`, `IStreamTests.cs:119-141`). **However:** `EncoderOverloadsTests.cs:135 EmptyArraysRejected` asserts the now-**non-conformant** zero-count rejection, and `assets/test_vectors.json` has **no** zero-count integer/fixlen array vector (no `count: 0`) | The suite both contradicts (`EmptyArraysRejected`) and fails to cover the updated §4.7–4.8 zero-count rule. Empty-sequence coverage is good. No malformed test exercises `MAX_DEPTH` (cap unimplemented — see #2). Assessed by inspection (no SDK to run). See Remediation #1. |
| 13 | `assets/` populated — branding + `test_vectors.json` (§8) | PASS | `assets/sofabuffers_logo.png`, `assets/sofabuffers_icon.png`, `assets/test_vectors.json` (format/version/vectors keys present, 67 vectors) | README header references `assets/sofabuffers_logo.png`. The shared vectors are generated by `corelib-c-cpp`; the missing zero-count array vectors (item 12) must be sourced from there, not hand-written here. |
| 14 | README follows family format with badges and required sections (§9) | **PARTIAL** | Header/tagline/logo (`README.md:1-8`); CI+coverage+branches+Docs badges (`README.md:12-15`); `## Why this design`, `## Usage` (2 examples), `## API summary`, `## Feature flags`, `## Build & test`, `## Benchmarks` all present | No explicit **install command** (e.g. `dotnet add package SofaBuffers`); only the package id is mentioned and "Targets .NET 9" (`README.md:39`). §9 item 2 requires the install command and minimum-version statement. See Remediation #3. |
| 15 | `perf` (CPU-independent) and `bench` (MB/s) tools present and runnable (§10) | PASS | `bench/SofaBuffers.Bench/` with `Perf.cs`, `Bench.cs`, `Callgrind.cs`, dispatched by `Program.cs:36-53` (`perf`/`bench`/`all`/named Callgrind workloads) | CPU-independent figure via Callgrind instruction counting (no portable cycle counter on .NET, mirroring the Java tool). |
| 16 | `.devcontainer/` with all required files + extensions incl. `anthropic.claude-code`; `.env` gitignored (§11) | PASS | `Dockerfile`, `build.sh`, `start.sh`, `attach.sh`, `devcontainer.json`, `.env.example` all present; container name `cs-devcontainer` (`build.sh:6`, `start.sh:17`, `attach.sh:4`); extensions incl. `anthropic.claude-code` (`devcontainer.json:11`); `.env` ignored by root `.gitignore:2` and `.devcontainer/.gitignore` | `.env` is effectively gitignored (`git ls-files` shows only `.env.example` tracked), but the **literal** `.devcontainer/.env` line §11.2 calls out is not present — covered by the broader `.env` pattern. Minor; noted, not a gap. |
| 17 | `ci.yml` builds & tests on push and PR; matrix where it matters; coverage uploaded + badge in README (§12.1) | PASS | `ci.yml` triggers on `push: [main]` + `pull_request` (`ci.yml:3-7`); `fail-fast: false` (`ci.yml:25`); coverage via Coverlet + `coverlet.runsettings`, badges published to `badges` branch consumed by README (`ci.yml:46-108`) | Single .NET version is acceptable for C# (§12.1). Release built in `build-test`; Debug exercised implicitly by the coverage job. §12.1's explicit "build in both debug and release" is met only across jobs — noted, not a gap. |
| 18 | `docs.yml` generates HTML docs and publishes to Pages via Actions deploy (no `gh-pages`); Docs badge links to the site (§12.2) | **PARTIAL** | DocFX build (`docs.yml:36-46`), `upload-pages-artifact@v3` + `deploy-pages@v4` with `pages: write`/`id-token: write` (`docs.yml:10-13,48-65`), deploy gated to push-on-main; Docs badge → `https://sofa-buffers.github.io/corelib-cs/` (`README.md:15`) | Deployment mechanism is fully correct, but the workflow **also triggers on `pull_request`** (`docs.yml:6-7`), whereas §12.2 says "Runs on push to main only (not on pull requests)". See Remediation #4. |

## Remediation Plan

Ordered by severity.

### 1. Permit zero-count arrays on encode and decode, and cover them in tests (item 6, item 12) — *High (GAP)*

**Problem.** Updated §4.7 makes `element_count == 0` a valid, fully-specified empty
array (`[ header ] [ count=0 ]`), and §4.8 specifies a zero-count fixlen array as
`[ header ] [ count=0 ]` with **no `fixlen_word` and no payload**. The port refuses
empty arrays on **both** sides:

* Encode: `WriteArrayHeader` throws `Argument` "empty array" on `count <= 0`
  (`OStream.cs:322`); `WriteArrayFp32` (`OStream.cs:431`) and `WriteArrayFp64`
  (`OStream.cs:453`) throw on `length == 0`.
* Decode: `FastVarintArray` (`IStream.cs:338`), `FastFixlenArray` (`IStream.cs:387`),
  and the byte-machine `StepArrayCount` (`IStream.cs:828`) all
  `throw InvalidMessage "array count"` when `count == 0`.
* Tests pin the wrong behavior: `EncoderOverloadsTests.cs:135 EmptyArraysRejected`,
  and no zero-count array vector exists in `assets/test_vectors.json`.

**Fix.**
- **Encoder:** drop the `count <= 0` rejection in `WriteArrayHeader`
  (`OStream.cs:320-328`) and the `length == 0` rejections in `WriteArrayFp32` /
  `WriteArrayFp64`. For a zero-count **integer** array, emit `[ header ] [ count=0 ]`
  and stop. For a zero-count **fixlen** array, emit `[ header ] [ count=0 ]` and
  **do not** emit the `fixlen_word` (move the `WriteVarint(fixlen_word)` after a
  `if (data.Length == 0) return;` guard, per §4.8).
- **Decoder:** in `FastVarintArray`, `FastFixlenArray`, and `StepArrayCount`, change
  the guard to reject only `count > ARRAY_MAX` (not `count == 0`). On `count == 0`,
  call `visitor.ArrayBegin(id, kind, 0)` and return to idle **without** reading any
  elements. For `FastFixlenArray`/`Fixlen` specifically, return **before** reading
  the `fixlen_word` (it is absent for zero-count, §4.8).
- **Tests:** invert/remove `EmptyArraysRejected`; add zero-count encode+decode
  roundtrip tests for unsigned, signed, and fixlen (fp32/fp64) arrays; refresh
  `assets/test_vectors.json` from `corelib-c-cpp` once it ships zero-count array
  vectors and assert they pass.

**Files.** `src/SofaBuffers/OStream.cs`, `src/SofaBuffers/IStream.cs`,
`tests/SofaBuffers.Tests/EncoderOverloadsTests.cs`,
`tests/SofaBuffers.Tests/TestVectorsConformanceTests.cs` (+ `assets/test_vectors.json`).

**Acceptance criteria.**
- Encoding a zero-count unsigned/signed array yields exactly `[ header ] [ 0x00 ]`.
- Encoding a zero-count fp32/fp64 array yields exactly `[ header ] [ 0x00 ]` (no
  `fixlen_word`, no payload).
- Decoding each of those byte sequences emits `ArrayBegin(id, kind, 0)`, no element
  callbacks, and the decoder resumes correctly on the following field.
- No test asserts that an empty array is rejected.

### 2. Enforce `MAX_DEPTH = 255` on decode and encode (item 7) — *Medium*

**Problem.** §4.9 and §6.2 make `MAX_DEPTH = 255` normative: a decoder must reject a
message nesting deeper than 255 with `InvalidMessage`, and an encoder must not open
more than 255 nested sequences. The port defines **no** `MAX_DEPTH` constant and uses
a `ulong _depth` bounded only at `ulong.MaxValue` (`IStream.cs:82,233,616`), so it
accepts arbitrarily deep nesting. The encoder (`OStream.WriteSequenceBegin`,
`OStream.cs:478`) tracks no depth at all. The C# decoder is iterative, so this is not
a stack-overflow risk, but it is a conformance divergence and leaves the cap untested.
(Carried forward unchanged from the prior audit.)

**Fix.**
- Add `internal const int MAX_DEPTH = 255;` to `WireFormat.cs` alongside `ID_MAX`/`ARRAY_MAX`.
- In `IStream`, on every sequence-start (`FastField` `T_SEQUENCE_START` at `IStream.cs:232`
  and `StepIdle` at `IStream.cs:615`), throw `SofabException(SofabError.InvalidMessage, "sequence too deep")`
  when `_depth >= MAX_DEPTH` *before* incrementing.
- In `OStream`, add a depth counter incremented in `WriteSequenceBegin` / decremented in
  `WriteSequenceEnd`, throwing `SofabException(SofabError.Argument)` (or `InvalidMessage`)
  when a 256th level would open.

**Files.** `src/SofaBuffers/WireFormat.cs`, `src/SofaBuffers/IStream.cs`,
`src/SofaBuffers/OStream.cs`, plus tests in `tests/SofaBuffers.Tests/DecoderErrorsTests.cs`
and `tests/SofaBuffers.Tests/EncoderOverloadsTests.cs`.

**Acceptance criteria.**
- A message with 256 nested `sequence_begin`s decodes to `InvalidMessage`; 255 succeeds.
- Encoding a 256th nested sequence raises the documented error; 255 succeeds.
- `MAX_DEPTH` is a named constant; a regression test pins the 255/256 boundary on both sides.

### 3. Add the install command (and tighten version requirements) to the README (item 14) — *Low*

**Problem.** §9 item 2 requires the README to state the **install command** and the
minimum required toolchain/dependency versions. The README names the package id and
says "Targets .NET 9" (`README.md:39`) but never shows how to install it.

**Fix.** In the `## SofaBuffers C# library` section add an install snippet
(`dotnet add package SofaBuffers`) and an explicit minimum-version line
(".NET SDK 9.0 or later; no runtime dependencies").

**Files.** `README.md`.

**Acceptance criteria.** README contains a copy-pasteable `dotnet add package SofaBuffers`
command and an explicit minimum-.NET-version statement in the overview section.

### 4. Restrict `docs.yml` to push-on-main (item 18) — *Low*

**Problem.** §12.2 states the docs workflow "Runs on push to main only (not on pull
requests)." `docs.yml:6-7` adds a `pull_request` trigger. Deployment is correctly gated
to push-on-main, so no PR ever deploys, but the build job still runs on PRs, contrary to
the spec.

**Fix.** Remove the `pull_request:` trigger from `docs.yml` (keep `push: branches:[main]`
and optionally `workflow_dispatch`). If PR-time doc-build validation is desired, document
it as an intentional deviation instead.

**Files.** `.github/workflows/docs.yml`.

**Acceptance criteria.** `docs.yml` triggers only on push to `main` (plus optional manual
dispatch); no `pull_request` trigger remains.

---

### Minor observations (PASS-with-note; no action strictly required)

- **`.devcontainer/.env` gitignore (item 16):** the file is effectively ignored via the
  broader `.env` patterns, but the literal `.devcontainer/.env` entry §11.2 calls out is
  absent. Adding it would make the mandated intent explicit.
- **CI debug build (item 17):** Release is built in `build-test`; Debug is only exercised
  through the coverage job's default-config `dotnet test`. Adding an explicit
  `-c Debug` build step would match §12.1's "both debug and release" literally.
- **UTF-8 validation:** the core delivers string bytes as a zero-copy view and defers
  UTF-8 validation to the visitor (`Encoding.UTF8.GetString`), so it never raises
  `InvalidMessage` for malformed UTF-8. This is consistent with the borrowed-view
  streaming design and the spec's idiomatic latitude; flagged only for awareness.
