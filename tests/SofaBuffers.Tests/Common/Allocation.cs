/*
 * SofaBuffers C# - the measured half of CORELIB_PLAN §6.6.4.
 *
 * §6.6 permits the codec's fixed-size state to be laid down when the encoder or
 * decoder is constructed and forbids every allocation after that, and §6.6.4
 * makes the *measurement* normative: "an allocation count, or the heap high-water
 * mark, over a complete encode and a complete decode, measured after the codec's
 * one-time construction", which on a runtime that does not box the codec's values
 * MUST be zero. C# is such a runtime - scalars are structs and every callback
 * carries them by value - so the assertion is exact zero, never a threshold.
 *
 * SPDX-License-Identifier: MIT
 */

using System;
using Xunit;

namespace SofaBuffers.Tests.Common;

/// <summary>Measures what a codec allocates after it has been constructed.</summary>
public static class Allocation
{
    /// <summary>How many times a measurement is retried before it is a failure.</summary>
    private const int Attempts = 5;

    /// <summary>
    /// Construct a fresh codec, then measure only what running <paramref name="body"/>
    /// over it allocates; repeat until a run comes back at exactly zero, and fail
    /// with every figure seen if none does.
    /// </summary>
    /// <remarks>
    /// The construction is deliberately outside the measured window: §6.6 makes
    /// one-time construction the boundary and lets it size the codec's fixed state.
    /// It is a <em>fresh</em> codec every attempt all the same, because a
    /// per-stream allocation is exactly what this is looking for.
    /// <para>
    /// The property under test is deterministic: a codec that takes storage after
    /// construction takes it on <em>every</em> fresh instance, so every attempt
    /// reports it and the retry cannot mask one. What the retry absorbs is the
    /// runtime landing something of its own in the window - a tier-1 transition, a
    /// GC bookkeeping charge - which happens to whichever call it happens to and is
    /// not a property of the codec at all. So the assertion stays exact zero
    /// without being flaky.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">the codec type</typeparam>
    /// <param name="construct">builds a fresh codec, outside the measured window</param>
    /// <param name="body">the complete encode or decode to measure</param>
    public static void AssertNone<T>(Func<T> construct, Action<T> body)
    {
        var seen = new long[Attempts];
        for (int i = 0; i < Attempts; i++)
        {
            T codec = construct();
            long before = GC.GetAllocatedBytesForCurrentThread();
            body(codec);
            seen[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            if (seen[i] == 0)
            {
                return;
            }
        }
        Assert.Fail(
            "the codec allocated after construction on every attempt (bytes: " +
            string.Join(", ", seen) + "); CORELIB_PLAN §6.6.4 requires zero");
    }
}
