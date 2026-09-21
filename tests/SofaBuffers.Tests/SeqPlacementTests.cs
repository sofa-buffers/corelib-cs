/*
 * SofaBuffers C# - the element placement generated wrapper arrays are filled
 * through: Seq.PlaceElem, Seq.ReserveElem, Seq.ReserveRow and Seq.CheckIndex
 * (MESSAGE_SPEC §5.1, §7.1, §7.4; CORELIB_PLAN §6.2.1, §7.2 item 8).
 *
 * These are called here DIRECTLY, with the test's own List<T>, not only through a
 * decode. None of the rules they carry is visible in the bytes -- two ports that
 * fill a gap differently, or grow before bounding the index, emit identical
 * messages -- so the only place they can be pinned is at the helper itself:
 *
 *   - an id is a position: a gap left by omitted interior elements is filled with
 *     the element default, a repeated id replaces (a framed element merges);
 *   - the index is bounded BEFORE the list grows, so a refused id leaves the list
 *     exactly as it was and a lower id delivered afterwards still lands;
 *   - a schema count is a capacity: id == count - 1 lands, id == count is
 *     InvalidMessage (§7.1);
 *   - where the schema declares no count the receiver cap bounds the index and a
 *     breach is LimitExceeded, never InvalidMessage; exactly one of the two bounds
 *     applies (§6.2.1), and a cap that was never stated is Argument.
 *
 * The last group pins the one bound these helpers deliberately do NOT carry: a
 * string/blob element's length, judged at the length word by PayloadAcc, in the
 * order a generated FixlenBegin arm runs the two checks.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SofaBuffers.Tests;

public class SeqPlacementTests
{
    /// <summary>The schema declares no count: the receiver cap governs.</summary>
    private const long NoCount = -1;

    /// <summary>A receiver cap large enough never to fire in a case about the schema count.</summary>
    private const long AnyRcap = 1000;

    /// <summary>A mutable framed element, standing in for a generated element class.</summary>
    private sealed class Elem
    {
        internal int Value;
    }

    private static SofabException Refused(Action a) => Assert.Throws<SofabException>(a);

    // --- PlaceElem: a leaf element at its id ---------------------------------

    [Fact]
    public void PlaceElemPutsTheValueAtItsId()
    {
        var l = new List<string>();
        Seq.PlaceElem(l, 0, "", "a", 5, AnyRcap);
        Seq.PlaceElem(l, 1, "", "b", 5, AnyRcap);
        Assert.Equal(new[] { "a", "b" }, l);
    }

    [Fact]
    public void PlaceElemFillsAGapWithTheElementDefault()
    {
        // Interior elements equal to the default are omitted on the wire (§5.1):
        // ids 0 and 2 missing must decode as "" at those positions, not shift
        // the delivered element down to index 0.
        var l = new List<string>();
        Seq.PlaceElem(l, 3, "", "d", 5, AnyRcap);
        Assert.Equal(new[] { "", "", "", "d" }, l);

        Seq.PlaceElem(l, 1, "", "b", 5, AnyRcap);
        Assert.Equal(new[] { "", "b", "", "d" }, l);
    }

    [Fact]
    public void PlaceElemGapIsTheSharedDefaultNotACopy()
    {
        // A blob gap costs no allocation: every gap slot is the one empty array.
        var l = new List<byte[]>();
        byte[] empty = Array.Empty<byte>();
        Seq.PlaceElem(l, 2, empty, new byte[] { 7 }, 3, AnyRcap);
        Assert.Same(empty, l[0]);
        Assert.Same(empty, l[1]);
        Assert.Equal(new byte[] { 7 }, l[2]);
    }

    [Fact]
    public void PlaceElemRepeatedIdReplaces()
    {
        // §7.4: last occurrence wins, at its own index -- never an append.
        var l = new List<string>();
        Seq.PlaceElem(l, 1, "", "first", 5, AnyRcap);
        Seq.PlaceElem(l, 1, "", "second", 5, AnyRcap);
        Assert.Equal(new[] { "", "second" }, l);
    }

    // --- the schema count: a capacity, verdict InvalidMessage ----------------

    [Fact]
    public void AnIdAtTheLastSlotOfTheCountLands()
    {
        var l = new List<string>();
        Seq.PlaceElem(l, 4, "", "e", 5, AnyRcap);
        Assert.Equal(5, l.Count);
        Assert.Equal("e", l[4]);
    }

    [Fact]
    public void AnIdAtTheCountIsInvalidAndLeavesTheListUntouched()
    {
        var l = new List<string>();
        Seq.PlaceElem(l, 0, "", "a", 5, AnyRcap);

        var e = Refused(() => Seq.PlaceElem(l, 5, "", "x", 5, AnyRcap));
        Assert.Equal(SofabError.InvalidMessage, e.Error);

        // Bounded BEFORE the list grows (§7.2 item 8): no slot toward the refused id.
        Assert.Equal(new[] { "a" }, l);

        // ...so a lower id delivered afterwards still lands at its own index.
        Seq.PlaceElem(l, 2, "", "c", 5, AnyRcap);
        Assert.Equal(new[] { "a", "", "c" }, l);
    }

    [Fact]
    public void ACountOfZeroAdmitsNoElement()
    {
        // 0 is a real capacity, not a spelling of "unbounded".
        var l = new List<string>();
        Assert.Equal(SofabError.InvalidMessage,
            Refused(() => Seq.PlaceElem(l, 0, "", "x", 0, AnyRcap)).Error);
        Assert.Empty(l);
    }

    // --- the receiver cap: only where the schema declares no count -----------

    [Fact]
    public void PastTheReceiverCapOnAnUncountedArrayIsLimitExceeded()
    {
        var l = new List<string>();
        Seq.PlaceElem(l, 2, "", "c", NoCount, 3);

        var e = Refused(() => Seq.PlaceElem(l, 3, "", "x", NoCount, 3));
        // Policy, not malformation: the same bytes decode under a looser cap (§6.3).
        Assert.Equal(SofabError.LimitExceeded, e.Error);
        Assert.NotEqual(SofabError.InvalidMessage, e.Error);
        Assert.Equal(new[] { "", "", "c" }, l);
    }

    [Fact]
    public void AnUnstatedReceiverCapIsAnArgumentError()
    {
        // A negative rcap on an uncounted array means the caller stated no cap.
        // That is the caller's defect, not a receiver policy: LimitExceeded would
        // promise a configured limit that does not exist.
        var l = new List<string>();
        var e = Refused(() => Seq.PlaceElem(l, 0, "", "x", NoCount, -1));
        Assert.Equal(SofabError.Argument, e.Error);
        Assert.Empty(l);
    }

    [Fact]
    public void TheReceiverCapNeverTouchesASchemaCountedArray()
    {
        // §6.2.1: exactly one bound applies. A declared count of 10 admits id 5
        // even under a receiver cap of 2 -- and even under no stated cap at all.
        var l = new List<string>();
        Seq.PlaceElem(l, 5, "", "f", 10, 2);
        Seq.PlaceElem(l, 6, "", "g", 10, -1);
        Assert.Equal(7, l.Count);

        // And past the count the verdict is the schema's, not the cap's.
        Assert.Equal(SofabError.InvalidMessage,
            Refused(() => Seq.PlaceElem(l, 10, "", "x", 10, 2)).Error);
    }

    [Fact]
    public void AHugeIdAllocatesNothing()
    {
        // One over-index element is by itself the allocation an untrusted index
        // buys; the bound must refuse it before the list is touched.
        var l = new List<string>();
        Assert.Equal(SofabError.LimitExceeded,
            Refused(() => Seq.PlaceElem(l, int.MaxValue, "", "x", NoCount, 16)).Error);
        Assert.Equal(SofabError.InvalidMessage,
            Refused(() => Seq.PlaceElem(l, int.MaxValue - 1, "", "x", 16, AnyRcap)).Error);
        Assert.Empty(l);
        Assert.Equal(0, l.Capacity);
    }

    // --- ReserveElem: a framed element's slot --------------------------------

    [Fact]
    public void ReserveElemMakesOneDistinctElementPerNewSlot()
    {
        var l = new List<Elem>();
        int made = 0;
        Func<Elem> make = () => { made++; return new Elem(); };

        Seq.ReserveElem(l, 2, make, 5, AnyRcap);
        Assert.Equal(3, made);
        Assert.Equal(3, l.Count);
        // A shared instance would alias every element of the array onto one object.
        Assert.Equal(3, l.Distinct().Count());
    }

    [Fact]
    public void ReserveElemLeavesAnExistingSlotAlone()
    {
        // A re-opened element id merges into what its earlier fields built (§7.4):
        // the slot keeps its object and its state.
        var l = new List<Elem>();
        Seq.ReserveElem(l, 1, static () => new Elem(), 5, AnyRcap);
        Elem first = l[1];
        first.Value = 42;

        int made = 0;
        Seq.ReserveElem(l, 1, () => { made++; return new Elem(); }, 5, AnyRcap);
        Assert.Equal(0, made);
        Assert.Same(first, l[1]);
        Assert.Equal(42, l[1].Value);

        // A higher id grows by exactly the slots it creates.
        Seq.ReserveElem(l, 3, () => { made++; return new Elem(); }, 5, AnyRcap);
        Assert.Equal(2, made);
        Assert.Same(first, l[1]);
    }

    [Fact]
    public void ReserveElemBoundsTheIndexBeforeCallingTheFactory()
    {
        var l = new List<Elem>();
        int made = 0;
        Func<Elem> make = () => { made++; return new Elem(); };

        Assert.Equal(SofabError.InvalidMessage,
            Refused(() => Seq.ReserveElem(l, 3, make, 3, AnyRcap)).Error);
        Assert.Equal(SofabError.LimitExceeded,
            Refused(() => Seq.ReserveElem(l, 4, make, NoCount, 4)).Error);
        Assert.Equal(SofabError.Argument,
            Refused(() => Seq.ReserveElem(l, 0, make, NoCount, -1)).Error);
        Assert.Equal(0, made);
        Assert.Empty(l);

        Seq.ReserveElem(l, 3, make, NoCount, 4);
        Assert.Equal(4, l.Count);
    }

    // --- ReserveRow: a matrix row --------------------------------------------

    [Fact]
    public void ReserveRowFillsGapsWithDistinctEmptyRows()
    {
        var rows = new List<List<uint>>();
        Seq.ReserveRow(rows, 2, 4, AnyRcap);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Empty(r));
        // A row is mutable: a shared empty row would make a write to one row
        // appear in every gap.
        Assert.NotSame(rows[0], rows[1]);
        Assert.NotSame(rows[1], rows[2]);
    }

    [Fact]
    public void ReserveRowOnAReopenedRowEmptiesItInPlace()
    {
        var rows = new List<List<string>>();
        Seq.ReserveRow(rows, 0, 4, AnyRcap);
        rows[0].Add("x");
        List<string> row = rows[0];

        Seq.ReserveRow(rows, 0, 4, AnyRcap);
        // §7.4: the value is replaced -- but the list object is reused.
        Assert.Single(rows);
        Assert.Same(row, rows[0]);
        Assert.Empty(rows[0]);
    }

    [Fact]
    public void ReserveRowBoundsTheRowIndexBeforeGrowing()
    {
        var rows = new List<List<float>>();
        Seq.ReserveRow(rows, 0, 2, AnyRcap);
        rows[0].Add(1.5f);

        Assert.Equal(SofabError.InvalidMessage,
            Refused(() => Seq.ReserveRow(rows, 2, 2, AnyRcap)).Error);
        Assert.Equal(SofabError.LimitExceeded,
            Refused(() => Seq.ReserveRow(rows, 2, NoCount, 2)).Error);
        Assert.Equal(SofabError.Argument,
            Refused(() => Seq.ReserveRow(rows, 0, NoCount, -1)).Error);

        // Untouched, including the existing row's content.
        Assert.Single(rows);
        Assert.Equal(new[] { 1.5f }, rows[0]);

        Seq.ReserveRow(rows, 1, 2, AnyRcap);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { 1.5f }, rows[0]);
    }

    // --- CheckIndex: the bound on its own ------------------------------------

    [Theory]
    // cap >= 0: the schema count governs, rcap is never read.
    [InlineData(0, 1L, -1L, null)]
    [InlineData(0, 1L, 0L, null)]
    [InlineData(1, 1L, 100L, SofabError.InvalidMessage)]
    [InlineData(4, 5L, 0L, null)]
    [InlineData(5, 5L, 100L, SofabError.InvalidMessage)]
    [InlineData(0, 0L, 100L, SofabError.InvalidMessage)]
    // cap < 0: the receiver cap governs.
    [InlineData(0, -1L, 1L, null)]
    [InlineData(1, -1L, 1L, SofabError.LimitExceeded)]
    [InlineData(0, -1L, 0L, SofabError.LimitExceeded)]
    [InlineData(65535, -1L, 65536L, null)]
    [InlineData(65536, -1L, 65536L, SofabError.LimitExceeded)]
    [InlineData(0, -1L, -1L, SofabError.Argument)]
    [InlineData(int.MaxValue, -1L, long.MaxValue, null)]
    // A negative id is never admitted, whichever bound applies.
    [InlineData(-1, 5L, 100L, SofabError.InvalidMessage)]
    [InlineData(-1, -1L, 100L, SofabError.LimitExceeded)]
    public void CheckIndexBoundaries(int id, long cap, long rcap, SofabError? want)
    {
        if (want is null)
        {
            Seq.CheckIndex(id, cap, rcap);
            return;
        }
        Assert.Equal(want.Value, Refused(() => Seq.CheckIndex(id, cap, rcap)).Error);
    }

    [Fact]
    public void TheRefusalNamesTheIndexAndTheBoundThatFired()
    {
        Assert.Contains("declared count 3",
            Refused(() => Seq.CheckIndex(7, 3, 100)).Message);
        Assert.Contains("configured limit 4",
            Refused(() => Seq.CheckIndex(7, NoCount, 4)).Message);
        Assert.Contains("max_dyn_array_count not stated",
            Refused(() => Seq.CheckIndex(7, NoCount, -1)).Message);
    }

    [Fact]
    public void EveryBoundIsARequiredArgument()
    {
        // §6.2.1: no default for a limit the caller did not give, no omitted
        // argument read as unlimited. Every placement takes both numbers, always.
        string[] names = { "PlaceElem", "ReserveElem", "ReserveRow", "CheckIndex" };
        foreach (string name in names)
        {
            MethodInfo m = typeof(Seq).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
            Assert.NotNull(m);
            ParameterInfo[] ps = m.GetParameters();
            Assert.Equal(new[] { "cap", "rcap" }, ps.Skip(ps.Length - 2).Select(p => p.Name));
            Assert.All(ps, p => Assert.False(p.HasDefaultValue, $"{name}({p.Name}) has a default"));
            Assert.All(ps.Skip(ps.Length - 2), p => Assert.Equal(typeof(long), p.ParameterType));
        }
        // And the class holds no limit of its own: its only constant is the
        // growth reservation, which bounds nothing.
        FieldInfo[] fields = typeof(Seq).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Equal(new[] { "ArrayInitCap" }, fields.Select(f => f.Name));
    }

    // --- the element's own length: the bound these helpers do NOT carry ------

    /// <summary>
    /// A generated <c>FixlenBegin</c> arm for a string/blob element, in the order
    /// the generator emits it: the index at the length word, then the length —
    /// both before a byte of payload is taken — and placement only once the
    /// payload is complete.
    /// </summary>
    private static void LengthWord(int id, int total, long cap, long rcap, long rmaxlen)
    {
        Seq.CheckIndex(id, cap, rcap);
        PayloadAcc.CheckBlobLength(total, rmaxlen);
    }

    [Fact]
    public void AnElementPayloadOverItsLengthCapIsRefusedAtTheLengthWordAndNotPlaced()
    {
        var l = new List<byte[]>();
        Seq.PlaceElem(l, 0, Array.Empty<byte>(), new byte[] { 1 }, NoCount, 8);

        // Index fine, payload over the receiver's max_dyn_blob_len: the length
        // verdict, LimitExceeded, and nothing lands -- placement never ran.
        var e = Refused(() => LengthWord(2, 9, NoCount, 8, 8));
        Assert.Equal(SofabError.LimitExceeded, e.Error);
        Assert.Single(l);

        // The index is judged first: an over-index element with an over-long
        // payload reports the index verdict of its array.
        Assert.Equal(SofabError.InvalidMessage,
            Refused(() => LengthWord(3, 9, 3, AnyRcap, 8)).Error);

        // A payload exactly at the cap is admitted and then placed at its id.
        LengthWord(2, 8, NoCount, 8, 8);
        Seq.PlaceElem(l, 2, Array.Empty<byte>(), new byte[8], NoCount, 8);
        Assert.Equal(3, l.Count);
        Assert.Empty(l[1]);
    }

    [Fact]
    public void AStringElementOverItsLengthCapIsRefusedBeforeAByteIsTaken()
    {
        var l = new List<string>();
        var acc = new PayloadAcc();
        byte[] first = { (byte)'a', (byte)'b' };

        // The first piece of a 10-byte string under a cap of 9: refused on that
        // piece, before the rest arrives, so the verdict does not depend on the
        // payload arriving whole.
        Seq.CheckIndex(0, NoCount, 4);
        var e = Refused(() => acc.String(10, 0, first, 0, first.Length, 9));
        Assert.Equal(SofabError.LimitExceeded, e.Error);
        Assert.Empty(l);
    }
}
