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

A dependency-free, allocation-light, streaming C# implementation of the
SofaBuffers (*Sofab*) serialization format. Like protobuf's `CodedInputStream` /
`CodedOutputStream`, it is meant to be driven by generated code: a schema-driven
generator emits one class per message plus marshal / unmarshal methods that call
the primitives here. The decoder uses the visitor pattern, so a generated message
is typically a single `switch` over the field id. The wire format is byte-for-byte
compatible with the other SofaBuffers language ports.

### Requirements

.NET SDK 9.0 or later; the library targets `net9.0`.

### Dependencies

None — only the .NET base class library (`System.Text`, `System.Buffers.Binary`).
No reflection and no runtime codegen, so it is friendly to trimming and Native AOT.

### Packaging

NuGet package id `SofaBuffers.Corelib`; the assembly is `SofaBuffers.dll` and the
public API lives under the `sofab` namespace (fixed by the format spec). Install it:

```sh
dotnet add package SofaBuffers.Corelib
```

## Why this design

| Goal | How |
|------|-----|
| No per-field allocation | State lives in caller buffers and small `OStream` / `IStream` objects. Scalars stay primitive (`ulong` / `double`) — no boxing on the hot path. |
| No reflection, no runtime codegen | Pure method calls; the decoder pushes to an `IVisitor` rather than reflecting over fields. Suitable for Native AOT and trimmed runtimes. |
| Streaming out | `OStream` writes into a small caller buffer and invokes a `FlushSink` whenever it fills, so a message can exceed the buffer — even RAM. |
| Streaming in | `IStream` is a state machine fed arbitrary chunks; large string / blob payloads are delivered in pieces to your `IVisitor`. |
| Reserve-offset | `new OStream(buf, offset)` leaves room at the front for a lower-layer header, saving a copy. |
| Explicit endianness | IEEE-754 values are read / written explicitly little-endian, identical on every runtime. |
| Generated-code friendly | `IVisitor` has a default no-op for every field kind, so sinks override only what they need. |

## Usage

### Encode

```csharp
using sofab;

byte[] buf = new byte[64];
var os = new OStream(buf);
os.WriteUnsigned(1, 42);
os.WriteSigned(2, -7);
os.WriteString(3, "hi");
int used = os.BytesUsed;   // bytes written to the buffer
```

### Decode

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
`IOException`); the specific cause is available via `SofabException.Error`.

### Streaming a message larger than the buffer

Give the `OStream` a `FlushSink`, whose signature `(byte[] data, int offset, int
length)` matches `Stream.Write`. The encoder hands the sink each full buffer and
resumes at the start, so a tiny scratch buffer can emit an arbitrarily large
message:

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

### Pull-decoding a `Stream` in chunks

Feed `IStream` whatever bytes you have; it keeps all parse state internally, so a
field (even a string / blob payload) may straddle any number of `Feed` calls:

```csharp
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

A string / blob field arrives as one chunk when fully present and as several when
split across `Feed` calls; each chunk carries the field's `total` length and its
`offset` within the field, so the payload need never be held in one piece:

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

## Memory handling

The library owns no growable buffer and no intermediate message object; ownership
of the bytes stays with the caller.

- **Encode (`OStream`).** The caller owns the output `byte[]`; `OStream` writes
  straight into it and never allocates or grows it. Full with no sink → the next
  write throws `SofabError.BufferFull`. With a `FlushSink`, the full buffer is handed
  to the sink and writing resumes at the *start* of the same array (so a message can
  exceed the buffer, even RAM). The sink's array is the encoder's live buffer,
  reused after the call returns, so a sink that retains bytes must copy them.
- **Decode (`IStream` + `IVisitor`).** The `byte[]` you `Feed` is aliased, not
  copied: `String` / `Blob` chunks point directly into it (`data[chunkOffset ..
  chunkOffset+chunkLength)`) and are valid only for the duration of the callback.
  Scalars and floats are passed by value (no boxing). A visitor that retains bytes
  must copy the chunk.

## Feature flags

No build toggles — always the full format.

## Build & test

```bash
dotnet build SofaBuffers.sln -c Release     # build library, tests and benchmarks
dotnet test  SofaBuffers.sln                # run the xUnit suite
./coverage.sh                               # coverlet: Cobertura + terminal summary
```

Requires the .NET SDK 9. The `.devcontainer/` builds a ready-to-use image with the
SDK and tooling preinstalled. Tests live in `tests/SofaBuffers.Tests/`, including
conformance replay of the shared language-agnostic vectors (byte-exact encode,
field-match decode, byte-at-a-time chunked decode).

## Benchmarks

Two standalone tools mirror the other ports' benchmarks so implementations can be
compared directly:

```bash
# perf -- per-op cost: a CPU-speed-independent figure plus throughput MB/s.
dotnet run -c Release --project bench/SofaBuffers.Bench -- perf

# bench -- a throughput table in MB/s for encode/decode workloads. MB = 1e6 bytes.
dotnet run -c Release --project bench/SofaBuffers.Bench -- bench
```

The managed runtime exposes no portable cycle counter, so `perf` reports CPU
time/op (clock-independent) as the code-cost proxy alongside MB/s. For a fully
CPU-speed-independent number, name a workload to run it once under Callgrind:

```bash
valgrind --tool=callgrind --collect-atstart=no \
  --toggle-collect='*Callgrind.OpEncodeU64Array*' \
  dotnet bench/SofaBuffers.Bench/bin/Release/net9.0/SofaBuffers.Bench.dll encode_u64_array
# workloads: encode_u64_array, decode_u64_array, encode_typical, decode_typical
```
