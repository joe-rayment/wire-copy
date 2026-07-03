// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests;

/// <summary>
/// NEVER-HEADLESS LAW (workspace-8ne3; plumbing deleted in workspace-9k27.10): the browser must never
/// run headless. Since workspace-9k27.10 the law is STRUCTURAL — there is no headless knob anywhere,
/// so a headless launch cannot even be requested. These tests pin that unexpressibility: the moment
/// anyone re-introduces a resolved-headless flag, a request-level Headless option, or a headless
/// parameter on the page-access APIs, they fail. The single Playwright launch chokepoint is
/// <c>BrowserSession.LaunchBrowserAsync</c>, which hardcodes <c>Headless = false</c> (its runtime
/// proof is the headed dock integration suites, which launch a REAL headful Chromium under Xvfb).
/// </summary>
[Trait("Category", "Unit")]
public class BrowserHeadlessPolicyTests
{
    [Fact]
    public void BrowserConfiguration_HasNoEffectiveHeadlessMember()
    {
        typeof(BrowserConfiguration).GetMember("EffectiveHeadless").Should().BeEmpty(
            "the resolved-headless decision was deleted (workspace-9k27.10) — headless is always false "
            + "and must stay unexpressible; do not re-add a resolved flag");
    }

    [Fact]
    public void PageLoadRequest_HasNoHeadlessOption()
    {
        typeof(PageLoadRequest).GetProperty("Headless").Should().BeNull(
            "page-load requests must not be able to ask for a headless browser");
    }

    [Fact]
    public void GetOrCreatePageAsync_TakesNoHeadlessParameter()
    {
        var method = typeof(IBrowserSession).GetMethod(nameof(IBrowserSession.GetOrCreatePageAsync));

        method.Should().NotBeNull();
        method!.GetParameters().Should().BeEmpty(
            "page creation must not be able to request a headless browser — the launch site is "
            + "hardcoded headful");
    }

    [Fact]
    public void PageAccessQueue_AcquireAsync_TakesNoHeadlessParameter()
    {
        var method = typeof(IPageAccessQueue).GetMethod(nameof(IPageAccessQueue.AcquireAsync));

        method.Should().NotBeNull();
        method!.GetParameters().Select(p => p.ParameterType).Should().NotContain(
            typeof(bool),
            "the page-access lease must not thread a headless flag to the session");
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

    [Fact]
    public void DescribeVisibilityResolution_FlagsAnIgnoredHeadlessRequest()
    {
        var description = new BrowserConfiguration { Visibility = BrowserVisibility.Headless }
            .DescribeVisibilityResolution();

        description.Should().Contain("IGNORED", "an explicit Visibility=Headless must be called out as ignored");
    }
}
