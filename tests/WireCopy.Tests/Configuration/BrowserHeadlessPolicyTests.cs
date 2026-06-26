// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests;

/// <summary>
/// NEVER-HEADLESS POLICY (workspace-8ne3): the browser must never run headless. These tests pin the
/// configuration-level guarantee — EffectiveHeadless is false for EVERY visibility setting, including an
/// explicit Visibility=Headless and the legacy Headless=true field, both of which are ignored.
/// </summary>
[Trait("Category", "Unit")]
public class BrowserHeadlessPolicyTests
{
    [Theory]
    [InlineData(BrowserVisibility.Auto)]
    [InlineData(BrowserVisibility.Visible)]
    [InlineData(BrowserVisibility.Headless)]
    public void EffectiveHeadless_IsAlwaysFalse_RegardlessOfVisibility(BrowserVisibility visibility)
    {
        var config = new BrowserConfiguration { Visibility = visibility };

        config.EffectiveHeadless.Should().BeFalse(
            "headless is disabled by hard project policy — the browser is always headful");
    }

    [Fact]
    public void EffectiveHeadless_IsFalse_EvenWhenLegacyHeadlessFieldIsTrue()
    {
        var config = new BrowserConfiguration { Headless = true, Visibility = BrowserVisibility.Headless };

        config.EffectiveHeadless.Should().BeFalse("the legacy Headless field and Visibility=Headless are ignored");
    }

    [Theory]
    [InlineData(BrowserVisibility.Auto)]
    [InlineData(BrowserVisibility.Visible)]
    [InlineData(BrowserVisibility.Headless)]
    public void DescribeVisibilityResolution_AlwaysReportsVisible_NeverHeadless(BrowserVisibility visibility)
    {
        var description = new BrowserConfiguration { Visibility = visibility }.DescribeVisibilityResolution();

        description.Should().Contain("VISIBLE");
        description.Should().NotContain("Browser mode: HEADLESS");
    }
}
