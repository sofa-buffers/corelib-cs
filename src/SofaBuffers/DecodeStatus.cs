/*
 * SofaBuffers C# - decode outcome (MESSAGE_SPEC §7).
 *
 * SPDX-License-Identifier: MIT
 */

namespace sofab;

/// <summary>
/// The terminal outcome of a decode (MESSAGE_SPEC §7), reported identically for
/// one-shot and streaming decode with <b>no</b> finish / finalize / end
/// promotion step.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IStream.Feed(byte[], IVisitor)"/> returns <see cref="Complete"/>
/// when the bytes consumed so far end exactly at a field boundary, or
/// <see cref="Incomplete"/> when they end <em>inside</em> a field — a partial
/// varint, a payload shorter than its declared length, an array that runs off the
/// end, or a nested sequence never closed. <see cref="Incomplete"/> is <b>not</b>
/// an error: more bytes could complete the message, and the caller owns
/// end-of-input, deciding whether a trailing <see cref="Incomplete"/> is a
/// truncation error for its protocol.
/// </para>
/// <para>
/// <see cref="Invalid"/> input — malformed regardless of what follows — is never
/// returned; it throws <see cref="SofabException"/> with
/// <see cref="SofabError.InvalidMessage"/> from <c>Feed</c>. The value is defined
/// here so the enum names all three §7 outcomes.
/// </para>
/// <para>
/// This mirrors the Go port's <c>ErrIncomplete</c> sentinel and the TypeScript
/// port's <c>DecodeStatus</c> — a distinct INCOMPLETE outcome that a caller tells
/// apart from a clean COMPLETE return and from the INVALID throw.
/// </para>
/// </remarks>
public enum DecodeStatus
{
    /// <summary>The bytes ended exactly at a field boundary — a valid message.</summary>
    Complete,

    /// <summary>
    /// The bytes ended inside a field (an unterminated varint, an unfinished
    /// fixlen / array payload, or a still-open nested sequence); more bytes could
    /// complete it. Not an error — the caller owns end-of-input.
    /// </summary>
    Incomplete,

    /// <summary>
    /// The bytes are malformed regardless of what follows. Never returned by
    /// <c>Feed</c>: it throws <see cref="SofabException"/>
    /// (<see cref="SofabError.InvalidMessage"/>) instead. Present for parity with
    /// the three MESSAGE_SPEC §7 outcomes.
    /// </summary>
    Invalid,
}
