/*
 * SofaBuffers C# - the leaf the two header-ceiling blocks share.
 *
 * `header_limits` feeds a TRUNCATED OVER-CEILING HEADER at the top level;
 * `header_limits_nested` feeds the same header one or two sequence frames deeper.
 * The two blocks are required to differ in WHERE THE FIELD ARRIVES and in nothing
 * else, so the destination that judges the declared size lives here and is used
 * verbatim by both runners: a nested block with a leaf of its own could pass by a
 * different mechanism than the one under test, which is exactly what the nested
 * block exists to rule out.
 *
 *   02 a2 06   then EOF
 *   ^^ id 0, wire type 2 (fixlen)
 *      ^^^^^ the length word (100 << 3) | 2 -- a 100-byte STRING is declared
 *              ... and the message ends.
 *
 * A conformant decoder answers AT THAT WORD, before the payload is asked for, so
 * the answer is the ceiling's and it is TERMINAL. Which ceiling speaks decides the
 * category, and the two give opposite answers on the same word:
 *
 *   "schema": { "maxlen": N }      -> a breach is INVALID         (MESSAGE_SPEC §7.1)
 *   "limits": { "max_dyn_...": N } -> a breach is LIMIT_EXCEEDED  (CORELIB_PLAN §6.2.1)
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Linq;
using System.Text.Json;

namespace SofaBuffers.Tests.Common;

/// <summary>
/// What this port can do, as the header-ceiling blocks' <c>requires</c> tags ask
/// it. One table, because both blocks ask the same questions and an answer kept
/// twice drifts.
/// </summary>
public static class PortCapability
{
    /// <summary>
    /// This port's answer to the <c>receiver_caps</c> capability both blocks gate
    /// on.
    /// </summary>
    /// <remarks>
    /// Like <c>dynamic_arrays</c> on the growth block, and unlike the wire-construct
    /// tags a vector carries, this is a PROFILE capability: a port declares it when
    /// its generated code carries §6.2.1 receiver caps DISTINCT from schema bounds.
    /// This one does. A cap is a required argument that is never held and never
    /// defaulted (<see cref="PayloadAcc"/>), a schema bound is generated code's own
    /// number, the two are mutually exclusive per field, and a breach of one is
    /// <see cref="SofabError.LimitExceeded"/> where a breach of the other is
    /// <see cref="SofabError.InvalidMessage"/>.
    /// </remarks>
    public const bool ReceiverCaps = true;

    /// <summary>
    /// Whether this port satisfies a tag it KNOWS, or <c>null</c> for one it does
    /// not recognize. What an unrecognized tag means is the caller's policy, and
    /// the two blocks answer it differently, so it is not decided here.
    /// </summary>
    /// <remarks>
    /// This is a full-wire-format implementation with no build switches, so the
    /// construct tags are all satisfied and only the profile tag is a real question.
    /// </remarks>
    /// <param name="tag">a <c>requires</c> tag</param>
    /// <returns>the answer, or <c>null</c> when the tag is unknown to this port</returns>
    public static bool? Known(string tag) => tag switch
    {
        "receiver_caps" => ReceiverCaps,
        "fixlen" or "array" or "sequence" or "fp32" or "fp64" or "int64" => true,
        _ => null,
    };
}

/// <summary>Which ceiling a case configures: exactly one of the two.</summary>
public enum Ceiling
{
    /// <summary>A schema <c>maxlen</c> / <c>count</c>; a breach is INVALID.</summary>
    Schema,

    /// <summary>A §6.2.1 receiver cap; a breach is LIMIT_EXCEEDED.</summary>
    Cap,
}

/// <summary>The construct a case's bytes open, read off the bytes themselves.</summary>
public enum Construct
{
    /// <summary>A fixlen field of subtype string.</summary>
    String,

    /// <summary>A fixlen field of subtype blob.</summary>
    Blob,

    /// <summary>A varint array, whose header declares an element count.</summary>
    Array,
}

/// <summary>
/// The destination a generated message class would be for one header-ceiling
/// case's field: it judges the declared length or count at the header hook, and
/// binds the payload behind it.
/// </summary>
/// <remarks>
/// <para>
/// The ceiling is applied at <see cref="IVisitor.FixlenBegin"/> /
/// <see cref="IVisitor.ArrayBegin"/> -- the word that declares the size, which
/// §6.2.1 names as the enforcement point ("before the allocation it is meant to
/// prevent") and which the decoder raises before any payload byte. For a string or
/// a blob the comparison itself is not restated here: it is
/// <see cref="PayloadAcc.CheckStringLength"/> / <see cref="PayloadAcc.CheckBlobLength"/>,
/// the library's own §6.2.1 check, so these cases bite on the library rather than
/// on an assertion written in a test file.
/// </para>
/// <para>
/// The corelib holds no limit -- no field, no default, no fallback constant
/// (§6.2.1) -- so the numbers are generated code's and this class stands in for
/// that layer. The two ceilings are wired in mutually exclusively, exactly as the
/// case states them: a <c>schema</c> case gets a bound this destination enforces
/// itself and NO cap, a <c>limits</c> case gets the cap and NO schema bound.
/// Neither route can borrow the other's number.
/// </para>
/// <para>
/// It is deliberately depth-blind. The nested block reaches it through a frame
/// gate that forwards only what arrives at the case's frame chain
/// (<c>HeaderLimitsNestedTests</c>), so the leaf behaviour the two blocks assert is
/// one implementation, not two.
/// </para>
/// </remarks>
public sealed class HeaderDest : IVisitor
{
    private readonly int _fieldId;
    private readonly Construct _construct;
    private readonly Ceiling _kind;
    private readonly long _bound;
    private readonly PayloadAcc _acc = new();

    /// <summary>
    /// How many values this destination actually bound. Every case in either
    /// block ends at or before the length word, so it must stay 0 -- which is only
    /// worth asserting because the counter genuinely moves when a payload does
    /// arrive.
    /// </summary>
    public int BoundValues { get; private set; }

    /// <summary>The field id, the construct and the single ceiling the case states.</summary>
    /// <param name="fieldId">the target field's id inside its innermost scope</param>
    /// <param name="construct">the construct the case's bytes open</param>
    /// <param name="kind">which of the two ceilings the case configures</param>
    /// <param name="bound">that ceiling's value</param>
    public HeaderDest(int fieldId, Construct construct, Ceiling kind, long bound)
    {
        _fieldId = fieldId;
        _construct = construct;
        _kind = kind;
        _bound = bound;
    }

    /// <summary>The declared length judged at the length word, before any payload byte.</summary>
    /// <param name="id">field id</param>
    /// <param name="subtype">the fixlen subtype that arrived</param>
    /// <param name="total">the declared payload length</param>
    public void FixlenBegin(int id, FixlenType subtype, int total)
    {
        if (id != _fieldId || !IsOurs(subtype))
        {
            // A field this destination does not read: MESSAGE_SPEC §7.3 skips it,
            // and §6.2.1 never caps a skipped field.
            return;
        }
        if (_kind == Ceiling.Schema)
        {
            // A schema bound is a statement about VALIDITY (MESSAGE_SPEC §7.1):
            // the number that exceeds it is already on the wire and no later byte
            // can make it legal, so the field is malformed, not declined.
            if (total > _bound)
            {
                throw new SofabException(SofabError.InvalidMessage,
                    $"declared length {total} over schema maxlen {_bound}");
            }
            return;
        }
        // A receiver cap: the bytes are well-formed and the same header decodes
        // under a looser cap, so the refusal is LimitExceeded (§6.2.1, §6.3).
        if (subtype == FixlenType.String)
        {
            PayloadAcc.CheckStringLength(total, _bound);
        }
        else
        {
            PayloadAcc.CheckBlobLength(total, _bound);
        }
    }

    /// <summary>
    /// The declared element count, judged the same way and at the same point.
    /// </summary>
    /// <remarks>
    /// A COUNT ahead of its payload is bound exactly as a length is (§6.2.1) -- and
    /// unlike a wrapper array, which carries no count on the wire and is bound at
    /// the element index instead (SequenceGrowthTests). The corelib offers no call
    /// for this one: array counts are generated code's throughout (see
    /// <see cref="SofabError.LimitExceeded"/>), so the comparison is here.
    /// </remarks>
    /// <param name="id">field id</param>
    /// <param name="kind">element category</param>
    /// <param name="count">the declared element count</param>
    public void ArrayBegin(int id, ArrayKind kind, int count)
    {
        if (id != _fieldId || _construct != Construct.Array)
        {
            return;
        }
        if (count > _bound)
        {
            throw new SofabException(
                _kind == Ceiling.Schema ? SofabError.InvalidMessage : SofabError.LimitExceeded,
                _kind == Ceiling.Schema
                    ? $"declared count {count} over schema count {_bound}"
                    : $"declared count {count} over max_dyn_array_count {_bound}");
        }
    }

    /// <summary>A chunk of the string payload behind the judged length word.</summary>
    /// <param name="id">field id</param>
    /// <param name="total">full field length</param>
    /// <param name="offset">position of this chunk within the field</param>
    /// <param name="data">backing array</param>
    /// <param name="chunkOffset">start of the chunk within <paramref name="data"/></param>
    /// <param name="chunkLength">length of the chunk</param>
    public void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
        if (id != _fieldId || _construct != Construct.String) { return; }
        if (_acc.String(total, offset, data, chunkOffset, chunkLength, _bound) != null)
        {
            BoundValues++;
        }
    }

    /// <summary>A chunk of the blob payload behind the judged length word.</summary>
    /// <param name="id">field id</param>
    /// <param name="total">full field length</param>
    /// <param name="offset">position of this chunk within the field</param>
    /// <param name="data">backing array</param>
    /// <param name="chunkOffset">start of the chunk within <paramref name="data"/></param>
    /// <param name="chunkLength">length of the chunk</param>
    public void Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
        if (id != _fieldId || _construct != Construct.Blob) { return; }
        if (_acc.Blob(total, offset, data, chunkOffset, chunkLength, _bound) != null)
        {
            BoundValues++;
        }
    }

    /// <summary>An unsigned array element behind the judged count word.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">the element value</param>
    public void Unsigned(int id, ulong value)
    {
        if (id == _fieldId && _construct == Construct.Array) { BoundValues++; }
    }

    /// <summary>A signed array element behind the judged count word.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">the element value</param>
    public void Signed(int id, long value)
    {
        if (id == _fieldId && _construct == Construct.Array) { BoundValues++; }
    }

    /// <summary>
    /// Whether an arrived fixlen subtype is the one this destination reads. The
    /// corelib is schema-agnostic and reports the subtype that ARRIVED, so a
    /// receiver whose schema names another one treats the field as a MESSAGE_SPEC
    /// §7.3 skip and does not measure it against this field's bound.
    /// </summary>
    private bool IsOurs(FixlenType subtype) =>
        (_construct == Construct.String && subtype == FixlenType.String)
        || (_construct == Construct.Blob && subtype == FixlenType.Blob);
}

/// <summary>
/// Reading one header-ceiling case: the single ceiling it states, and the shape
/// its bytes actually carry.
/// </summary>
public static class HeaderCeiling
{
    /// <summary>
    /// Read the single ceiling a case states, under the key naming the bound.
    /// </summary>
    /// <remarks>
    /// A case carries <c>schema</c> or <c>limits</c> and never both -- §6.2.1
    /// forbids applying a receiver cap to a field the schema already bounds -- and
    /// the object names exactly one bound. Both are checked here rather than
    /// assumed, because a case that stated two ceilings would silently test
    /// whichever the runner happened to read first.
    /// </remarks>
    /// <param name="c">the case object</param>
    /// <param name="name">the case name, for the failure text</param>
    /// <returns>which ceiling it is, the key naming it, and its value</returns>
    public static (Ceiling Kind, string Name, long Bound) ReadCeiling(JsonElement c, string name)
    {
        bool hasSchema = c.TryGetProperty("schema", out JsonElement schema);
        bool hasLimits = c.TryGetProperty("limits", out JsonElement limits);
        if (hasSchema == hasLimits)
        {
            throw new InvalidOperationException(
                $"{name}: a case carries exactly one of `schema` and `limits` (§6.2.1)");
        }

        JsonElement owner = hasSchema ? schema : limits;
        JsonProperty[] bounds = owner.EnumerateObject().ToArray();
        if (bounds.Length != 1)
        {
            throw new InvalidOperationException(
                $"{name}: the ceiling names {bounds.Length} bounds, want exactly one");
        }
        return (hasSchema ? Ceiling.Schema : Ceiling.Cap, bounds[0].Name, bounds[0].Value.GetInt64());
    }

    /// <summary>
    /// The construct the case's bytes declare, after stepping through the sequence
    /// frames the case says the field is nested in.
    /// </summary>
    /// <remarks>
    /// The bounds in these blocks are ABSOLUTE, not cap-relative as in
    /// <c>sequence_growth</c>: the case IS a fixed byte string, so the declared
    /// number is baked into the varint and the case instead TELLS the port which
    /// ceiling to configure. What the case does not spell out is which CONSTRUCT
    /// the header opens, so it is read off the bytes -- the one description that
    /// cannot drift from them -- and cross-checked against <c>frames</c>,
    /// <c>field_id</c> and <c>declared</c>. A disagreement means the block was
    /// hand-edited rather than copied verbatim.
    /// </remarks>
    /// <param name="name">the case name, for the failure text</param>
    /// <param name="serialized">the case's <c>serialized</c> hex</param>
    /// <param name="frames">the sequence ids the field is nested in, outermost first (empty at the top level)</param>
    /// <param name="fieldId">the <c>field_id</c> the case states</param>
    /// <param name="declared">the <c>declared</c> number the case states</param>
    /// <returns>the construct, the field id and the declared number the bytes carry</returns>
    public static (Construct Kind, int Id, long Declared) ReadShape(
        string name, string serialized, int[] frames, int fieldId, long declared)
    {
        byte[] raw = Convert.FromHexString(serialized);
        int at = 0;

        // The frame chain first, outermost first: every id the case names must be
        // on the wire as a SEQUENCE OPEN, in that order, ahead of the target field.
        for (int d = 0; d < frames.Length; d++)
        {
            ulong frame = Varint(name, raw, ref at);
            if ((frame & 0x07) != 0x6) // T_SEQUENCE_START
            {
                throw new InvalidOperationException(
                    $"{name}: frame {d} opens with wire type {frame & 0x07}, want a sequence (6)");
            }
            int frameId = checked((int)(frame >> 3));
            if (frameId != frames[d])
            {
                throw new InvalidOperationException(
                    $"{name}: frame {d} on the wire is id {frameId}, the case says {frames[d]}");
            }
        }

        ulong header = Varint(name, raw, ref at);
        int id = checked((int)(header >> 3));
        Construct kind;
        long onTheWire;

        switch (header & 0x07)
        {
            case 0x2: // T_FIXLEN
                ulong word = Varint(name, raw, ref at);
                onTheWire = checked((long)(word >> 3));
                kind = (word & 0x07) switch
                {
                    0x2 => Construct.String,
                    0x3 => Construct.Blob,
                    _ => throw new InvalidOperationException(
                        $"{name}: fixlen subtype {word & 0x07} carries no ceiling of its own"),
                };
                break;
            case 0x3: // T_VARINTARRAY_UNSIGNED
            case 0x4: // T_VARINTARRAY_SIGNED
                onTheWire = checked((long)Varint(name, raw, ref at));
                kind = Construct.Array;
                break;
            default:
                throw new InvalidOperationException(
                    $"{name}: wire type {header & 0x07} opens no length or count header");
        }

        if (id != fieldId)
        {
            throw new InvalidOperationException($"{name}: bytes carry id {id}, the case says {fieldId}");
        }
        if (onTheWire != declared)
        {
            throw new InvalidOperationException(
                $"{name}: bytes declare {onTheWire}, the case says {declared}");
        }
        return (kind, id, onTheWire);
    }

    /// <summary>
    /// One varint out of the case's header. The header is complete in
    /// <c>serialized</c> even where <c>chunks</c> splits it, so a truncated varint
    /// here is a corrupt case rather than an expected outcome.
    /// </summary>
    /// <param name="name">the case name, for the failure text</param>
    /// <param name="b">the case's bytes</param>
    /// <param name="at">the read cursor, advanced past the varint</param>
    /// <returns>the varint's value</returns>
    public static ulong Varint(string name, byte[] b, ref int at)
    {
        ulong v = 0;
        int shift = 0;
        while (at < b.Length)
        {
            byte x = b[at++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) { return v; }
            shift += 7;
            if (shift > 63)
            {
                throw new InvalidOperationException($"{name}: varint wider than 64 bits");
            }
        }
        throw new InvalidOperationException($"{name}: serialized ends inside a varint");
    }
}
