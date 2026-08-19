/*
 * SofaBuffers C# - generated-code support layer: strict UTF-8 materialization of
 * a decoded string payload.
 *
 * SPDX-License-Identifier: MIT
 */

using System.Text;

namespace sofab;

/// <summary>
/// Turns a raw UTF-8 byte range into a C# <see cref="string"/>, rejecting a range
/// that is not well-formed — the <b>support layer</b> for generated code, not part
/// of the codec.
/// </summary>
/// <remarks>
/// <see cref="IStream"/> transcodes nothing: it hands a <c>string</c> payload to
/// <see cref="IVisitor.String"/> as the wire bytes it read, and a field nobody
/// reads is skipped without being looked at (CORELIB_PLAN §6.4). Materializing the
/// value — and therefore validating it — is the consumer's step, which for a
/// generated message class means every <c>string</c> field of every schema. That
/// step has the same shape wherever it appears, so it lives here instead of being
/// emitted, rationale and all, into every generated source tree (generator#345).
/// <para>
/// <b>Validate first, convert second — there is no other order.</b>
/// <see cref="Encoding.UTF8"/> substitutes <c>U+FFFD</c> for malformed input, so a
/// check made on the resulting string can never fail: it holds a repaired value
/// that no longer remembers what arrived. MESSAGE_SPEC §8 forbids that repair in
/// every mode ("no silent replacement, ever"), and §7 wants the message rejected.
/// The strict codec below therefore does both at once: it throws on the first byte
/// it cannot decode rather than producing a string.
/// </para>
/// <para>
/// <b>Strict means the Unicode scalar encoding and nothing else.</b> Rejected:
/// overlong forms (including the <c>C0 80</c> "modified UTF-8" NUL), the surrogate
/// code points <c>U+D800..U+DFFF</c> encoded as three bytes, anything above
/// <c>U+10FFFF</c>, a lead byte whose continuation bytes are missing or out of
/// range, and a bare continuation byte. C# <c>string</c> is a Unicode type and can
/// hold none of those, so this port is strict unconditionally — there is no
/// <c>SOFAB_STRICT_UTF8</c> switch to turn off (§6.4).
/// </para>
/// <para>
/// <b>Call it once, on the complete payload.</b> Validity is a property of the
/// whole <c>string</c> field, never of a chunk: a multi-byte sequence split across
/// a feed is a well-formed prefix, not a defect. <see cref="PayloadAcc.String"/> is
/// what arranges that, and calls this at the moment the last chunk lands.
/// </para>
/// </remarks>
public static class Utf8
{
    /// <summary>
    /// The strict UTF-8 codec of this library, shared by both directions.
    /// </summary>
    /// <remarks>
    /// <c>throwOnInvalidBytes: true</c> replaces the replacement-character fallback
    /// on <em>both</em> sides of the codec: decoding raises
    /// <see cref="DecoderFallbackException"/> for bytes that are not valid UTF-8,
    /// encoding raises <see cref="EncoderFallbackException"/> for a UTF-16 value
    /// that is not a valid Unicode string (an unpaired surrogate), which is what
    /// <see cref="OStream.WriteString"/> needs of it. Valid text goes to exactly
    /// the same bytes as the default UTF-8 encoder.
    /// </remarks>
    internal static readonly UTF8Encoding Strict =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Materialize <c>data[offset, offset + length)</c> as a <see cref="string"/>,
    /// rejecting a range that is not well-formed UTF-8.
    /// </summary>
    /// <remarks>
    /// The rejection is a <see cref="SofabException"/> carrying
    /// <see cref="SofabError.InvalidMessage"/> — the §5.2 <c>INVALID</c> outcome.
    /// Thrown from inside an <see cref="IVisitor"/> callback, which is where
    /// generated code calls this from, it propagates out of
    /// <see cref="IStream.Feed(byte[], int, int, IVisitor)"/> with the verdict
    /// latched, exactly like the decoder's own rejections: the stream stays
    /// <see cref="DecodeStatus.Invalid"/> regardless of what follows.
    /// <para>
    /// Only the named range is inspected. A chunk carrying the payload plus
    /// whatever came after it decodes to the payload alone, so a caller passing the
    /// declared <c>total</c> never has to trim its input first.
    /// </para>
    /// </remarks>
    /// <param name="data">buffer holding the payload</param>
    /// <param name="offset">first byte of the payload within <paramref name="data"/></param>
    /// <param name="length">payload length in bytes</param>
    /// <returns>the decoded string</returns>
    /// <exception cref="SofabException">
    /// (<see cref="SofabError.InvalidMessage"/>) when the range is not valid UTF-8.
    /// </exception>
    public static string Decode(byte[] data, int offset, int length)
    {
        try
        {
            return Strict.GetString(data, offset, length);
        }
        catch (DecoderFallbackException)
        {
            throw new SofabException(SofabError.InvalidMessage, "string: invalid UTF-8");
        }
    }
}
