/*
 * SofaBuffers C# - library-level constants.
 *
 * SPDX-License-Identifier: MIT
 */

namespace SofaBuffers;

/// <summary>
/// Library-level constants for the SofaBuffers (<c>sofab</c>) core.
/// </summary>
/// <remarks>
/// The architecture spec fixes the namespace name as <c>sofab</c>; this type
/// carries that name so the API version and other shared constants are reachable
/// as <c>SofaBuffers.Sofab.*</c>.
/// </remarks>
public static class Sofab
{
    /// <summary>
    /// The SofaBuffers API version (currently <c>1</c>). Callers and the schema
    /// generator use this to verify compatibility at build or run time. Mirrors
    /// the C <c>SOFAB_API_VERSION</c> and the C++ <c>sofab::API_VERSION</c>.
    /// </summary>
    public const int ApiVersion = 1;
}
