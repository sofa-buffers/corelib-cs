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
