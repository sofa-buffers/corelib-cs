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
    /// Start of an array field. The <c>count</c> elements follow through the
    /// scalar / float callbacks with the same <c>id</c>.
    /// </summary>
    /// <param name="id">field id</param>
    /// <param name="kind">element category</param>
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
