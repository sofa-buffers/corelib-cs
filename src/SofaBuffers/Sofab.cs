/*
 * SofaBuffers C# - library-level constants.
 *
 * SPDX-License-Identifier: MIT
 */

namespace sofab;

/// <summary>
/// Library-level constants for the SofaBuffers (<c>sofab</c>) core.
/// </summary>
/// <remarks>
/// Public symbols live under the <c>sofab</c> namespace (per the architecture
/// spec); this type collects library-level constants such as the API version,
/// reachable as <c>sofab.Sofab.*</c>.
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
