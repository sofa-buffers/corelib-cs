/*
 * SofaBuffers C# - the LimitExceeded decode error category (issue #28).
 *
 * A receiver-configured decode limit on an unbounded field (max_dyn_array_count /
 * max_dyn_string_len / max_dyn_blob_len) is enforced in generated code, not here;
 * this corelib only defines the distinct category so a violation is reported
 * uniformly and never conflated with wire malformation (InvalidMessage).
 *
 * SPDX-License-Identifier: MIT
 */

using Xunit;

namespace SofaBuffers.Tests;

public class LimitExceededErrorTests
{
    [Fact]
    public void LimitExceededIsDistinctFromInvalidMessage()
    {
        // The whole point of the category: a receiver cap violation (policy) must
        // be tellable apart from wire malformation. If these ever collapsed to the
        // same value the Crucible differential fuzzer would flag backends with
        // different configured caps as wire-conformance divergence.
        Assert.NotEqual(SofabError.InvalidMessage, SofabError.LimitExceeded);
    }

    [Fact]
    public void ExceptionCarriesLimitExceededCategory()
    {
        // Model how generated decode code reports a cap violation: throw with the
        // LimitExceeded category. A caller catches SofabException and branches on
        // .Error without string matching, seeing LimitExceeded, not InvalidMessage.
        SofabException? caught = null;
        try
        {
            throw new SofabException(SofabError.LimitExceeded, "max_dyn_array_count");
        }
        catch (SofabException ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.Equal(SofabError.LimitExceeded, caught!.Error);
        Assert.NotEqual(SofabError.InvalidMessage, caught.Error);
    }

    [Fact]
    public void LimitExceededMessageNamesTheCategoryAndDetail()
    {
        // Detail (which cap was hit) is preserved for diagnostics.
        var ex = new SofabException(SofabError.LimitExceeded, "max_dyn_string_len");
        Assert.Contains("LimitExceeded", ex.Message);
        Assert.Contains("max_dyn_string_len", ex.Message);
    }
}
