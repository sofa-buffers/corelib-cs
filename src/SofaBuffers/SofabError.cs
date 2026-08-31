/*
 * SofaBuffers C# - error codes.
 *
 * SPDX-License-Identifier: MIT
 */

namespace sofab;

/// <summary>
/// Error categories raised by the encoder and decoder.
/// </summary>
/// <remarks>
/// Mirrors the C <c>sofab_ret_t</c> status codes (minus <c>OK</c>, which the C#
/// API models as a normal return). Every <see cref="SofabException"/> carries
/// one of these so callers can branch on the cause without string matching.
/// </remarks>
public enum SofabError
{
    /// <summary>Invalid caller argument (e.g. a field id outside <c>0..ID_MAX</c>).</summary>
    Argument,

    /// <summary>The output buffer is full and no flush sink is available.</summary>
    BufferFull,

    /// <summary>
    /// The input bytes are not a valid Sofab message (varint overflow, a reserved
    /// fixlen subtype, a count above <c>ARRAY_MAX</c>, nesting past
    /// <c>MAX_DEPTH</c>, dangling sequence end, ...).
    /// </summary>
    /// <remarks>
    /// A zero-count array is <em>not</em> in that company: it is a normal
    /// encoding — <c>[ header ][ count=0 ]</c> with no elements (CORELIB_PLAN
    /// §4.7), plus the <c>fixlen_word</c> naming the element subtype for a fixlen
    /// array (§4.8) — and decodes into a single
    /// <see cref="IVisitor.ArrayBegin(int, ArrayKind, int)"/> with a count of 0.
    /// </remarks>
    InvalidMessage,

    /// <summary>
    /// A receiver-configured decode limit was exceeded for an unbounded (schema
    /// declares no <c>count</c> / <c>maxlen</c>) field — the wire count or total
    /// length reported by the decoder callbacks
    /// (<c>array_begin</c>, <c>string</c>, <c>blob</c>) is above a
    /// <c>max_dyn_array_count</c> / <c>max_dyn_string_len</c> /
    /// <c>max_dyn_blob_len</c> cap baked into the generated code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <b>policy, not wire malformation</b>: the message is well-formed,
    /// but a receiver has chosen a stricter ceiling than the wire allows. It is
    /// therefore a category <b>distinct</b> from <see cref="InvalidMessage"/> —
    /// two backends with different configured caps must not read as wire-conformance
    /// divergence. A limit violation is always a hard decode error: the generated
    /// code raises it <em>before</em> allocating, and never clamps or truncates.
    /// </para>
    /// <para>
    /// <b>This corelib holds no limit</b> — no field, no default, no fallback
    /// constant, and no omitted argument read as "unlimited" (CORELIB_PLAN
    /// §6.2.1). The values are generated code's throughout. What it does is
    /// perform the comparison where §6.2.1 wants it, for the two payload kinds
    /// whose length it already sees: <see cref="PayloadAcc.String"/> and
    /// <see cref="PayloadAcc.Blob"/> take the cap as a <em>required argument</em>
    /// and check <c>total</c> at the length header, before a byte is taken. Array
    /// element counts and indices have no such call here — <see cref="Seq"/> only
    /// grows an array generated code owns — so those caps are enforced in
    /// generated code, and, per §6.2.1's "one implementation, wherever it runs",
    /// each rule is enforced in exactly one of the two places.
    /// </para>
    /// <para>
    /// A cap that was never stated is a caller defect, reported as
    /// <see cref="Argument"/>: <see cref="LimitExceeded"/> would promise a limit
    /// to raise that nobody configured. A format ceiling (<c>ARRAY_MAX</c>,
    /// <c>FIXLEN_MAX</c>) is the format's bound, not a receiver cap, and reaching
    /// one stays <see cref="InvalidMessage"/>. Mirrors the Go port's
    /// <c>ErrLimitExceeded</c> sentinel.
    /// </para>
    /// </remarks>
    LimitExceeded,
}
