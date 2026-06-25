/*
 * SofaBuffers C# - shared wire constants and varint/zigzag codecs.
 *
 * SPDX-License-Identifier: MIT
 */

namespace sofab;

/// <summary>
/// Wire constants and the base-128 varint / ZigZag helpers shared by the encoder
/// and decoder.
/// </summary>
/// <remarks>
/// Internal: these are an implementation detail of the <c>sofab</c> core, not
/// part of the public API.
/// </remarks>
internal static class WireFormat
{
    // --- field-header 3-bit type tags (low 3 bits of the id header varint) ---
    internal const int T_VARINT_UNSIGNED = 0x0;
    internal const int T_VARINT_SIGNED = 0x1;
    internal const int T_FIXLEN = 0x2;
    internal const int T_VARINTARRAY_UNSIGNED = 0x3;
    internal const int T_VARINTARRAY_SIGNED = 0x4;
    internal const int T_FIXLENARRAY = 0x5;
    internal const int T_SEQUENCE_START = 0x6;
    internal const int T_SEQUENCE_END = 0x7;

    /// <summary>Largest valid field id (<c>INT32_MAX</c>), matching <c>SOFAB_ID_MAX</c>.</summary>
    internal const int ID_MAX = int.MaxValue;

    /// <summary>
    /// Largest array element count / fixlen byte length (<c>INT32_MAX</c>),
    /// matching <c>SOFAB_ARRAY_MAX</c> / <c>SOFAB_FIXLEN_MAX</c>.
    /// </summary>
    internal const ulong ARRAY_MAX = int.MaxValue;

    /// <summary>Number of value bits; bounds the maximum varint length (64-bit value type).</summary>
    internal const int VALUE_BITS = 64;

    /// <summary>
    /// ZigZag-encode a signed value to its unsigned varint representation.
    /// </summary>
    /// <param name="v">signed value</param>
    /// <returns>the unsigned (bit-pattern) representation</returns>
    internal static ulong ZigzagEncode(long v)
    {
        return (ulong)((v << 1) ^ (v >> 63));
    }

    /// <summary>
    /// ZigZag-decode an unsigned varint back to a signed value.
    /// </summary>
    /// <param name="u">unsigned (bit-pattern) representation</param>
    /// <returns>the signed value</returns>
    internal static long ZigzagDecode(ulong u)
    {
        return (long)(u >> 1) ^ -(long)(u & 1UL);
    }
}
