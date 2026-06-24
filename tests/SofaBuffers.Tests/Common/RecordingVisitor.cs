/*
 * SofaBuffers C# - shared recording visitor for tests.
 *
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SofaBuffers.Tests.Common;

/// <summary>
/// An <see cref="IVisitor"/> that records every callback as a flat list of
/// human-readable events, and reassembles chunked string/blob payloads. Tests
/// assert against the event list, which keeps them independent of how the decoder
/// chunks input.
/// </summary>
public sealed class RecordingVisitor : IVisitor
{
    /// <summary>Recorded events, one per decoded field (string/blob coalesced).</summary>
    public readonly List<string> Events = new();

    private readonly MemoryStream _pending = new();
    private string? _pendingKind;
    private int _pendingId;
    private int _pendingTotal;

    public void Unsigned(int id, ulong value)
    {
        Events.Add("u:" + id + "=" + value.ToString(CultureInfo.InvariantCulture));
    }

    public void Signed(int id, long value)
    {
        Events.Add("s:" + id + "=" + value.ToString(CultureInfo.InvariantCulture));
    }

    public void Fp32(int id, float value)
    {
        Events.Add("f32:" + id + "=" + value.ToString("R", CultureInfo.InvariantCulture));
    }

    public void Fp64(int id, double value)
    {
        Events.Add("f64:" + id + "=" + value.ToString("R", CultureInfo.InvariantCulture));
    }

    public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
        Accumulate("str", id, total, data, chunkOffset, chunkLength);
    }

    public void Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
        Accumulate("blob", id, total, data, chunkOffset, chunkLength);
    }

    private void Accumulate(string kind, int id, int total, byte[] data, int chunkOffset, int chunkLength)
    {
        if (_pendingKind == null)
        {
            _pendingKind = kind;
            _pendingId = id;
            _pendingTotal = total;
            _pending.SetLength(0);
        }
        _pending.Write(data, chunkOffset, chunkLength);
        if (_pending.Length >= _pendingTotal)
        {
            byte[] full = _pending.ToArray();
            if (_pendingKind == "str")
            {
                Events.Add("str:" + _pendingId + "=" + Encoding.UTF8.GetString(full));
            }
            else
            {
                Events.Add("blob:" + _pendingId + "=" + Hex(full));
            }
            _pendingKind = null;
        }
    }

    public void ArrayBegin(int id, ArrayKind kind, int count)
    {
        Events.Add("arr:" + id + ":" + kind.ToString().ToUpperInvariant() + ":" + count);
    }

    public void SequenceBegin(int id)
    {
        Events.Add("seq{:" + id);
    }

    public void SequenceEnd()
    {
        Events.Add("seq}");
    }

    private static string Hex(byte[] b)
    {
        var sb = new StringBuilder();
        foreach (byte x in b)
        {
            sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
