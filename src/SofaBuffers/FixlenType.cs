/*
 * SofaBuffers C# - fixed-length field sub-types.
 *
 * SPDX-License-Identifier: MIT
 */

namespace sofab;

/// <summary>
/// Sub-type of a fixed-length field — the 3-bit tag encoded in the low bits of a
/// fixlen length header (see the SofaBuffers documentation, "Fixlen Length and
/// Type").
/// </summary>
public enum FixlenType
{
    /// <summary>32-bit IEEE-754 float, little-endian on the wire.</summary>
    Fp32 = 0x0,

    /// <summary>64-bit IEEE-754 double, little-endian on the wire.</summary>
    Fp64 = 0x1,

    /// <summary>UTF-8 / raw text, no NUL terminator on the wire.</summary>
    String = 0x2,

    /// <summary>Arbitrary raw bytes.</summary>
    Blob = 0x3,
}

/// <summary>Helpers for the <see cref="FixlenType"/> wire encoding.</summary>
internal static class FixlenTypeExtensions
{
    /// <summary>The 3-bit wire tag (0..3) for this sub-type.</summary>
    public static int Raw(this FixlenType type) => (int)type;

    /// <summary>
    /// Decode a 3-bit fixlen tag from the wire.
    /// </summary>
    /// <param name="raw">the tag value (low 3 bits of the fixlen header)</param>
    /// <returns>the matching <see cref="FixlenType"/></returns>
    /// <exception cref="SofabException">
    /// with <see cref="SofabError.InvalidMessage"/> for a reserved or unsupported tag
    /// </exception>
    public static FixlenType FromRaw(int raw)
    {
        switch (raw)
        {
            case 0x0: return FixlenType.Fp32;
            case 0x1: return FixlenType.Fp64;
            case 0x2: return FixlenType.String;
            case 0x3: return FixlenType.Blob;
            default: throw new SofabException(SofabError.InvalidMessage, "fixlen type " + raw);
        }
    }
}
