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

The codec has four use cases — serialize a message that fits in one buffer,
serialize one too large for the buffer (streamed out in chunks), deserialize a
whole message, and deserialize one arriving in chunks — plus the generated-code
path that wraps them. Encoder and decoder report problems by throwing
`SofabException` (which extends `IOException`); the cause is on `SofabException.Error`.

### Serialize

Write fields into a caller-owned `byte[]` sized to hold the whole message, then
read the byte count:

```csharp
using sofab;

byte[] buf = new byte[64];
var os = new OStream(buf);
os.WriteUnsigned(1, 42);
os.WriteSigned(2, -7);
os.WriteString(3, "hi");
int used = os.BytesUsed;        // bytes written to the buffer
```

### Serialize stream

Give the `OStream` a `FlushSink`, whose `(byte[] data, int offset, int length)`
signature matches `Stream.Write`. The encoder hands the sink each full buffer and
resumes at the start, so a tiny scratch buffer emits an arbitrarily large message:

```csharp
using System.IO;
using sofab;

byte[] scratch = new byte[16];                 // tiny buffer
using var outStream = new MemoryStream();      // or a socket / file
FlushSink sink = outStream.Write;              // (data, offset, length)
var os = new OStream(scratch, 0, sink);
for (int i = 0; i < 1000; i++)
    os.WriteUnsigned(i, (ulong)i);
os.Flush();                                    // push the tail
```

### Deserialize

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

### Deserialize stream

`IStream` keeps all parse state internally, so feed it whatever bytes you have —
from any source — and a field (even a string / blob payload) may straddle any
number of `Feed` calls:

```csharp
using sofab;

var iss = new IStream();
var sink = new My();
byte[] chunk = new byte[16];
int n;
DecodeStatus status = DecodeStatus.Complete;
while ((n = inStream.Read(chunk, 0, chunk.Length)) > 0)
    status = iss.Feed(chunk, 0, n, sink);      // decode this slice
// status (also iss.Status) is Complete if the bytes ended at a field boundary,
// or Incomplete if the stream stopped inside a field / with an open sequence.
```

`Feed` returns a `DecodeStatus` (MESSAGE_SPEC §7). `Complete` means the bytes
consumed so far end exactly at a field boundary — a valid message. `Incomplete`
means they end *inside* a field (a partial varint, an unfinished string / blob /
array payload, or a still-open nested sequence): **not** an error and **not** a
rejection — the partial field is held and the next `Feed` resumes where it left
off. There is no finish / finalize step: the caller owns end-of-input and decides
whether a trailing `Incomplete` is a truncation for its protocol. Genuinely
malformed input — regardless of what follows — still throws `SofabException`
with `SofabError.InvalidMessage`.

Generated decode code may also enforce receiver-side limits on unbounded
(schema declares no `count` / `maxlen`) fields — `max_dyn_array_count`,
`max_dyn_string_len`, `max_dyn_blob_len` caps baked in by `sofabgen`. A field
whose wire count or total length exceeds its cap throws `SofabException` with
`SofabError.LimitExceeded`, raised *before* any allocation and never clamped or
truncated. This is a category deliberately **distinct** from
`SofabError.InvalidMessage`: exceeding a configured cap is receiver *policy*, not
wire malformation, so two peers with different caps do not read as a conformance
divergence. This corelib enforces no limits and ships no default cap values — it
only defines the `LimitExceeded` category so generated code reports a violation
uniformly.

### Code generator

The common case is *not* to call the primitives by hand but to let `sofabgen`
emit one typed class per message: a `Marshal` chaining `OStream` writes, an
`Encode` that marshals into a `MaxSize` buffer, and a `Decode` built on the
`IVisitor` switch. A hand-written stand-in, encoded then decoded:

```csharp
using sofab;

// generated by: sofabgen --lang csharp
public sealed class Point : IVisitor
{
    public long X, Y;
    public const int MaxSize = 32;

    public void Marshal(OStream os) { os.WriteSigned(1, X); os.WriteSigned(2, Y); }

    public byte[] Encode()
    {
        var buf = new byte[MaxSize];
        var os = new OStream(buf);
        Marshal(os);
        var outp = new byte[os.BytesUsed];
        Array.Copy(buf, outp, os.BytesUsed);
        return outp;
    }

    public static Point Decode(byte[] data)
    {
        var p = new Point();
        new IStream().Feed(data, 0, data.Length, p);
        return p;
    }

    public void Signed(int id, long v)
    {
        switch (id) { case 1: X = v; break; case 2: Y = v; break; }
    }
}

var p = new Point { X = 3, Y = 4 };
byte[] wire = p.Encode();
Point got = Point.Decode(wire);        // got.X == 3, got.Y == 4
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

## Strings & UTF-8

A `string` is UTF-8 text on the wire (MESSAGE_SPEC §8); `blob` is the type for
arbitrary bytes. Because C# `string` is a Unicode (UTF-16) type it can never
hold non-UTF-8 bytes, so SofaBuffers C# is **always strict** — there is no
`SOFAB_STRICT_UTF8` toggle to turn off (CORELIB_PLAN §6.4: "Unicode-string
targets are always strict").

- **Encode.** `OStream.WriteString` refuses a value that cannot be encoded as
  valid UTF-8 — i.e. one carrying an unpaired surrogate — with
  `SofabException(SofabError.Argument)`, *before* writing any bytes. It never
  silently substitutes `U+FFFD` the way the default `Encoding.UTF8` does; silent
  replacement is a data mutation the spec forbids. Valid strings (including
  embedded `U+0000`) encode to exactly the same bytes as before.
- **Decode.** The decoder hands the raw string bytes to your visitor without
  transcoding; generated code materializes the `string` with a strict/fatal
  decoder, producing the `InvalidMessage` outcome on invalid UTF-8. Skipped
  fields are never validated.

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
CPU-speed-independent number, `bench/run_callgrind.sh` reports instructions
retired per operation (Ir/op) under Callgrind:

```bash
bash bench/run_callgrind.sh
# workloads: encode_u64_array, decode_u64_array, encode_typical, decode_typical
```

Because the .NET runtime JITs the hot code at run time there is no stable native
symbol to `--toggle-collect` on, so the script runs each workload at two rep
counts and subtracts the whole-process instruction counts
(`Ir/op = (Ir(R2) − Ir(R1)) / (R2 − R1)`), which cancels CLR startup, JIT and
one-time setup exactly and leaves the pure per-op cost.
