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
    [InlineData(HumanActionVariant.TwoFactor, "two-factor")]
    [InlineData(HumanActionVariant.Paywall, "paywall")]
    [InlineData(HumanActionVariant.RegionBlock, "region")]
    [InlineData(HumanActionVariant.RedirectLoop, "redirect")]
    public void Verdict_NamesTheVariant(HumanActionVariant variant, string expectedFragment)
    {
        var action = new HumanActionRequired(variant, "nytimes.com");

        var body = TerminalPageRenderer.GetInteractiveRefreshBody(action);

        body.ToLowerInvariant().Should().Contain(expectedFragment.ToLowerInvariant());
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
