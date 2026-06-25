<p align="center"><img src="assets/sofabuffers_logo.png" alt="SofaBuffers Logo" height="140"></p>

# SofaBuffers

<b>Structured Objects For Anyone</b><br>
<i>... so optimized, feels amazing.</i>

[Would you like to know more?](https://github.com/sofa-buffers)

## SofaBuffers C# library

[![CI](https://github.com/sofa-buffers/corelib-cs/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/sofa-buffers/corelib-cs/actions/workflows/ci.yml)
[![Coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fsofa-buffers%2Fcorelib-cs%2Fbadges%2Fcoverage.json)](https://github.com/sofa-buffers/corelib-cs/actions/workflows/ci.yml)
[![Branches](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fsofa-buffers%2Fcorelib-cs%2Fbadges%2Fbranches.json)](https://github.com/sofa-buffers/corelib-cs/actions/workflows/ci.yml)
[![Docs](https://img.shields.io/badge/docs-API-blue)](https://sofa-buffers.github.io/corelib-cs/)

[GitHub repository](https://github.com/sofa-buffers/corelib-cs)

A **dependency-free**, **allocation-light**, **streaming** C# implementation of
the SofaBuffers (*Sofab*) serialization format. It is the **runtime stream core**
(equivalent to the C `corelib`'s `istream` / `ostream`), a port of the C
`corelib` (`istream.c` / `ostream.c`) that runs anywhere .NET does — from
desktops and servers to containers in the cloud.

Like protobuf's `CodedInputStream` / `CodedOutputStream`, this library is meant
to be driven by **generated code**: a schema-driven generator emits one class per
message plus marshal / unmarshal methods that call the primitives here. The
decoder uses the **visitor pattern**, so a generated message is typically a single
`switch` over the field id.

The wire format is specified, language-neutrally, in the
[SofaBuffers documentation](https://github.com/sofa-buffers/documentation). The
unit tests here use the exact byte vectors from the
[C corelib](https://github.com/sofa-buffers/corelib-c-cpp)'s reference suite
(`test/c/test_ostream.c`) to guarantee byte-for-byte interoperability with the C,
C++, Rust, Java and Go implementations.

NuGet package id: `SofaBuffers` · namespace `SofaBuffers`. Targets .NET 9 (`net9.0`).

## Why this design

| Goal | How |
|------|-----|
| No per-field allocation | All state lives in caller-provided buffers and small `OStream` / `IStream` objects. Scalars stay primitive (`ulong` / `double`) — no boxing, nothing escapes to the heap on the hot path. |
| No reflection, no runtime codegen | Pure method calls; the decoder pushes to an `IVisitor` interface rather than reflecting over fields. Suitable for AOT / Native AOT and trimmed, locked-down runtimes. |
| Streaming **out** | `OStream` writes into a small caller buffer and invokes a `FlushSink` (a delegate, e.g. `stream.Write`) whenever it fills, so a message can exceed the buffer — and even RAM. |
| Streaming **in** | `IStream` is a byte-at-a-time state machine fed arbitrary chunks; large string / blob payloads are delivered in pieces to your `IVisitor`. |
| Reserve-offset | `new OStream(buf, offset)` leaves room at the front of the buffer for a lower-layer protocol header (saves a copy). |
| Explicit endianness | IEEE-754 values are written / read little-endian with explicit bit shifts, so behaviour is identical on every runtime. |
| Generated-code friendly | `IVisitor` has a default no-op for every field kind (a C# default-interface method), so generated (and hand-written) sinks override only what they need and ignore the rest. |

## Source documentation

[Documentation](https://sofa-buffers.github.io/corelib-cs/) — DocFX HTML for the
`SofaBuffers` namespace, generated from the XML doc comments and published to
GitHub Pages on every push to `main`. Build / preview it locally:

```bash
dotnet tool install -g docfx        # one-time
docfx docs/docfx.json               # output under docs/_site
docfx docs/docfx.json --serve       # build and serve at http://localhost:8080
```

## Usage

```csharp
using SofaBuffers;

// ---- encode (fixed buffer, no per-write allocation) ----
byte[] buf = new byte[64];
var os = new OStream(buf);
os.WriteUnsigned(1, 42);
os.WriteSigned(2, -7);
os.WriteString(3, "hi");
int used = os.BytesUsed;

// ---- decode (push to your visitor) ----
class My : IVisitor
{
    public ulong A;
    public long B;
    public void Unsigned(int id, ulong v) { if (id == 1) A = v; }
    public void Signed(int id, long v)    { if (id == 2) B = v; }
    // Fp32(), Fp64(), String(), Blob(), ArrayBegin(), SequenceBegin(), ... as needed
}

var sink = new My();
new IStream().Feed(buf, 0, used, sink);
```

Encoder and decoder report problems through `SofabException` (which extends
`IOException`, so it composes with .NET I/O and a flush sink that does real I/O);
the specific cause is available via `SofabException.Error`.

### Streaming a message larger than the buffer

```csharp
using System.IO;
using SofaBuffers;

byte[] scratch = new byte[16];                 // tiny buffer
using var outStream = new MemoryStream();      // or a socket / file
FlushSink sink = outStream.Write;              // (data, offset, length)
var os = new OStream(scratch, 0, sink);
for (int i = 0; i < 1000; i++)
{
    os.WriteUnsigned(i, (ulong)i);
}
os.Flush();                                    // push the tail
```

### Reading a large payload in chunks

An `IVisitor` receives string / blob payloads as one or more chunks, each
carrying the field `total` length and the byte `offset` of the chunk, so the
payload need never be held in one piece:

```csharp
class BlobSink : IVisitor
{
    public void Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
        // append data[chunkOffset .. chunkOffset+chunkLength) to your sink
    }
}
new IStream().Feed(buf, 0, used, new BlobSink());
```

## API summary

**`OStream`** (encoder) — `WriteUnsigned`, `WriteSigned`, `WriteBoolean`,
`WriteFp32`, `WriteFp64`, `WriteString`, `WriteBlob`, `WriteFixlen`;
`WriteArrayUnsigned` / `WriteArraySigned` (overloaded for 8/16/32/64-bit element
types), `WriteArrayFp32`, `WriteArrayFp64`; `WriteSequenceBegin` /
`WriteSequenceEnd`; `Flush`, `BufferSet`, `BytesUsed`.

**`IStream`** (decoder) — `Feed(data[, off, len], visitor)`, fed in arbitrarily
small chunks.

**`IVisitor`** (decoder sink, every method a default no-op) — `Unsigned`,
`Signed`, `Fp32`, `Fp64`, `String`, `Blob`, `ArrayBegin`, `SequenceBegin`,
`SequenceEnd`. Override only what you need; unhandled fields are skipped.

**Supporting types** — `FixlenType`, `ArrayKind`, `SofabError`,
`SofabException` (`: IOException`), `FlushSink` (delegate), and
`Sofab.ApiVersion` (`== 1`).

## Format coverage

The C# build always includes the full format — unsigned / signed varints,
`fp32` / `fp64`, strings, blobs, arrays and nested sequences — because the
desktop and cloud targets it is built for are not code-size constrained. The C
library's compile-time `SOFAB_DISABLE_*` switches (which strip whole code paths
for tiny microcontrollers) therefore have no C# equivalent. The value type is
64-bit (`ulong` / `long`), matching the C default configuration so the wire image
and varint lengths are identical.

## Layering vs. the C library

| C file | C# type | Status |
|--------|---------|--------|
| `sofab.h` (types / constants) | `SofabError`, `FixlenType`, `ArrayKind`, `WireFormat` | ported |
| `ostream.c` | `OStream` (+ `FlushSink`) | ported |
| `istream.c` | `IStream` + `IVisitor` | ported (push / visitor model instead of bind-target callbacks) |
| `object.c` (descriptor transcoder) | — | not ported. The idiomatic C# equivalent is generated message classes — a schema-driven generator emitting `IVisitor` / encode glue; the streaming core above already covers serialize / deserialize. |

## Build & test

```bash
dotnet build SofaBuffers.sln -c Release     # build the library, tests and benchmarks
dotnet test  SofaBuffers.sln                # run the xUnit suite
```

Requires the .NET SDK 9. The `.devcontainer/` here builds a ready-to-use image
(`./.devcontainer/start.sh`) with the SDK and tooling preinstalled.

## Benchmarks

Two standalone tools mirror the C / C++ / Rust / Java benchmarks so the
implementations can be compared directly:

```bash
# perf -- per-op cost: a CPU-speed-independent figure (cycles/op where the
#         runtime exposes a cycle counter) plus throughput MB/s.
dotnet run -c Release --project bench/SofaBuffers.Bench -- perf

# bench -- a throughput table in MB/s for encode/decode of a 1000-element u64
#          array and a small "typical" mixed message. MB = 1e6 bytes.
dotnet run -c Release --project bench/SofaBuffers.Bench -- bench
```

`perf` and `bench` encode the identical message (same field ids, types and
values) as their C/C++/Rust/Java counterparts and print the same report layout.
The managed .NET runtime exposes no portable hardware cycle counter, so — like
the Java tool — `perf` reports `cycles/op` as unavailable and uses **CPU time/op**
(process CPU time, clock-independent) as the code-cost proxy, alongside the
machine-dependent MB/s figure.

For a fully CPU-speed-independent number, name a workload directly to run it
**once** under a profiler (Callgrind instruction counting, the spec's second
acceptable technique for runtimes without a cycle counter):

```bash
# one encode of the 1000-element u64 array, setup excluded:
valgrind --tool=callgrind --collect-atstart=no \
  --toggle-collect='*Callgrind.OpEncodeU64Array*' \
  dotnet bench/SofaBuffers.Bench/bin/Release/net9.0/SofaBuffers.Bench.dll encode_u64_array
# workloads: encode_u64_array, decode_u64_array, encode_typical, decode_typical
```

## Testing & coverage

```bash
dotnet test SofaBuffers.sln                 # unit + integration tests
./coverage.sh                               # coverlet: Cobertura + terminal summary (+ HTML if ReportGenerator is installed)
```

Tests live in `tests/SofaBuffers.Tests/` as focused suites:

- `TestVectorsConformanceTests.cs` — the shared, language-agnostic conformance
  suite (`assets/test_vectors.json`, copied verbatim from the documentation
  repo): every vector replayed for encode (byte-exact), decode (field match) and
  byte-at-a-time chunked decode
- `OStreamTests.cs` — encoder, byte-exact vs. the C reference vectors
- `IStreamTests.cs` — decoder over the same vectors + malformed-input errors + byte-at-a-time feeding
- `DecoderErrorsTests.cs` — every malformed-input rejection branch
- `EncoderOverloadsTests.cs` — every `OStream` writer overload + argument validation
- `RoundTripTests.cs` — encode → decode value preservation (scalars, arrays, strings/blobs, sequences)
- `ApiTests.cs` — offset reserve, flush-sink streaming larger than the buffer, chunked decode
- `StreamingEdgeTests.cs` — empty string/blob fields, multi-byte array counts
- `VisitorDefaultsTests.cs` — a no-op `IVisitor` silently drops every field kind
- `Common/RecordingVisitor.cs` — shared recording `IVisitor`

Current coverage: **~98% lines / ~94% branches** (coverlet, library only).
The CI workflow ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) builds,
tests and measures coverage, then publishes the badge JSON consumed above.
