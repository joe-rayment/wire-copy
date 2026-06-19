// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-lmwm — the interactive-refresh prompt previously asserted
/// "Complete any captcha or login in the browser." unconditionally, even when
/// the headed load succeeded with no gate detected. The copy is now
/// verdict-aware: no verdict → honest "page loaded"; a verdict → copy that
/// names the variant.
/// </summary>
[Trait("Category", "Unit")]
public class InteractiveRefreshCopyTests
{
    [Fact]
    public void NoVerdict_SaysPageLoaded_NeverClaimsCaptcha()
    {
        var body = TerminalPageRenderer.GetInteractiveRefreshBody(requiredAction: null);

        body.Should().StartWith("Page loaded");
        body.ToLowerInvariant().Should().NotContain("captcha",
            "a healthy page must not be described as gated");
        body.ToLowerInvariant().Should().NotContain("login");
    }

    [Theory]
    [InlineData(HumanActionVariant.Captcha, "CAPTCHA")]
    [InlineData(HumanActionVariant.Login, "Log in")]
    [InlineData(HumanActionVariant.CookieConsent, "cookie")]
    [InlineData(HumanActionVariant.TwoFactor, "verification")]
    [InlineData(HumanActionVariant.Paywall, "paywall")]
    [InlineData(HumanActionVariant.RegionBlock, "region")]
    [InlineData(HumanActionVariant.BotBlock, "bots")]
    [InlineData(HumanActionVariant.RedirectLoop, "redirect")]
    public void Verdict_NamesTheVariant(HumanActionVariant variant, string expectedFragment)
    {
        var action = new HumanActionRequired(variant, "nytimes.com");

        var body = TerminalPageRenderer.GetInteractiveRefreshBody(action);

        body.ToLowerInvariant().Should().Contain(expectedFragment.ToLowerInvariant());
    }

    [Theory]
    [InlineData(HumanActionVariant.Captcha)]
    [InlineData(HumanActionVariant.Login)]
    [InlineData(HumanActionVariant.CookieConsent)]
    [InlineData(HumanActionVariant.TwoFactor)]
    [InlineData(HumanActionVariant.Paywall)]
    [InlineData(HumanActionVariant.RegionBlock)]
    [InlineData(HumanActionVariant.BotBlock)]
    [InlineData(HumanActionVariant.RedirectLoop)]
    [InlineData(HumanActionVariant.Generic)]
    public void RefreshBody_FitsWithinBoxWidth(HumanActionVariant variant)
    {
        // The interactive-refresh body is rendered un-truncated inside the centered box (≤54 usable
        // chars at 80 cols). A too-long BotBlock body overflowed the box (workspace-3rtr review), so
        // pin every variant's body to the same width budget GetHumanActionCopy is checked against.
        const int maxContentChars = 54; // MaxBoxContentWidth (56) - 2 padding spaces

        var body = TerminalPageRenderer.GetInteractiveRefreshBody(new HumanActionRequired(variant, "subdomain.nytimes.com"));

        body.Length.Should().BeLessThanOrEqualTo(
            maxContentChars,
            $"interactive-refresh body for {variant} '{body}' must fit in {maxContentChars} chars at 80 cols");
    }

    [Fact]
    public void GenericVerdict_AsksForTheRequiredAction()
    {
        var action = new HumanActionRequired(HumanActionVariant.Generic, "example.com");

        var body = TerminalPageRenderer.GetInteractiveRefreshBody(action);

        body.Should().Contain("required action");
    }

    [Fact]
    public void LayoutSetupPointer_NamesCtrlL_AndFitsTheBox()
    {
        // workspace-u5vu: Shift+I on a link-list page outside preview mode is
        // often a reach for the layout wizard — the prompt points at Ctrl+L.
        TerminalPageRenderer.LayoutSetupPointer.Should().Contain("Ctrl+l");
        TerminalPageRenderer.LayoutSetupPointer.Length.Should().BeLessThanOrEqualTo(54,
            "the pointer must fit the centered box content width at 80 cols");
    }
}
