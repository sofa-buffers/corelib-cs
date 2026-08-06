/*
 * SofaBuffers C# - decoder visitor.
 *
 * SPDX-License-Identifier: MIT
 */

namespace sofab;

/// <summary>
/// Receives decoded fields pushed by an <see cref="IStream"/>.
/// </summary>
/// <remarks>
/// The decoder follows the <em>visitor pattern</em>: rather than binding a
/// destination buffer per field (as the C API does), it calls back into an
/// <see cref="IVisitor"/> as each field is decoded. Every method has a default
/// no-op implementation (a C# default-interface method), so an implementor
/// overrides only the field kinds it cares about; unhandled fields are simply
/// dropped (the equivalent of "not interested" / skip in the C API). This keeps
/// generated message classes small: a generated visitor is typically one
/// <c>switch</c> on the field id.
/// <para>
/// <b>Streaming contract.</b> Scalars and floats are delivered whole. String and
/// blob payloads are delivered in one or more chunks so they can exceed the input
/// chunk size (and even RAM); each chunk reports the field <c>total</c> length
/// and the byte <c>offset</c> of the chunk within the field. Array elements are
/// announced once via <see cref="ArrayBegin"/> and then delivered through the
/// scalar / float callbacks with the same <c>id</c>.
/// </para>
/// <para>
/// <b>Header hooks.</b> A field that declares a size is announced on the
/// <em>word that declares it</em>, ahead of any payload: <see cref="ArrayBegin"/>
/// for an array's element count, and <see cref="FixlenBegin"/> for a fixlen
/// field's byte length. A receiver holding a schema <c>count</c> / <c>maxlen</c>
/// bound judges it there, so the verdict cannot depend on where the input was
/// chunked (CORELIB_PLAN §5.2, §6.4).
/// </para>
/// <para>
/// <b>Buffer ownership.</b> The <c>data</c> array handed to <see cref="String"/>
/// and <see cref="Blob"/> is the caller's input buffer; it is only valid for the
/// duration of the call. A visitor that needs to retain bytes must copy the
/// <c>[chunkOffset, chunkOffset + chunkLength)</c> range.
/// </para>
/// </remarks>
public interface IVisitor
{
    /// <summary>An unsigned-integer field, or an unsigned array element.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">the unsigned 64-bit value</param>
    void Unsigned(int id, ulong value)
    {
    }

    /// <summary>A signed-integer field, or a signed array element.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">the value</param>
    void Signed(int id, long value)
    {
    }

    /// <summary>A 32-bit float field, or an <c>fp32</c> array element.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">the value</param>
    void Fp32(int id, float value)
    {
    }

    /// <summary>A 64-bit float field, or an <c>fp64</c> array element.</summary>
    /// <param name="id">field id</param>
    /// <param name="value">the value</param>
    void Fp64(int id, double value)
    {
    }

    /// <summary>
    /// A chunk of a string field (raw UTF-8 bytes, no NUL terminator).
    /// </summary>
    /// <remarks>For an empty string this is called once with <c>total == 0</c> and
    /// <c>chunkLength == 0</c>.</remarks>
    /// <param name="id">field id</param>
    /// <param name="total">full field length in bytes</param>
    /// <param name="offset">byte position of this chunk within the field</param>
    /// <param name="data">backing array containing the chunk</param>
    /// <param name="chunkOffset">start of the chunk within <c>data</c></param>
    /// <param name="chunkLength">number of bytes in the chunk</param>
    void String(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
    }

    /// <summary>
    /// A chunk of a blob field. See <see cref="String"/> for the chunking model.
    /// </summary>
    /// <param name="id">field id</param>
    /// <param name="total">full field length in bytes</param>
    /// <param name="offset">byte position of this chunk within the field</param>
    /// <param name="data">backing array containing the chunk</param>
    /// <param name="chunkOffset">start of the chunk within <c>data</c></param>
    /// <param name="chunkLength">number of bytes in the chunk</param>
    void Blob(int id, int total, int offset, byte[] data, int chunkOffset, int chunkLength)
    {
    }

    /// <summary>
    /// Start of a fixed-length field, announced once the <c>fixlen_word</c> has
    /// been read and validated and before any payload byte is delivered.
    /// </summary>
    /// <remarks>
    /// Called exactly once per fixlen field — <c>fp32</c>, <c>fp64</c>,
    /// <c>string</c> and <c>blob</c> alike, <paramref name="total"/> <c>== 0</c>
    /// included — always before the matching <see cref="Fp32"/> /
    /// <see cref="Fp64"/> / <see cref="String"/> / <see cref="Blob"/> call, and
    /// never per payload chunk. This is the <see cref="ArrayBegin"/> of the
    /// fixlen world, and it exists for the same reason.
    /// <para>
    /// <b>Why the decoder has to be the one to say it.</b> A schema
    /// <c>maxlen</c> bound is fully established by the length word: the number
    /// that exceeds it is already on the wire, and no later byte can make it
    /// legal. CORELIB_PLAN §5.2 makes <c>INVALID</c> dominate <c>INCOMPLETE</c>,
    /// so a message ending exactly at that word is malformed — but the payload
    /// callbacks carry <c>total</c> only once payload bytes exist, so such a
    /// message would deliver no event at all and degrade to <c>INCOMPLETE</c>.
    /// The verdict would then depend on where the input happened to be chunked,
    /// which §6.4 and §7.2 forbid outright. Raising from this callback is what
    /// turns the field into <c>INVALID</c> at the only point where that decision
    /// is chunk-independent.
    /// </para>
    /// <para>
    /// <paramref name="subtype"/> is the subtype that <em>arrived</em>, not the
    /// one that was <em>declared</em> — the corelib is schema-agnostic. A
    /// receiver whose schema names a different subtype must treat the field as a
    /// MESSAGE_SPEC §7.3 skip and <em>not</em> measure it against this field's
    /// bound, exactly as it does for a mistyped <see cref="ArrayBegin"/>. What
    /// the format itself rejects — a reserved subtype, or an <c>fp32</c>/
    /// <c>fp64</c> of the wrong width (§4.6) — is <c>INVALID</c> and is judged
    /// before this call, so a subtype seen here is always one of the four.
    /// </para>
    /// <para>
    /// A fixlen <em>array</em> is announced by <see cref="ArrayBegin"/> instead,
    /// whose <see cref="ArrayKind"/> already carries the element subtype; its
    /// shared <c>fixlen_word</c> does not raise this hook.
    /// </para>
    /// </remarks>
    /// <param name="id">field id</param>
    /// <param name="subtype">the resolved fixlen subtype that arrived on the wire</param>
    /// <param name="total">declared payload length in bytes (4 / 8 for a float)</param>
    void FixlenBegin(int id, FixlenType subtype, int total)
    {
    }

    /// <summary>
    /// Start of an array field. The <c>count</c> elements follow through the
    /// scalar / float callbacks with the same <c>id</c>.
    /// </summary>
    /// <remarks>
    /// Called exactly once per array field, never per element, and always before
    /// the first element. <em>When</em> it is called depends on the wire type:
    /// <list type="bullet">
    /// <item><description>
    /// An integer array (<c>ARRAY_UNSIGNED</c> / <c>ARRAY_SIGNED</c>) is announced
    /// immediately after its element-count varint — the wire type already fixes
    /// the element kind.
    /// </description></item>
    /// <item><description>
    /// A fixlen array (<c>ARRAY_FIXLEN</c>) is announced only after its
    /// <c>fixlen_word</c> has been read and validated, so <paramref name="kind"/>
    /// is the true element subtype, <see cref="ArrayKind.Fp32"/> or
    /// <see cref="ArrayKind.Fp64"/>. CORELIB_PLAN §4.8 fixes this order: a
    /// receiver must be able to decide the field is not its array's value
    /// (MESSAGE_SPEC §7.3, a wrong subtype ⇒ skip) <em>before</em> it applies any
    /// schema bound to <paramref name="count"/>. A zero-count fixlen array still
    /// carries its word, and is still announced exactly once.
    /// </description></item>
    /// </list>
    /// The format ceiling on <paramref name="count"/> is enforced on the count
    /// varint, ahead of this call, so an absurd count is rejected without any
    /// allocation whatever the subtype turns out to be.
    /// </remarks>
    /// <param name="id">field id</param>
    /// <param name="kind">element category; for a fixlen array, its element subtype</param>
    /// <param name="count">number of elements</param>
    void ArrayBegin(int id, ArrayKind kind, int count)
    {
    }

    /// <summary>Start of a nested sequence (a new id scope).</summary>
    /// <param name="id">field id of the sequence</param>
    void SequenceBegin(int id)
    {
    }

    /// <summary>End of the current nested sequence.</summary>
    void SequenceEnd()
    {
    }
}
