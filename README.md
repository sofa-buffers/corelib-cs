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
— a port of the C `corelib`'s `istream.c` / `ostream.c` that runs anywhere .NET
does, from desktops and servers to containers in the cloud.

Like protobuf's `CodedInputStream` / `CodedOutputStream`, this library is meant to
be driven by **generated code**: a schema-driven generator emits one class per
message plus marshal / unmarshal methods that call the primitives here. The
decoder uses the **visitor pattern**, so a generated message is typically a single
`switch` over the field id.

The wire format is specified, language-neutrally, in the
[SofaBuffers documentation](https://github.com/sofa-buffers/documentation). The
unit tests here replay the shared, language-agnostic conformance vectors
(`assets/test_vectors.json`, copied verbatim from the documentation repo) to
guarantee byte-for-byte interoperability with the C, C++, Rust, Java and Go
implementations.

### Requirements

- **.NET SDK 9.0 or later.** The library targets `net9.0` (see
  `src/SofaBuffers/SofaBuffers.csproj`); CI builds and tests on `9.0.x`.

### Dependencies

- **None.** The runtime uses only the .NET base class library (`System.Text`,
  `System.Buffers.Binary`). No reflection and no runtime code generation, so it is
  friendly to trimming and Native AOT.

### Package

NuGet package id `SofaBuffers.Corelib`; the assembly is `SofaBuffers.dll` and the
public API lives under the `sofab` namespace (fixed by the format spec, as in the
C++ `namespace sofab`). Install it with:

```sh
dotnet add package SofaBuffers.Corelib
```

## Why this design

| Goal | How |
|------|-----|
| No per-field allocation | All state lives in caller-provided buffers and small `OStream` / `IStream` objects. Scalars stay primitive (`ulong` / `double`) — no boxing, nothing escapes to the heap on the hot path. |
| No reflection, no runtime codegen | Pure method calls; the decoder pushes to an `IVisitor` interface rather than reflecting over fields. Suitable for AOT / Native AOT and trimmed, locked-down runtimes. |
| Streaming **out** | `OStream` writes into a small caller buffer and invokes a `FlushSink` (a delegate, e.g. `stream.Write`) whenever it fills, so a message can exceed the buffer — and even RAM. |
| Streaming **in** | `IStream` is a state machine fed arbitrary chunks; large string / blob payloads are delivered in pieces to your `IVisitor`. |
| Reserve-offset | `new OStream(buf, offset)` leaves room at the front of the buffer for a lower-layer protocol header (saves a copy). |
| Explicit endianness | IEEE-754 values are written / read explicitly little-endian, so behaviour is identical on every runtime. |
| Generated-code friendly | `IVisitor` has a default no-op for every field kind (a C# default-interface method), so generated (and hand-written) sinks override only what they need and ignore the rest. |

## Usage

All public types are in the `sofab` namespace.

### Simple encode

```csharp
using sofab;

byte[] buf = new byte[64];
var os = new OStream(buf);
os.WriteUnsigned(1, 42);
os.WriteSigned(2, -7);
os.WriteString(3, "hi");
int used = os.BytesUsed;   // bytes written to the buffer
```

### Simple decode

The decoder pushes each decoded field to your `IVisitor`; override only the kinds
you consume (every callback defaults to a no-op, so unhandled fields are skipped):

```csharp
using sofab;

class My : IVisitor
{
    public ulong A;
    public long  B;
    public void Unsigned(int id, ulong v) { if (id == 1) A = v; }
    public void Signed(int id, long v)    { if (id == 2) B = v; }
    // Fp32, Fp64, String, Blob, ArrayBegin, SequenceBegin, ... as needed
}

var sink = new My();
new IStream().Feed(buf, 0, used, sink);
```

Encoder and decoder report problems by throwing `SofabException` (which extends
`IOException`, so it composes with .NET I/O and a flush sink that does real I/O);
the specific cause is available via `SofabException.Error`.

### Streaming a message larger than the buffer (OStream over a `Stream`)

Give the `OStream` a `FlushSink` — the streaming *output* primitive. Its signature
`(byte[] data, int offset, int length)` matches `Stream.Write`, so a `Stream` is a
sink by method group. The encoder hands the sink each full buffer and resumes at
the start, so a tiny scratch buffer can emit an arbitrarily large message:

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

### Pull-decoding a `Stream` in chunks (IStream)

`IStream` is the streaming *input* primitive: feed it whatever bytes you have and
it keeps all parse state internally, so a field (even a string / blob payload) may
straddle any number of `Feed` calls. Pump a `Stream` through it in fixed-size
reads:

```csharp
using System.IO;
using sofab;

var iss = new IStream();
var sink = new My();
byte[] chunk = new byte[16];
int n;
while ((n = inStream.Read(chunk, 0, chunk.Length)) > 0)
{
    iss.Feed(chunk, 0, n, sink);               // decode this slice
}
```

A string / blob field arrives as **one chunk when fully present** in the fed
buffer and as **several chunks** when split across `Feed` calls; each chunk carries
the field's `total` length and its `offset` within the field, so the payload need
never be held in one piece:

```csharp
class BlobSink : IVisitor
{
    public void Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
        // append data[chunkOffset .. chunkOffset+chunkLength) to your destination
    }
}
```

### Generated-code shape (Serialize / Deserialize)

The common case is *not* to call these primitives by hand but to let the schema
generator emit a typed class per message. Generated code has no magic — it is a
thin `Serialize` that chains `OStream` writes and a `Deserialize` built on the
`IVisitor` switch above. A hand-written equivalent looks like this:

```csharp
using sofab;

public sealed class Point : IVisitor
{
    public long X;
    public long Y;

    // encode: chain typed writes into a caller buffer
    public int Serialize(byte[] buf)
    {
        var os = new OStream(buf);
        os.WriteSigned(1, X);
        os.WriteSigned(2, Y);
        return os.BytesUsed;
    }

    // decode: feed the bytes; the visitor callbacks below fill the fields
    public static Point Deserialize(byte[] data, int off, int len)
    {
        var p = new Point();
        new IStream().Feed(data, off, len, p);
        return p;
    }

    public void Signed(int id, long v)
    {
        switch (id) { case 1: X = v; break; case 2: Y = v; break; }
    }
}
```

## API summary

### Encoding (`OStream`)

`OStream` wraps a **caller-owned** `byte[]` and appends fields to it. The surface
is a set of typed `Write*` calls that each emit one field: `WriteUnsigned` /
`WriteSigned` / `WriteBoolean` for varint scalars, `WriteFp32` / `WriteFp64` for
IEEE-754 floats, `WriteString` (UTF-8, no NUL) and `WriteBlob` for length-prefixed
byte payloads, and `WriteFixlen` as the low-level primitive the float / string /
blob writers build on. Homogeneous arrays go through `WriteArrayUnsigned` /
`WriteArraySigned` (overloaded across the four integer widths — u8/u16/u32/u64 and
i8/i16/i32/i64) and `WriteArrayFp32` / `WriteArrayFp64`; a **zero-count array is
valid** and encoded as its header alone. Nesting is opened and closed with
`WriteSequenceBegin(id)` / `WriteSequenceEnd()` (up to `MAX_DEPTH` = 255 levels).

There is no growable buffer and no builder object: the writers append directly to
your array. `BytesUsed` reports how many bytes have been written since the last
flush; `Flush()` pushes any pending bytes to the sink (and returns the count that
was pending); `BufferSet(buffer, offset)` swaps in a fresh caller array, typically
from inside a flush sink (e.g. double-buffering).

Errors are **thrown, not returned**: an out-of-range id or negative length raises
`SofabError.Argument`, and a full buffer with no sink raises
`SofabError.BufferFull`.

### Decoding (`IStream` + `IVisitor`)

The decoder is **pull on the wire, push to the caller**: you `Feed` bytes and the
`IStream` calls back into your `IVisitor` once per decoded value. There is no
per-field "read into this destination" call and no explicit `Skip` — a visitor
leaves a callback at its default no-op to ignore (skip) that field kind.

- **`IStream`** — `Feed(byte[] data, IVisitor)` and
  `Feed(byte[] data, int off, int len, IVisitor)`. May be fed in arbitrarily small
  chunks; all parse state lives in the `IStream`, so any field can straddle any
  number of `Feed` calls.
- **`IVisitor`** — the read surface: `Unsigned` / `Signed` hand back a `ulong` /
  `long` by value (also each integer-array element), `Fp32` / `Fp64` a `float` /
  `double`, `String` / `Blob` a chunk of bytes **viewing the input buffer**,
  `ArrayBegin(id, kind, count)` announces that `count` elements follow through the
  scalar / float callbacks, and `SequenceBegin` / `SequenceEnd` bracket a nested
  id scope. Every method is a default no-op, so you implement only what you read.

Scalars are delivered whole; string / blob payloads arrive as one chunk when fully
present and as several when split across `Feed` calls (each chunk reports the field
`total` and the chunk's `offset` within it). An empty string / blob is reported as
a single call with `total == 0` and `chunkLength == 0`. Malformed input (varint
overflow, bad type tag, out-of-range id, dangling sequence end, over-deep nesting)
raises `SofabError.InvalidMessage`.

**Allowed types.** unsigned int (`ulong`), signed int (`long`, ZigZag), bool
(an unsigned `0`/`1` on the wire — decoded via `Unsigned`), fp32 / fp64, string
(UTF-8, no terminator), blob, and arrays of the integer widths and of fp32 / fp64.
The **only disallowed** array form is a *dynamic-length subtype* (`string` / `blob`)
as a fixlen-array element — the encoder offers no such overload and the decoder
rejects it with `SofabError.InvalidMessage` ("dynamic fixlen array element").
Zero-count arrays are legal on both sides.

### Memory handling

The library never owns a growable buffer or an intermediate message object: the
encoder writes into a fixed, caller-provided array, and the decoder hands back
values either by value or as a **view into the caller's own input buffer**.

- **Input buffer (decode).** The `byte[]` you pass to `Feed` is aliased, not
  copied: `String` / `Blob` chunks point **directly into it** (`data[chunkOffset ..
  chunkOffset+chunkLength)`), valid only for the duration of the callback. The
  library never allocates a `string` or copies the payload — a visitor that wants a
  `string` calls `Encoding.UTF8.GetString(...)` itself, and one that retains bytes
  must copy the chunk. Scalars / floats are decoded into primitives and passed by
  value (no boxing, no allocation). The decoder's only internal scratch is a fixed
  **8-byte** accumulator used to reassemble a single `fp32` / `fp64` value that
  straddles a `Feed` boundary (partial varints are held in a couple of `ulong` /
  `int` fields); it is never used for string / blob bytes, which always view the
  input directly.
- **Output buffer (encode).** The caller owns the output `byte[]`; `OStream` writes
  straight into it and **never allocates or grows it**. When it fills with **no**
  sink, the next write throws `SofabError.BufferFull`; **with** a `FlushSink`, the
  full buffer is handed to the sink and writing resumes at the *start* of the same
  array, so a message can exceed the buffer (and even RAM). The sink's `byte[]` is
  the encoder's live buffer, reused after the call returns, so a sink that retains
  bytes must copy them. Scalars, floats, blobs and arrays are encoded in place with
  no per-write allocation; `WriteString` measures the UTF-8 length once and encodes
  **directly into the buffer** when it has room, allocating a temporary array only
  on the buffer-spanning fallback (encode pre-encoded UTF-8 via `WriteBlob` /
  `WriteFixlen` to avoid even that).
- **Message object.** There is none: your `IVisitor` (or generated message class)
  *is* the message. The library holds no per-message heap state beyond the small
  `OStream` / `IStream` instances.

### Supporting types

`FixlenType` (`Fp32`, `Fp64`, `String`, `Blob`), `ArrayKind` (`Unsigned`,
`Signed`, `Fixlen`), `SofabError` (`Argument`, `Usage`, `BufferFull`,
`InvalidMessage` — the encoder / decoder actually raise `Argument`, `BufferFull`
and `InvalidMessage`), `SofabException` (`: IOException`, carries `.Error`),
`FlushSink` (delegate `(byte[] data, int offset, int length)`), and
`Sofab.ApiVersion` (`== 1`).

## Feature flags

**No build toggles — always the full format.** Unlike the C library's compile-time
`SOFAB_DISABLE_*` switches (which strip whole code paths for tiny
microcontrollers), the C# build defines no conditional-compilation constants and
ships every field kind unconditionally: `fixlen` (fp32 / fp64, string, blob),
`array` (unsigned / signed / fixlen arrays), `sequence` (nested scopes) and `fp64`
are always present. The scalar value type is 64-bit (`ulong` / `long`), matching
the C default configuration so the wire image and varint lengths are identical.

## Build & test

```bash
dotnet build SofaBuffers.sln -c Release     # build the library, tests and benchmarks
dotnet test  SofaBuffers.sln                # run the xUnit suite
./coverage.sh                               # coverlet: Cobertura + terminal summary (+ HTML if ReportGenerator is installed)
```

Requires the .NET SDK 9. The `.devcontainer/` here builds a ready-to-use image
(`./.devcontainer/start.sh`) with the SDK and tooling preinstalled.

Tests live in `tests/SofaBuffers.Tests/`: `TestVectorsConformanceTests.cs` replays
the shared language-agnostic vectors (encode byte-exact, decode field-match, and
byte-at-a-time chunked decode); `OStreamTests` / `IStreamTests` cover the encoder
and decoder against the same vectors; `DecoderErrorsTests` exercises every
malformed-input rejection branch; `EncoderOverloadsTests` every writer overload and
argument check; `RoundTripTests` encode→decode value preservation; `ApiTests`
offset-reserve and flush-sink streaming larger than the buffer; `StreamingEdgeTests`
empty string/blob and multi-byte array counts; `VisitorDefaultsTests` that a no-op
`IVisitor` silently drops every field kind.

The [`ci.yml`](.github/workflows/ci.yml) workflow builds and tests on .NET `9.0.x`,
then a separate coverage job runs the suite under `coverlet.collector` (scoped to
the library via `coverlet.runsettings`) and publishes the **Coverage** and
**Branches** badge JSON to an orphan `badges` branch. The [`docs.yml`](.github/workflows/docs.yml)
workflow builds the DocFX API site and deploys it to GitHub Pages (the **Docs**
badge).

## Benchmarks

Two standalone tools mirror the C / C++ / Rust / Java benchmarks so the
implementations can be compared directly:

```bash
# perf -- per-op cost: a CPU-speed-independent figure plus throughput MB/s.
dotnet run -c Release --project bench/SofaBuffers.Bench -- perf

# bench -- a throughput table in MB/s for encode/decode workloads. MB = 1e6 bytes.
dotnet run -c Release --project bench/SofaBuffers.Bench -- bench
```

`perf` and `bench` encode the identical message (same field ids, types and values)
as their C/C++/Rust/Java counterparts and print the same report layout. The managed
.NET runtime exposes no portable hardware cycle counter, so — like the Java tool —
`perf` reports `cycles/op` as unavailable and uses **CPU time/op** (process CPU
time, clock-independent) as the code-cost proxy, alongside the machine-dependent
MB/s figure.

For a fully CPU-speed-independent number, name a workload directly to run it
**once** under a profiler (Callgrind instruction counting):

```bash
# one encode of the 1000-element u64 array, setup excluded:
valgrind --tool=callgrind --collect-atstart=no \
  --toggle-collect='*Callgrind.OpEncodeU64Array*' \
  dotnet bench/SofaBuffers.Bench/bin/Release/net9.0/SofaBuffers.Bench.dll encode_u64_array
# workloads: encode_u64_array, decode_u64_array, encode_typical, decode_typical
```
