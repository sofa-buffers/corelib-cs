/*
 * SofaBuffers C# - shared conformance suite.
 *
 * Replays the language-agnostic test vectors (assets/test_vectors.json, copied
 * verbatim from the documentation repo) through the encoder and decoder, as
 * required by the SofaBuffers architecture spec (ARCHITECTURE.md §7). Each
 * vector is exercised three ways:
 *
 *   1. encode      -- replay fields at the given offset; bytes must equal serialized.hex
 *   2. decode      -- feed serialized.hex; decoded fields must match fields[]
 *   3. decode 1-by-1 -- feed one byte at a time; result must match the whole-feed decode
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SofaBuffers.Tests;

public class TestVectorsConformanceTests
{
    // --- vector model + loader ---------------------------------------------

    private sealed class Vector
    {
        public string Name = "";
        public int Offset;
        public JsonElement[] Fields = Array.Empty<JsonElement>();
        public byte[] Expected = Array.Empty<byte>();
    }

    private static readonly Dictionary<string, Vector> Vectors = Load();

    private static Dictionary<string, Vector> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "test_vectors.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var map = new Dictionary<string, Vector>();
        foreach (JsonElement v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var vec = new Vector
            {
                Name = v.GetProperty("name").GetString()!,
                Offset = v.GetProperty("offset").GetInt32(),
                // Clone so the elements outlive the JsonDocument.
                Fields = v.GetProperty("fields").EnumerateArray().Select(f => f.Clone()).ToArray(),
                Expected = Convert.FromHexString(v.GetProperty("serialized").GetProperty("hex").GetString()!),
            };
            map[vec.Name] = vec;
        }
        return map;
    }

    /// <summary>One xUnit case per vector, keyed by name.</summary>
    public static IEnumerable<object[]> VectorNames => Vectors.Keys.Select(n => new object[] { n });

    // --- helpers ------------------------------------------------------------

    private static int Id(JsonElement f) => f.GetProperty("id").GetInt32();

    /// <summary>A finite JSON number, or the literals "inf" / "-inf".</summary>
    private static double Fp(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            string s = e.GetString()!;
            return s switch
            {
                "inf" => double.PositiveInfinity,
                "-inf" => double.NegativeInfinity,
                _ => double.Parse(s, CultureInfo.InvariantCulture),
            };
        }
        return e.GetDouble();
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // --- encode replay ------------------------------------------------------

    private static void ReplayEncode(OStream os, JsonElement[] fields)
    {
        foreach (JsonElement f in fields)
        {
            switch (f.GetProperty("op").GetString())
            {
                case "unsigned": os.WriteUnsigned(Id(f), f.GetProperty("value").GetUInt64()); break;
                case "signed": os.WriteSigned(Id(f), f.GetProperty("value").GetInt64()); break;
                case "boolean": os.WriteBoolean(Id(f), f.GetProperty("value").GetBoolean()); break;
                case "fp32": os.WriteFp32(Id(f), (float)Fp(f.GetProperty("value"))); break;
                case "fp64": os.WriteFp64(Id(f), Fp(f.GetProperty("value"))); break;
                case "string": os.WriteString(Id(f), f.GetProperty("value").GetString()!); break;
                case "blob": os.WriteBlob(Id(f), Convert.FromHexString(f.GetProperty("value_hex").GetString()!)); break;
                case "array": ReplayArray(os, f); break;
                case "sequence_begin": os.WriteSequenceBegin(Id(f)); break;
                case "sequence_end": os.WriteSequenceEnd(); break;
                default: throw new InvalidOperationException("unknown op " + f.GetProperty("op").GetString());
            }
        }
    }

    private static void ReplayArray(OStream os, JsonElement f)
    {
        int id = Id(f);
        JsonElement vals = f.GetProperty("values");
        string et = f.GetProperty("element_type").GetString()!;
        switch (et)
        {
            case "u8": os.WriteArrayUnsigned(id, vals.EnumerateArray().Select(x => (byte)x.GetUInt64()).ToArray()); break;
            case "u16": os.WriteArrayUnsigned(id, vals.EnumerateArray().Select(x => (ushort)x.GetUInt64()).ToArray()); break;
            case "u32": os.WriteArrayUnsigned(id, vals.EnumerateArray().Select(x => (uint)x.GetUInt64()).ToArray()); break;
            case "u64": os.WriteArrayUnsigned(id, vals.EnumerateArray().Select(x => x.GetUInt64()).ToArray()); break;
            case "i8": os.WriteArraySigned(id, vals.EnumerateArray().Select(x => (sbyte)x.GetInt64()).ToArray()); break;
            case "i16": os.WriteArraySigned(id, vals.EnumerateArray().Select(x => (short)x.GetInt64()).ToArray()); break;
            case "i32": os.WriteArraySigned(id, vals.EnumerateArray().Select(x => (int)x.GetInt64()).ToArray()); break;
            case "i64": os.WriteArraySigned(id, vals.EnumerateArray().Select(x => x.GetInt64()).ToArray()); break;
            case "fp32": os.WriteArrayFp32(id, vals.EnumerateArray().Select(x => (float)Fp(x)).ToArray()); break;
            case "fp64": os.WriteArrayFp64(id, vals.EnumerateArray().Select(x => Fp(x)).ToArray()); break;
            default: throw new InvalidOperationException("unknown element_type " + et);
        }
    }

    // --- decode: normalized event tokens -----------------------------------

    /// <summary>Records decoded fields as flat tokens, coalescing string/blob chunks.</summary>
    private sealed class TokenVisitor : IVisitor
    {
        public readonly List<string> Tokens = new();
        private readonly MemoryStream _pending = new();
        private string? _kind;
        private int _id;
        private int _total;

        public void Unsigned(int id, ulong v) => Tokens.Add($"u:{id}={v}");
        public void Signed(int id, long v) => Tokens.Add($"s:{id}={v}");
        public void Fp32(int id, float v) => Tokens.Add($"f32:{id}={BitConverter.SingleToInt32Bits(v)}");
        public void Fp64(int id, double v) => Tokens.Add($"f64:{id}={BitConverter.DoubleToInt64Bits(v)}");
        public void String(int id, int total, int offset, byte[] d, int o, int l) => Chunk("str", id, total, d, o, l);
        public void Blob(int id, int total, int offset, byte[] d, int o, int l) => Chunk("blob", id, total, d, o, l);
        public void ArrayBegin(int id, ArrayKind kind, int count) => Tokens.Add($"arr:{id}:{kind}:{count}");
        public void SequenceBegin(int id) => Tokens.Add($"seq{{:{id}");
        public void SequenceEnd() => Tokens.Add("seq}");

        private void Chunk(string kind, int id, int total, byte[] d, int o, int l)
        {
            if (_kind == null)
            {
                _kind = kind;
                _id = id;
                _total = total;
                _pending.SetLength(0);
            }
            _pending.Write(d, o, l);
            if (_pending.Length >= _total)
            {
                Tokens.Add($"{_kind}:{_id}={Hex(_pending.ToArray())}");
                _kind = null;
            }
        }
    }

    /// <summary>Builds the expected token stream directly from the vector's fields[].</summary>
    private static List<string> ExpectedTokens(JsonElement[] fields)
    {
        var t = new List<string>();
        foreach (JsonElement f in fields)
        {
            int id = f.TryGetProperty("id", out JsonElement idEl) ? idEl.GetInt32() : 0;
            switch (f.GetProperty("op").GetString())
            {
                case "unsigned": t.Add($"u:{id}={f.GetProperty("value").GetUInt64()}"); break;
                case "signed": t.Add($"s:{id}={f.GetProperty("value").GetInt64()}"); break;
                case "boolean": t.Add($"u:{id}={(f.GetProperty("value").GetBoolean() ? 1 : 0)}"); break;
                case "fp32": t.Add($"f32:{id}={BitConverter.SingleToInt32Bits((float)Fp(f.GetProperty("value")))}"); break;
                case "fp64": t.Add($"f64:{id}={BitConverter.DoubleToInt64Bits(Fp(f.GetProperty("value")))}"); break;
                case "string": t.Add($"str:{id}={Hex(Encoding.UTF8.GetBytes(f.GetProperty("value").GetString()!))}"); break;
                case "blob": t.Add($"blob:{id}={f.GetProperty("value_hex").GetString()!.ToLowerInvariant()}"); break;
                case "sequence_begin": t.Add($"seq{{:{id}"); break;
                case "sequence_end": t.Add("seq}"); break;
                case "array": ExpectedArrayTokens(t, f, id); break;
                default: throw new InvalidOperationException("unknown op");
            }
        }
        return t;
    }

    private static void ExpectedArrayTokens(List<string> t, JsonElement f, int id)
    {
        JsonElement vals = f.GetProperty("values");
        string et = f.GetProperty("element_type").GetString()!;
        int count = vals.GetArrayLength();
        bool signed = et[0] == 'i';
        bool fp = et[0] == 'f';
        ArrayKind kind = fp ? ArrayKind.Fixlen : (signed ? ArrayKind.Signed : ArrayKind.Unsigned);
        t.Add($"arr:{id}:{kind}:{count}");
        foreach (JsonElement x in vals.EnumerateArray())
        {
            if (et == "fp32")
            {
                t.Add($"f32:{id}={BitConverter.SingleToInt32Bits((float)Fp(x))}");
            }
            else if (et == "fp64")
            {
                t.Add($"f64:{id}={BitConverter.DoubleToInt64Bits(Fp(x))}");
            }
            else if (signed)
            {
                t.Add($"s:{id}={x.GetInt64()}");
            }
            else
            {
                t.Add($"u:{id}={x.GetUInt64()}");
            }
        }
    }

    // --- the three tests ----------------------------------------------------

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void EncodeMatchesVector(string name)
    {
        Vector v = Vectors[name];
        var buf = new byte[v.Expected.Length + v.Offset + 16];
        var os = new OStream(buf, v.Offset);
        ReplayEncode(os, v.Fields);

        // The produced message is the bytes after the reserved offset prefix.
        var produced = new byte[os.BytesUsed - v.Offset];
        Array.Copy(buf, v.Offset, produced, 0, produced.Length);
        Assert.Equal(v.Expected, produced);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void DecodeMatchesVector(string name)
    {
        Vector v = Vectors[name];
        var visitor = new TokenVisitor();
        new IStream().Feed(v.Expected, visitor);
        Assert.Equal(ExpectedTokens(v.Fields), visitor.Tokens);
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void DecodeByteByByteMatchesWhole(string name)
    {
        Vector v = Vectors[name];

        var whole = new TokenVisitor();
        new IStream().Feed(v.Expected, whole);

        var oneByOne = new TokenVisitor();
        var iss = new IStream();
        foreach (byte b in v.Expected)
        {
            iss.Feed(new[] { b }, oneByOne);
        }

        Assert.Equal(whole.Tokens, oneByOne.Tokens);
    }
}
