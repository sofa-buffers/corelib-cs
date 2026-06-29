<p align="center"><img src="assets/sofabuffers_logo.png" alt="SofaBuffers" height="140"></p>

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

NuGet package id: `SofaBuffers` · namespace `sofab` (fixed by the format spec, as
in the C++ `namespace sofab`). Targets .NET 9 (`net9.0`).

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

## Usage

```csharp
using sofab;

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
using sofab;

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

### Write operations (`OStream`, encoder)

| Method | Writes |
|--------|--------|
| `WriteUnsigned(int id, ulong value)` | unsigned varint field |
| `WriteSigned(int id, long value)` | signed (ZigZag) varint field |
| `WriteBoolean(int id, bool value)` | unsigned `0` / `1` |
| `WriteFp32(int id, float value)` | 32-bit IEEE-754 float (little-endian) |
| `WriteFp64(int id, double value)` | 64-bit IEEE-754 double (little-endian) |
| `WriteString(int id, string text)` | UTF-8 string field (no NUL on the wire) |
| `WriteBlob(int id, byte[] data)` / `WriteBlob(int id, byte[] data, int from, int length)` | raw-byte blob field (whole array or a slice) |
| `WriteFixlen(int id, byte[] data, int from, int length, FixlenType subtype)` | low-level fixlen field (the primitive the float / string / blob writers build on) |
| `WriteArrayUnsigned(int id, …)` | unsigned array — overloaded for `byte[]`, `ushort[]`, `uint[]`, `ulong[]` (u8/u16/u32/u64) |
| `WriteArraySigned(int id, …)` | signed array — overloaded for `sbyte[]`, `short[]`, `int[]`, `long[]` (i8/i16/i32/i64) |
| `WriteArrayFp32(int id, float[] data)` / `WriteArrayFp64(int id, double[] data)` | fixlen (float) array |
| `WriteSequenceBegin(int id)` / `WriteSequenceEnd()` | open / close a nested id scope |
| `Flush()` → `int` | push pending bytes to the sink; returns the count that was pending |
| `BufferSet(byte[] buffer, int offset)` | swap in a new caller buffer (typically from inside a flush sink) |
| `BytesUsed` → `int` | bytes written to the active buffer since the last flush |

### Read operations (`IStream` + `IVisitor`, decoder)

The decoder is **pull on the wire, push to the caller**: you `Feed` bytes and the
`IStream` calls back into your `IVisitor` once per decoded value. There is no
per-field "read into this destination" call and no explicit `Skip` — a visitor
simply leaves a callback at its default no-op to ignore (skip) that field kind.

**`IStream`** — `Feed(byte[] data, IVisitor visitor)` and
`Feed(byte[] data, int off, int len, IVisitor visitor)`. May be fed in
arbitrarily small chunks; all parse state lives in the `IStream`, so a field
(even a string / blob payload) can straddle any number of `Feed` calls.

**`IVisitor`** — the read surface; every method is a default no-op, so you
override only the kinds you consume:

| Callback | Hands the caller |
|----------|------------------|
| `Unsigned(int id, ulong value)` | a `ulong`, by value (also each unsigned array element) |
| `Signed(int id, long value)` | a `long`, by value (also each signed array element) |
| `Fp32(int id, float value)` | a `float`, by value (also each `fp32` array element) |
| `Fp64(int id, double value)` | a `double`, by value (also each `fp64` array element) |
| `String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)` | a chunk of UTF-8 bytes **viewing the input buffer** (`data[chunkOffset .. chunkOffset+chunkLength)`); `total` is the full field length, `offset` the chunk's position within the field |
| `Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)` | a chunk of raw bytes, same view-into-input contract as `String` |
| `ArrayBegin(int id, ArrayKind kind, int count)` | the announcement that `count` elements of `kind` follow through the scalar / float callbacks with the same `id` |
| `SequenceBegin(int id)` / `SequenceEnd()` | entry / exit of a nested id scope |

A string / blob field is delivered as **one chunk when it is fully present** in
the fed buffer, and as **several chunks** when it is split across `Feed` calls;
each chunk always points into the buffer of the `Feed` that produced it. An empty
string / blob is reported as a single call with `total == 0` and
`chunkLength == 0`.

### Allowed types

| Field | Encoder | Decoder | Notes |
|-------|---------|---------|-------|
| unsigned int | `WriteUnsigned` (`ulong`) | `Unsigned` (`ulong`) | the 64-bit scalar holds u8..u64 |
| signed int | `WriteSigned` (`long`) | `Signed` (`long`) | ZigZag; holds i8..i64 |
| bool | `WriteBoolean` | `Unsigned` (`0` / `1`) | bool is just an unsigned `0`/`1` on the wire |
| fp32 / fp64 | `WriteFp32` / `WriteFp64` | `Fp32` / `Fp64` | `float` = fp32, `double` = fp64 |
| string | `WriteString` | `String` | UTF-8, no terminator |
| blob | `WriteBlob` | `Blob` | arbitrary bytes |
| unsigned array | `WriteArrayUnsigned` ×4 widths | `ArrayBegin(Unsigned)` + `Unsigned` per element | element widths u8/u16/u32/u64 |
| signed array | `WriteArraySigned` ×4 widths | `ArrayBegin(Signed)` + `Signed` per element | element widths i8/i16/i32/i64 |
| fixlen (float) array | `WriteArrayFp32` / `WriteArrayFp64` | `ArrayBegin(Fixlen)` + `Fp32` / `Fp64` per element | only `fp32` / `fp64` are valid fixlen-array elements |

**Disallowed:** dynamic-length subtypes (`string`, `blob`) as **fixlen-array
elements** — the encoder offers no such overload and the decoder rejects them
with `SofabError.InvalidMessage` ("dynamic fixlen array element"). Empty arrays
are rejected on both sides (`SofabError.Argument` / `InvalidMessage`); a `0` array
count is never legal.

### Memory handling

The library never owns a growable buffer: the encoder writes into a fixed,
caller-provided array, and the decoder hands back values either by value or as a
**view into the caller's own input buffer** — there is no internal copy of a
string / blob payload and no heap allocation per field.

**Encode (`OStream`).** The caller owns the output `byte[]`; `OStream` writes
straight into it and **never allocates or grows it**. The buffer is fixed-size:

- When it fills with **no** `FlushSink`, the next write throws
  `SofabError.BufferFull`.
- When it fills **with** a `FlushSink`, the full buffer is handed to the sink and
  writing resumes at the *start* of the same array — so a message can exceed the
  buffer (and even RAM). `Flush()` pushes the tail; `BufferSet` swaps in a fresh
  caller array (e.g. double-buffering from inside the sink). The sink's `byte[]`
  is the encoder's live buffer, reused after the call returns, so a sink that
  retains bytes must copy them.

Per-write allocation is avoided for every scalar, float, blob and array (each
element is varint- or little-endian-encoded directly into the buffer). The one
transient allocation is in `WriteString`, which calls `Encoding.UTF8.GetBytes` to
produce the UTF-8 bytes; encode pre-encoded UTF-8 through `WriteBlob` /
`WriteFixlen` to avoid even that.

**Decode (`IStream`).** Reads are **copy-vs-view by kind**:

- **Scalars / floats** are decoded into primitives (`ulong` / `long` / `float` /
  `double`) and passed **by value** — no boxing, no allocation.
- **String / blob** payloads are handed as `(byte[] data, int chunkOffset, int
  chunkLength)` pointing **directly into the buffer you passed to `Feed`** —
  zero-copy, valid only for the duration of the callback. The library never
  allocates a `string` or copies the bytes; a visitor that wants a `string` calls
  `Encoding.UTF8.GetString(data, chunkOffset, chunkLength)` itself, and one that
  retains bytes must copy the chunk.
- **Arrays** allocate nothing: `ArrayBegin` reports the count and the elements
  arrive through the scalar / float callbacks, so the caller can stream them into
  a destination of its choosing.

The decoder's only internal scratch is a fixed **8-byte** accumulator used solely
to reassemble a single `fp32` / `fp64` value (or a varint) that straddles a
`Feed` boundary; it is never used for string / blob bytes, which always view the
input directly.

### Supporting types

`FixlenType` (`Fp32`, `Fp64`, `String`, `Blob`), `ArrayKind` (`Unsigned`,
`Signed`, `Fixlen`), `SofabError` (`Argument`, `Usage`, `BufferFull`,
`InvalidMessage`), `SofabException` (`: IOException`, carries `.Error`),
`FlushSink` (delegate `(byte[] data, int offset, int length)`), and
`Sofab.ApiVersion` (`== 1`).

## Feature flags

Unlike the C library's compile-time `SOFAB_DISABLE_*` switches (which strip whole
code paths for tiny microcontrollers), the C# build always ships the **full**
format — there are no build toggles, because the desktop and cloud targets it is
built for are not code-size constrained.

| Feature | State |
|---------|-------|
| `fixlen` (fp32 / fp64, string, blob) | always on |
| `array` (unsigned / signed / fixlen arrays) | always on |
| `sequence` (nested scopes) | always on |
| `fp64` | always on |

The scalar value type is 64-bit (`ulong` / `long`), matching the C default
configuration so the wire image and varint lengths are identical.

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
./coverage.sh                               # coverlet: Cobertura + terminal summary (+ HTML if ReportGenerator is installed)
```

Requires the .NET SDK 9. The `.devcontainer/` here builds a ready-to-use image
(`./.devcontainer/start.sh`) with the SDK and tooling preinstalled.

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

