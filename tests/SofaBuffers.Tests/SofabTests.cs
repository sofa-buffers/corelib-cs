/*
 * SofaBuffers C# - library-level constant checks.
 *
 * SPDX-License-Identifier: MIT
 */

using Xunit;

namespace SofaBuffers.Tests;

public class SofabTests
{
    [Fact]
    public void ApiVersionIsOne()
    {
        // CORELIB_PLAN §6.2 pins `API_VERSION` to 1 in the constants table; the
        // §13 checklist restates it as "API version constant/getter returns 1".
        Assert.Equal(1, Sofab.ApiVersion);
    }
}
