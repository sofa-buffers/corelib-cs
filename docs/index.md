<p align="center"><img src="assets/sofabuffers_logo.png" alt="SofaBuffers Logo" height="120"></p>

# SofaBuffers C# — API documentation

A **dependency-free**, **allocation-light**, **streaming** C# implementation of
the SofaBuffers (*Sofab*) serialization format. It is the runtime stream core
(equivalent to the C `corelib`'s `istream` / `ostream`), a port of the C
`corelib` (`istream.c` / `ostream.c`).

The decoder uses the **visitor pattern**, so a generated message is typically a
single `switch` over the field id.

## Where to start

- **[API reference](xref:sofab)** — every public type, generated from the XML doc comments.
- <xref:sofab.OStream> — the streaming encoder.
- <xref:sofab.IStream> + <xref:sofab.IVisitor> — the streaming decoder and its visitor.

## Quick example

```csharp
using sofab;

// encode
byte[] buf = new byte[64];
var os = new OStream(buf);
os.WriteUnsigned(1, 42);
os.WriteSigned(2, -7);
os.WriteString(3, "hi");
int used = os.BytesUsed;

// decode
class My : IVisitor
{
    public ulong A;
    public long B;
    public void Unsigned(int id, ulong v) { if (id == 1) A = v; }
    public void Signed(int id, long v)    { if (id == 2) B = v; }
}
var sink = new My();
new IStream().Feed(buf, 0, used, sink);
```

See the [project README](https://github.com/sofa-buffers/corelib-cs) for the full
guide, benchmarks and the wire-format documentation links.
