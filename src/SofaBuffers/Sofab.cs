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

    /// <summary>
    /// The smallest output buffer this port accepts <b>for streaming</b> — i.e.
    /// for a buffer installed together with a <see cref="FlushSink"/>. This
    /// encoder splits every atomic unit across a flush, so the value is
    /// <c>1</c>: a message of any size streams through a one-byte buffer and
    /// produces bytes identical to the one-shot path. Mirrors the C
    /// <c>SOFAB_MIN_OUTPUT_BUFFER</c>.
    /// </summary>
    /// <remarks>
    /// CORELIB_PLAN §5.1. A buffer installed with a sink must satisfy
    /// <c>buffer.Length - offset &gt;= MinOutputBuffer</c>, at construction and
    /// at every mid-stream <see cref="OStream.BufferSet"/>; it is rejected
    /// there, with an <see cref="System.ArgumentOutOfRangeException"/>, never
    /// partway through a message. A buffer installed <i>without</i> a sink is
    /// subject to no minimum at all — no flush can occur, so a message that
    /// encodes to two bytes may be encoded into a two-byte buffer.
    /// </remarks>
    public const int MinOutputBuffer = 1;
}
