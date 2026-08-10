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
generator emits one class per message plus the `Serialize` / `Decode` methods
that call the primitives here. The decoder uses the visitor pattern, so a
generated message is typically a single `switch` over the field id. The wire
format is byte-for-byte compatible with the other SofaBuffers language ports.

### Requirements

.NET SDK 10.0. Every project in the solution multi-targets `net9.0` and
`net10.0`, and `dotnet restore` resolves *all* of a solution's target frameworks
regardless of which one you then build, so the newest SDK is the minimum for
building or testing either leg; the .NET 9 runtime has to be installed as well to
run the `net9.0` leg (which is why every workflow that restores — CI *and* the
docs build — installs both). Consuming the published
package needs only .NET 9 or later.

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

`WriteFp32` / `WriteFp64` / `WriteString` / `WriteBlob` cover every fixed-length
field a schema can produce. The raw escape hatch `WriteFixlen(id, data, from,
length, subtype)` writes one directly, and holds the caller to what the wire
allows (CORELIB_PLAN §4.6): `subtype` must be one of the four defined tags —
`0x4`–`0x7` are reserved — and `Fp32` / `Fp64` must declare exactly 4 / 8 payload
bytes. Anything else is a malformed `fixlen_word` that every port, this one
included, rejects as `InvalidMessage` on decode, so the encoder refuses it up
front with `SofabException(SofabError.Argument)`, before writing a byte.

`WriteString` takes a C# `string`, a Unicode type, and transcodes it to UTF-8 on
the way out — `WriteBlob` is the call for arbitrary bytes (MESSAGE_SPEC §8). A
value with no valid UTF-8 encoding, i.e. one carrying an unpaired surrogate, is
refused with `SofabException(SofabError.Argument)` rather than silently
substituted with `U+FFFD` the way the default `Encoding.UTF8` does; silent
replacement is a data mutation the spec forbids. The refusal is **atomic at
every length**: no byte is written, `BytesUsed` does not advance, and a header
held back by `WriteSequenceBeginLazy` is not committed, so catching it and
closing the sequence still lets an otherwise-empty sequence vanish
(MESSAGE_SPEC §2) instead of framing an empty `26 07`. Valid strings — embedded
`U+0000` included — encode to exactly the bytes `Encoding.UTF8` would produce.

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

A sink that only *copies* the bytes — as `Stream.Write` does — returns without
doing anything else, and the encoder resumes at offset 0 of the same array. A
zero-copy sink instead **takes** the buffer (hands it to a transport, queues it
for an asynchronous write) and must install a replacement before it returns, with
`BufferSet(buffer, offset)`. The start offset belongs to that installation, not to
the buffer, so a sink can reserve framing-header room in *every* packet —
installing the same array again re-arms the reservation:

```csharp
byte[] a = new byte[512], b = new byte[512];   // two packet buffers
OStream os = null!;
FlushSink sink = (data, offset, length) =>
{
    Stamp(data, length);                       // fill the 3 reserved header bytes
    transport.Send(data, length);              // takes ownership of `data`
    os.BufferSet(ReferenceEquals(data, a) ? b : a, 3);  // reserve the next header
};
os = new OStream(a, 3, sink);                  // first packet's header room
```

### Nested sequences

A sequence (a nested struct/union, or an array of variable-size elements) is
opened with `WriteSequenceBeginLazy` and closed with one of two closers. The
header is **held back** until the sequence turns out to have content, so a
sequence closed with nothing in it emits nothing at all — which is exactly
MESSAGE_SPEC §2's rule that a sequence-typed field equal to its declared default
is omitted rather than framed empty. Nothing is buffered: the held-back ids are
encoder state, so a tiny output buffer still produces the one-shot bytes. The
hold-back reaches the format's full `MAX_DEPTH` (255) — the pending run grows on
demand, so the encoding is canonical at every nesting depth.

```csharp
os.WriteSequenceBeginLazy(4);
os.WriteSigned(1, -3);         // 26 — the first child commits the held-back header,
                               // 09 05 — then the child itself (id 1, zigzag(-3) = 5)
os.WriteSequenceEnd();         // 07 — the frame is on the wire

os.WriteSequenceBeginLazy(5);
os.WriteSequenceEnd();         // nothing: header and end marker both dropped

os.WriteSequenceBeginLazy(0);
os.WriteSequenceEndKeep();     // 06 07 — the frame is forced out even when empty
```

Those seven calls produce exactly `26 09 05 07 06 07` — six bytes, with the whole
middle sequence gone. (Byte-for-byte asserted by `ReadmeNestedSequencesExample`
in the encoder test suite.)

Which closer to use is a static property of the position in the schema, not of
the value:

| position | closer |
|---|---|
| `struct` / `union` field | `WriteSequenceEnd` |
| array field (the wrapper) | `WriteSequenceEnd` |
| wrapper-array **element** (`struct`/`union`/nested row) | `WriteSequenceEndKeep` |
| array field known to differ from a **non-empty** declared `default` | `WriteSequenceEndKeep` |

`WriteSequenceEndKeep` is the safe default when a call site is ambiguous: using
it where `WriteSequenceEnd` would do costs one non-canonical empty frame that
every decoder normalizes away, while the reverse drops an array element and
silently changes the decoded array's **length** (§5.1).

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

A field that declares a size is announced on the word that declares it, before
any payload: `ArrayBegin(id, kind, count)` for an array, `FixlenBegin(id,
subtype, total)` for a string / blob / float. That is where a schema `count` or
`maxlen` bound belongs — judged there, the verdict cannot depend on where the
input happened to be chunked (CORELIB_PLAN §5.2).

`String` payloads reach the visitor as raw wire bytes: the decoder transcodes
nothing and validates nothing, and a field the visitor ignores is skipped
without ever being looked at (CORELIB_PLAN §6.4). Generated code materializes
the C# `string` with a strict/fatal UTF-8 decoder, which is where invalid UTF-8
becomes the `InvalidMessage` outcome.

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

**That rejection is terminal** (CORELIB_PLAN §5.2 lists `INVALID` as *"no —
terminal"*). Once a `Feed` has thrown `InvalidMessage`, the `IStream` latches the
verdict: `Status` answers `DecodeStatus.Invalid` from then on, and every later
`Feed` throws `InvalidMessage` again — consuming nothing, emitting no visitor
callback. No continuation of bytes can make malformed input valid, so a caller
that logs the exception and keeps reading its socket can neither resume decoding
nor ever see `Complete` for a stream this decoder already rejected; decode the
next message with a new `IStream`. (`Invalid` is thus reported by `Status`, never
returned by `Feed`, which throws instead.) A `SofabException` carrying
`InvalidMessage` that a *visitor* raises — generated code judging a schema bound
(MESSAGE_SPEC §7.1) or a strict-UTF-8 payload (§6.4) — is the same terminal
outcome and latches the same way.

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
uniformly. A limit rejection is terminal as well (§6.2.1), so it too closes the
`IStream` to further feeds; but because those bytes are *well-formed*, §6.3
forbids folding it into the `INVALID` decode outcome, and `Status` never reports
`Invalid` for it — it stays `Incomplete`, which is what happened: the decoder
stopped part-way through a message and will not finish it.

### Code generator

The common case is *not* to call the primitives by hand but to let `sofabgen`
emit one typed class per message. Its surface is the same in every language port
— CORELIB_PLAN §6.1.1 closes the name set and lets a port adapt only the casing,
so a developer learns these once, not once per language:

| generated member | what it is |
|---|---|
| `Serialize(OStream os)` | streaming out: chains the `OStream` writes for this message's fields, and nothing else |
| `Encode()` | one-shot: `Serialize` into a `MaxSize` buffer, hand back exactly the bytes used |
| `Decode(byte[])` | one-shot: build the object from a complete message |
| `TryDecode(byte[], out T)` | the same, returning the `DecodeStatus` instead of discarding it |
| `Decoder` | streaming in: `new T.Decoder()` holds an `IStream` and takes chunks of any size through `Feed` |

Nothing else is an entry point into the wire format. A hand-written stand-in of
what the generator emits, condensed to one field pair:

```csharp
using sofab;

// generated by: sofabgen --lang csharp
public sealed class Point
{
    public long X, Y;
    public const int MaxSize = 32;

    public void Serialize(OStream os) { os.WriteSigned(1, X); os.WriteSigned(2, Y); }

    public byte[] Encode()
    {
        var buf = new byte[MaxSize];
        var os = new OStream(buf);
        Serialize(os);
        var outp = new byte[os.BytesUsed];
        Array.Copy(buf, outp, os.BytesUsed);
        return outp;
    }

    public static Point Decode(byte[] data)
    {
        var m = new Point();
        new IStream().Feed(data, 0, data.Length, new Visitor(m));
        return m;
    }

    public static DecodeStatus TryDecode(byte[] data, out Point msg)
    {
        msg = new Point();
        return new IStream().Feed(data, 0, data.Length, new Visitor(msg));
    }

    // The per-field hook the corelib's decoder calls: one switch over the id.
    private sealed class Visitor : IVisitor
    {
        private readonly Point _m;
        public Visitor(Point m) => _m = m;
        public void Signed(int id, long v)
        {
            switch (id) { case 1: _m.X = v; break; case 2: _m.Y = v; break; }
        }
    }

    public sealed class Decoder
    {
        private readonly Point _m = new Point();
        private readonly IStream _is = new IStream();
        private readonly Visitor _v;
        public Decoder() => _v = new Visitor(_m);
        public DecodeStatus Feed(byte[] chunk, int off, int len) => _is.Feed(chunk, off, len, _v);
        public DecodeStatus Status => _is.Status;
        public Point Message => _m;
    }
}
```

The one-shot pair is the 90% case — a message that fits comfortably in memory:

```csharp
var p = new Point { X = 3, Y = 4 };
byte[] wire = p.Encode();              // 09 06 11 08
Point got = Point.Decode(wire);        // got.X == 3, got.Y == 4
```

Both are thin wrappers over the streaming pair, which is what to reach for when
the message does not fit — or when the bytes arrive from a socket rather than in
one array. `Serialize` takes an `OStream` the *caller* owns, so give it a scratch
buffer with a `FlushSink` and the bytes leave as it fills; `Decoder.Feed` accepts
chunks of any size, so the decode side is driven by whatever the transport hands
over. Neither side ever holds the whole message:

```csharp
using System.IO;

using var outStream = new MemoryStream();                     // or a socket / file
var os = new OStream(new byte[Sofab.MinOutputBuffer], 0, outStream.Write);
p.Serialize(os);                                              // fields only
os.Flush();                                                   // push the tail
// outStream.ToArray() is byte-for-byte p.Encode(), through a one-byte buffer

var dec = new Point.Decoder();
DecodeStatus st = DecodeStatus.Complete;
for (int i = 0; i < wire.Length; i++)
    st = dec.Feed(wire, i, 1);                                // one byte at a time
// st == DecodeStatus.Complete -- the bytes ended on a field boundary (§5.2);
// dec.Message.X == 3, dec.Message.Y == 4
```

`Serialize` writes this message's fields and nothing else, which is exactly why a
nested message can be written into an already-open sequence frame — the same
method serves the top level and every level below it. On the decode side each
`Feed` returns the outcome for everything fed so far, so a caller that owns its
framing reads `Complete` / `Incomplete` per chunk and never needs a finish step.
(This section's bytes, and both streaming legs, are executed by
`ReadmeGeneratorExampleTests`.)

## Memory handling

The library owns no growable buffer and no intermediate message object; ownership
of the bytes stays with the caller.

- **Encode (`OStream`).** The caller owns the output `byte[]`; `OStream` writes
  straight into it and never allocates or grows it. Full with no sink → the next
  write throws `SofabError.BufferFull`. With a `FlushSink`, the full buffer is handed
  to the sink and writing resumes at the *start* of the same array (so a message can
  exceed the buffer, even RAM). The sink's array is the encoder's live buffer,
  reused after the call returns, so a sink that retains bytes must copy them —
  unless it **takes** the buffer, in which case it must install a replacement with
  `BufferSet(buffer, offset)` before returning; the encoder then resumes at *that
  call's* offset (reserved header room and all) instead of at 0. The offset is
  consumed by the flush it was installed in: a later flush the sink returns from
  without installing anything resumes at 0 again (CORELIB_PLAN §5.1).
- **No payload-sized temporaries.** That "never allocates" holds for `string` too,
  the one payload that has to be *transcoded* rather than copied: when the value is
  longer than the room left in the buffer, `WriteString` transcodes it in pieces
  straight into that room — carrying a split surrogate pair across the flush with a
  stateful encoder — instead of materializing the UTF-8 bytes first. So the buffer,
  not the message, bounds peak memory (CORELIB_PLAN §5.1: the payload run of a
  `string` is *divisible* at any byte boundary): a 64 MB string streams through a
  16-byte buffer in 16-byte pieces, and a `string` with no schema `maxlen` is not a
  memory risk. Output bytes are identical either way.
- **`Sofab.MinOutputBuffer` = 1 (`MIN_OUTPUT_BUFFER`, CORELIB_PLAN §5.1).** The
  smallest buffer accepted **for streaming**. This encoder splits every atomic unit
  across a flush, so one byte is enough: any message streams through a one-byte
  buffer and produces bytes identical to the one-shot path. The minimum applies
  **only to a buffer installed with a `FlushSink`** — at construction and at every
  `BufferSet` — where `buffer.Length - offset >= Sofab.MinOutputBuffer` must hold;
  a buffer that falls short is rejected right there with an
  `ArgumentOutOfRangeException`, the same way an out-of-range offset is, never
  partway through a message. A buffer installed **without** a sink is subject to no
  minimum at all: no flush can occur, so a message sized from a bounded schema's
  `MaxSize` fits exactly — a two-byte message encodes into a two-byte buffer — and
  anything larger reports `SofabError.BufferFull`.
- **Decode (`IStream` + `IVisitor`).** The `byte[]` you `Feed` is aliased, not
  copied: `String` / `Blob` chunks point directly into it (`data[chunkOffset ..
  chunkOffset+chunkLength)`) and are valid only for the duration of the callback.
  Scalars and floats are passed by value (no boxing). A visitor that retains bytes
  must copy the chunk.

## Feature flags

No build toggles — always the full format. In particular there is no
`SOFAB_STRICT_UTF8` knob to turn off: C# `string` is a Unicode type and can
never hold non-UTF-8 bytes, so this port is **always strict** (CORELIB_PLAN
§6.4: "Unicode-string targets are always strict").

## Build & test

```bash
dotnet build SofaBuffers.sln -c Release     # build library, tests and benchmarks
dotnet test  SofaBuffers.sln                # run the xUnit suite
./coverage.sh                               # coverlet: Cobertura + terminal summary
```

Requires the .NET SDK 10 and the .NET 9 runtime (see [Requirements](#requirements)).
All three commands cover both target frameworks; append `-f net9.0` / `-f net10.0`
to `build` or `test` to narrow a run to one of them, and `-c Debug` / `-c Release`
to pick a configuration. CI crosses the two axes: four legs build *and* run the
suite in Debug and Release on each framework, so unoptimized code paths and
`#if DEBUG` code are compiled and exercised too. The `.devcontainer/` builds a
ready-to-use image with the SDKs and tooling preinstalled. Tests live in `tests/SofaBuffers.Tests/`, including
conformance replay of the shared language-agnostic vectors (byte-exact encode,
field-match decode, byte-at-a-time chunked decode). Helpers used by more than one
test file live in `tests/SofaBuffers.Tests/Common/` — `RecordingVisitor` plus
`TestBytes.Bytes`/`Encode`; import the latter with
`using static SofaBuffers.Tests.Common.TestBytes;` rather than re-declaring them.

## Benchmarks

Three standalone tools mirror the other ports' benchmarks so implementations can
be compared directly: `perf` measures the per-op cost, `bench` measures this
machine's throughput in MB/s, and `bench/run_callgrind.sh` measures the
deterministic instruction count per operation (Ir/op). The first two are one
project, `bench/SofaBuffers.Bench`, selected by argument; because that project
multi-targets, each command line has to name the framework to run:

```bash
# perf -- per-op cost: a CPU-speed-independent figure plus throughput MB/s.
dotnet run -c Release --project bench/SofaBuffers.Bench -f net10.0 -- perf

# bench -- a throughput table in MB/s for encode/decode workloads. MB = 1e6 bytes.
dotnet run -c Release --project bench/SofaBuffers.Bench -f net10.0 -- bench
```

The workload set is the family's, defined once in
`bench/SofaBuffers.Bench/Workloads.cs` and driven by all three tools: a
1000-element `u64` array, a small mixed `typical` message, an unbounded **1 MB
`blob`** encoded both one-shot and streamed through a 4096-byte buffer with a
flush sink (and decoded from 4096-byte chunks), and a **`composite`** message
holding what the flat datasets never reach — a wrapper array with a header per
element, 320 bytes of non-ASCII UTF-8, nesting at depth 3, a field equal to its
default that the encoder must *not* write, and a two-byte field header. The
encoded sizes are cross-port parity checks: 170 bytes for the `perf` message,
1,000,005 for the blob and 956 for the composite.

The managed runtime exposes no portable cycle counter, so `perf` reports CPU
time/op (clock-independent) as the code-cost proxy alongside MB/s. Only the third
tool is fully independent of the machine: `bench/run_callgrind.sh` counts
instructions retired per operation under Callgrind (it needs `valgrind` on the
`PATH` — `apt-get install valgrind`, and the `.devcontainer/` image ships it —
builds the project itself, and runs it directly on the built assembly):

```bash
bash bench/run_callgrind.sh
# one row: WORKLOADS=encode_composite bash bench/run_callgrind.sh
# workloads: encode_u64_array, encode_typical, encode_blob_oneshot, encode_blob_streaming, encode_composite, decode_u64_array, decode_typical, decode_blob, decode_composite, decode_composite_skip
```

Because the .NET runtime JITs the hot code at run time there is no stable native
symbol to `--toggle-collect` on, so the script runs each workload at two rep
counts and subtracts the whole-process instruction counts
(`Ir/op = (Ir(R2) − Ir(R1)) / (R2 − R1)`), which cancels CLR startup, JIT and
one-time setup exactly and leaves the pure per-op cost. It pins the runtime
(`DOTNET_TieredCompilation=0`, a gen0 large enough that the bounded run never
collects, a heap-limit cap so the GC initializes under Valgrind) so the two runs
differ only in the rep count.

One measurement, on one shared container (`net10.0`, .NET 10.0.302) — the MB/s
column is this machine, the Ir/op column is not:

| workload | Ir/op | MB/s |
|---|---:|---:|
| encode: u64 array (1000)   |    64,417 |   2,169.28 |
| encode: typical message    |     1,591 |     133.57 |
| encode: blob 1MB one-shot  | 1,008,631 |  35,615.69 |
| encode: blob 1MB streaming |   155,458 |  42,797.57 |
| encode: composite          |    22,661 |     601.78 |
| decode: u64 array (1000)   |   107,102 |   1,697.40 |
| decode: typical message    |     2,447 |     150.42 |
| decode: blob 1MB           |    39,522 | 392,692.15 |
| decode: composite          |    21,708 |     570.68 |
| decode: composite skip-all |    21,609 |     502.02 |

**Read the two `blob 1MB` encode rows against each other, not against the rest.**
Five of that message's bytes are metadata and a million are payload, so its MB/s
is this machine's memory bandwidth rather than a statement about the library —
and the streamed row comes out *ahead* of the one-shot one, because a 4 KiB window
stays in L1 while a one-shot encode writes a megabyte out to memory. Their Ir/op
gap has to be read with the same care, because on this runtime it is an artifact
of how Callgrind counts a bulk copy: a bare `Array.Copy` of a megabyte costs
~1,000,000 Ir whether the destination starts at offset 0 or at 5, while the same
volume copied as 245 × 4096 bytes costs 70,629 — the big copy takes a
repeat-string path Callgrind charges about one instruction per byte, the 4 KiB
ones a vectorized path at ~0.07. So the honest reading of the pair is not
"streaming is 6.5× cheaper": it is that the streamed row's 155,458 Ir sits
84,829 above the 70,629 a bare chunked copy of the same bytes costs, i.e. **~346
Ir per flush** is what the divisible-run path (CORELIB_PLAN §5.1) costs here.

`decode: blob 1MB` is the same kind of row: the decoder hands the visitor a window
into the input and copies nothing, so 39,522 Ir — 161 per 4096-byte chunk — buys a
megabyte, and the MB/s figure says more about that than about memory.

`decode: composite skip-all` lands within half a percent of `decode: composite`
(21,609 against 21,708). In a push port "skip everything" is a visitor that
overrides nothing, so what it saves is the callback bodies alone: the walk, the
UTF-8 validation and the chunk bookkeeping are the decode, and none of them can be
skipped. A router that materializes nothing pays essentially full price here.
