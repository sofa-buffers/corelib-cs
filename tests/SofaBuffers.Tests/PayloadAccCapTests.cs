/*
 * SofaBuffers C# - the receiver cap PayloadAcc is HANDED (CORELIB_PLAN §6.2.1).
 *
 * §6.2.1 fixes two things separately. The *provenance* of a max_dyn_* number is
 * generated code's, always: "a codec MUST NOT hold a limit of its own, MUST NOT
 * supply a default for one it was not given, MUST NOT read an omitted argument as
 * unlimited, and MUST NOT clamp to one". The *site of the comparison* is not fixed
 * -- "a corelib MAY take a limit as an argument and perform the check itself, and a
 * port that does is conformant" -- and for a string or a blob this port now does,
 * because PayloadAcc.String/Blob is the call generated code already makes for every
 * one of them and the length is right there.
 *
 * So these tests are written over the two halves:
 *   - the check happens here, at the length header, BEFORE a byte is taken -- the
 *     enforcement point §6.2.1 requires, "before the allocation it is meant to
 *     prevent". The regression this pins is a destination sized from the announced
 *     length and only then rejected (the shape corelib-cpp's free readString had:
 *     fitDest() on `total` ahead of any cap);
 *   - the number is never owned. The parameter is required, a cap of 0 is a real
 *     cap rather than a spelling of "unlimited", and a cap that was never stated is
 *     a caller defect in §6.3's InvalidArgument category -- not LimitExceeded,
 *     which would promise a limit to raise that nobody configured.
 *
 * Array element counts and indices are deliberately NOT here: this corelib has no
 * per-element call to hang them on (Seq only grows an array generated code owns),
 * so those caps stay in generated code -- §6.2.1's "one implementation, wherever it
 * runs", one rule enforced in exactly one layer.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

using static SofaBuffers.Tests.Common.TestBytes;

namespace SofaBuffers.Tests;

public class PayloadAccCapTests
{
    private static byte[] Utf8Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // --- the cap is compared, and the verdict is a policy one -----------------

    [Fact]
    public void AStringLongerThanTheCapIsLimitExceeded()
    {
        var acc = new PayloadAcc();
        byte[] payload = Utf8Bytes("0123456789");

        var e = Assert.Throws<SofabException>(
            () => acc.String(payload.Length, 0, payload, 0, payload.Length, 9));

        // Policy, not malformation: the same bytes decode for a receiver whose cap
        // is 10, so §6.3 forbids reporting them as InvalidMessage.
        Assert.Equal(SofabError.LimitExceeded, e.Error);
        Assert.NotEqual(SofabError.InvalidMessage, e.Error);
        Assert.Contains("max_dyn_string_len", e.Message);
    }

    [Fact]
    public void ABlobLongerThanTheCapIsLimitExceeded()
    {
        var acc = new PayloadAcc();
        byte[] payload = Utf8Bytes("0123456789");

        var e = Assert.Throws<SofabException>(
            () => acc.Blob(payload.Length, 0, payload, 0, payload.Length, 9));

        Assert.Equal(SofabError.LimitExceeded, e.Error);
        Assert.Contains("max_dyn_blob_len", e.Message);
    }

    [Fact]
    public void TheCapIsAnInclusiveCeiling()
    {
        // The boundary, both kinds: total == cap passes, total == cap + 1 does not.
        // A cap is the largest length admitted, not the first refused.
        byte[] payload = Utf8Bytes("0123456789");
        int n = payload.Length;

        Assert.Equal("0123456789", new PayloadAcc().String(n, 0, payload, 0, n, n));
        Assert.Equal(payload, new PayloadAcc().Blob(n, 0, payload, 0, n, n));

        Assert.Throws<SofabException>(() => new PayloadAcc().String(n, 0, payload, 0, n, n - 1));
        Assert.Throws<SofabException>(() => new PayloadAcc().Blob(n, 0, payload, 0, n, n - 1));
    }

    [Fact]
    public void ACapOfZeroIsARealCapAndNotASpellingOfUnlimited()
    {
        // §6.2.1 has "no unset state and no unlimited mode", so no value of the
        // argument may be read as "do not check". Zero admits the empty payload and
        // refuses every other -- which is what a receiver that configured zero asked
        // for, and the opposite of what a sentinel reading would do.
        var acc = new PayloadAcc();
        Assert.Equal("", acc.String(0, 0, Array.Empty<byte>(), 0, 0, 0));

        byte[] one = Utf8Bytes("x");
        var e = Assert.Throws<SofabException>(() => acc.String(1, 0, one, 0, 1, 0));
        Assert.Equal(SofabError.LimitExceeded, e.Error);
    }

    // --- a cap that was never stated is a caller defect -----------------------

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void ACapThatWasNeverStatedIsAnArgumentError(long cap)
    {
        // -1 is the shape a port reaches for when it wants "unlimited". §6.2.1
        // forbids honouring it, and §6.3 forbids calling it LimitExceeded: that
        // would tell the operator to raise a limit that was never configured. It is
        // a defect in the CALL, so it is InvalidArgument (this port's Argument).
        var acc = new PayloadAcc();
        byte[] payload = Utf8Bytes("hi");

        var s = Assert.Throws<SofabException>(() => acc.String(2, 0, payload, 0, 2, cap));
        Assert.Equal(SofabError.Argument, s.Error);
        Assert.NotEqual(SofabError.LimitExceeded, s.Error);

        var b = Assert.Throws<SofabException>(() => acc.Blob(2, 0, payload, 0, 2, cap));
        Assert.Equal(SofabError.Argument, b.Error);
    }

    [Fact]
    public void AnUnstatedCapIsRefusedEvenForAnEmptyPayload()
    {
        // Nothing about the message excuses the missing number: the call is wrong
        // whatever it carries.
        var acc = new PayloadAcc();
        var e = Assert.Throws<SofabException>(
            () => acc.String(0, 0, Array.Empty<byte>(), 0, 0, -1));
        Assert.Equal(SofabError.Argument, e.Error);
    }

    // --- the enforcement point: at the header, before anything is taken -------

    [Fact]
    public void AnOverCapPayloadIsRefusedBeforeItIsMaterialized()
    {
        // The regression that motivates the whole change. A destination sized from
        // the announced length and only then rejected has already let the sender
        // dictate the receiver's allocation -- so the refusal must cost nothing
        // proportional to `total`, here 256 KiB delivered whole against a cap of 64.
        var acc = new PayloadAcc();
        byte[] big = new byte[256 * 1024];

        // Warm both throw paths up, so what is measured is this call and not the
        // JIT's one-off work on the exception machinery.
        var warm = new PayloadAcc();
        Assert.Throws<SofabException>(() => warm.Blob(big.Length, 0, big, 0, big.Length, 64));
        Assert.Throws<SofabException>(() => warm.String(big.Length, 0, big, 0, big.Length, 64));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<SofabException>(() => acc.Blob(big.Length, 0, big, 0, big.Length, 64));
        Assert.Throws<SofabException>(() => acc.String(big.Length, 0, big, 0, big.Length, 64));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Room for the two exception objects and their messages, nowhere near the
        // 512 KiB two materialized payloads would cost.
        Assert.True(allocated < 8192, "allocated " + allocated + " bytes refusing 2x256 KiB");
    }

    [Fact]
    public void TheFirstChunkOfAnOverCapPayloadIsAlreadyRefused()
    {
        // A split payload must not be accumulated up to the cap and rejected on the
        // chunk that crosses it: `total` announces the whole length on chunk 0, and
        // that is where §6.2.1 puts the check.
        var acc = new PayloadAcc();
        byte[] payload = Utf8Bytes("0123456789");

        var e = Assert.Throws<SofabException>(() => acc.String(10, 0, payload, 0, 3, 4));
        Assert.Equal(SofabError.LimitExceeded, e.Error);
    }

    [Fact]
    public void ARefusedPayloadLeavesNothingBehindForTheNextOne()
    {
        // The refusal took no bytes, so the accumulator is not carrying a prefix of
        // the rejected payload into the next field a caller decides to read.
        var acc = new PayloadAcc();
        byte[] rejected = Utf8Bytes("0123456789");
        Assert.Throws<SofabException>(() => acc.String(10, 0, rejected, 0, 4, 4));

        byte[] next = Utf8Bytes("kept");
        Assert.Null(acc.String(4, 0, next, 0, 2, 64));
        Assert.Equal("kept", acc.String(4, 2, next, 2, 2, 64));
    }

    // --- through the decoder --------------------------------------------------

    /// <summary>
    /// What generated code looks like for a schema-unbounded string field: hold one
    /// accumulator, pass the callback's arguments through together with the cap the
    /// generator baked in.
    /// </summary>
    private sealed class CappedStringVisitor : IVisitor
    {
        private readonly PayloadAcc _acc = new();
        private readonly long _cap;

        public string? Value;

        public CappedStringVisitor(long cap) => _cap = cap;

        public void String(int id, int total, int offset, byte[] data, int co, int cl)
        {
            string? s = _acc.String(total, offset, data, co, cl, _cap);
            if (s is not null)
            {
                Value = s;
            }
        }
    }

    [Fact]
    public void ACapViolationIsTerminalButNeverInvalid()
    {
        // End to end, and the distinction §6.3 insists on: the decoder latches the
        // rejection like its own verdicts -- every later Feed re-raises it -- but the
        // bytes are well-formed, so the refusal travels as LimitExceeded on the
        // error channel and is never reported as InvalidMessage / the Invalid
        // outcome. Feed never returns at all here, so there is no status to fold
        // the policy rejection into.
        byte[] wire = Encode(2048, os => os.WriteString(1, "0123456789"));

        var iss = new IStream();
        var visitor = new CappedStringVisitor(4);

        var e = Assert.Throws<SofabException>(() => iss.Feed(wire, visitor));
        Assert.Equal(SofabError.LimitExceeded, e.Error);
        Assert.NotEqual(SofabError.InvalidMessage, e.Error);
        Assert.Null(visitor.Value);

        var again = Assert.Throws<SofabException>(() => iss.Feed(wire, visitor));
        Assert.Equal(SofabError.LimitExceeded, again.Error);
        Assert.NotEqual(SofabError.InvalidMessage, again.Error);
    }

    [Fact]
    public void TheSameBytesDecodeUnderALooserCap()
    {
        // Two receivers configured differently reaching different outcomes on the
        // same message is not an interop failure (§6.2.1) -- it is the definition of
        // the category.
        byte[] wire = Encode(2048, os => os.WriteString(1, "0123456789"));

        var visitor = new CappedStringVisitor(10);
        Assert.Equal(DecodeStatus.Complete, new IStream().Feed(wire, visitor));
        Assert.Equal("0123456789", visitor.Value);
    }

    // --- the same comparison, offered at the LENGTH WORD ----------------------

    /// <remarks>
    /// The check reachable on its own, for the caller to make from
    /// <c>IVisitor.FixlenBegin</c> — the point §6.2.1 actually names: "at the
    /// count/length header — before the allocation it is meant to prevent".
    ///
    /// <para><see cref="PayloadAcc.String"/> cannot be that point by itself. It
    /// fires only once a payload byte exists, so a message whose length word
    /// declares 100 bytes and then <em>ends</em> reaches no chunk, no call and no
    /// verdict — and the decode answers <c>Incomplete</c> for bytes already
    /// refused, which §6.3 makes the wrong category (the refusal is terminal) and
    /// §5.2.4 makes an invitation to feed a stream that will never be accepted.
    /// Three bytes claiming a hundred is the shape that matters.</para>
    /// </remarks>
    [Fact]
    public void AnOverCapLengthIsRefusedWithNoPayloadAtAll()
    {
        SofabException s = Assert.Throws<SofabException>(() => PayloadAcc.CheckStringLength(100, 8));
        Assert.Equal(SofabError.LimitExceeded, s.Error);

        SofabException b = Assert.Throws<SofabException>(() => PayloadAcc.CheckBlobLength(1 << 20, 8));
        Assert.Equal(SofabError.LimitExceeded, b.Error);
    }

    /// <summary>A length at or below the cap passes the header check silently.</summary>
    [Fact]
    public void AnInCapLengthPassesTheHeaderCheck()
    {
        PayloadAcc.CheckStringLength(8, 8);
        PayloadAcc.CheckStringLength(0, 8);
        PayloadAcc.CheckBlobLength(8, 8);
    }

    /// <summary>
    /// An unstated cap is a caller defect here too — <c>Argument</c>, never
    /// <c>LimitExceeded</c>, which would promise a limit nobody configured, and
    /// never silently uncapped (§6.2.1, §6.3).
    /// </summary>
    [Fact]
    public void TheHeaderCheckRefusesAnUnstatedCap()
    {
        Assert.Equal(SofabError.Argument,
            Assert.Throws<SofabException>(() => PayloadAcc.CheckStringLength(1, -1)).Error);
        Assert.Equal(SofabError.Argument,
            Assert.Throws<SofabException>(() => PayloadAcc.CheckBlobLength(1, -1)).Error);
    }

    /// <summary>
    /// One implementation, two application points (§6.2.1, "one implementation,
    /// wherever it runs"): the payload call answers identically to the header check
    /// for every length, so an accumulator driven by hand — without the header call
    /// — is still bounded, and a caller making both cannot get two verdicts out of
    /// one length.
    /// </summary>
    [Fact]
    public void TheHeaderCheckAndThePayloadCallAgreeOnEveryLength()
    {
        foreach (int total in new[] { 0, 1, 7, 8, 9, 100, 1 << 20 })
        {
            SofabError? header = null;
            try
            {
                PayloadAcc.CheckStringLength(total, 8);
            }
            catch (SofabException e)
            {
                header = e.Error;
            }

            SofabError? chunk = null;
            try
            {
                // One byte of a `total`-byte payload: enough to reach the guard,
                // never enough to complete anything.
                byte[] data = new byte[Math.Max(total, 1)];
                new PayloadAcc().String(total, 0, data, 0, Math.Min(total, 1), 8);
            }
            catch (SofabException e)
            {
                chunk = e.Error;
            }

            Assert.Equal(header, chunk);
        }
    }

    // --- the corelib holds no limit (the structural half) ---------------------

    [Fact]
    public void TheCapIsARequiredArgumentWithNoDefault()
    {
        // "An argument a caller may omit is an API affordance, never a licence to
        // decode uncapped. The strictest form, and the recommended one, makes the
        // argument required" (§6.2.1). There is also no legacy overload left to fall
        // back to: a caller who states no cap does not compile.
        foreach (string name in new[] { "String", "Blob" })
        {
            MethodInfo[] overloads = typeof(PayloadAcc)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == name)
                .ToArray();

            MethodInfo m = Assert.Single(overloads);
            ParameterInfo cap = m.GetParameters()[^1];
            Assert.Equal("cap", cap.Name);
            Assert.Equal(typeof(long), cap.ParameterType);
            Assert.False(cap.HasDefaultValue, name + " has a default cap");
            Assert.False(cap.IsOptional, name + " has an optional cap");
        }
    }

    [Fact]
    public void ThePayloadAccumulatorStoresNoLimit()
    {
        // The number is used for one comparison and not retained: the accumulator's
        // whole state is the reassembly buffer and its fill mark, and there is no
        // constant standing by to be used as a fallback.
        FieldInfo[] fields = typeof(PayloadAcc).GetFields(
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static);

        Assert.Equal(
            new[] { "_buffer", "_length" },
            fields.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Empty(fields.Where(f => f.IsStatic));
    }
}
