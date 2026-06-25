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

    /// <summary>Invalid API usage (e.g. a decoded value does not fit the requested type).</summary>
    Usage,

    /// <summary>The output buffer is full and no flush sink is available.</summary>
    BufferFull,

    /// <summary>
    /// The input bytes are not a valid Sofab message (varint overflow, bad type
    /// tag, zero-length array, dangling sequence end, ...).
    /// </summary>
    InvalidMessage,
}
