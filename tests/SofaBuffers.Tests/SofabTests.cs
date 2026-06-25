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
        // ARCHITECTURE.md §6.2: API_VERSION is normatively 1.
        Assert.Equal(1, Sofab.ApiVersion);
    }
}
