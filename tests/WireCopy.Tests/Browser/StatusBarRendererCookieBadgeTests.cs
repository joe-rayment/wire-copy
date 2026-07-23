// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the cookie-staleness UX badge rendered by <see cref="StatusBarRenderer"/>.
/// Surfaces silently failing pre-fetch on paywalled domains so the user knows to
/// recover via <c>:cookies import</c> (or <c>Shift+I</c>). Bead: workspace-siu3.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class StatusBarRendererCookieBadgeTests
{
    private readonly StatusBarRenderer _statusBar;

    public StatusBarRendererCookieBadgeTests()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        _statusBar = new StatusBarRenderer(new RenderHelpers(), themeProvider);
    }

    private static string CaptureConsoleOutput(Action action)
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);
            action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void StatusBar_RendersCookieBadge_WhenMissingCookieDomainsProvided()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Hierarchical,
                terminalWidth: 200,
                missingCookieDomains: new[] { "nytimes.com" }));

        output.Should().Contain("nytimes.com",
            "the badge surfaces the paywalled domain so the user knows what's missing");
        output.Should().Contain("Shift+I",
            "the badge offers an in-app recovery shortcut to login/import cookies");
        output.Should().Contain(":login",
            "the keystroke description tells the user what Shift+I does");
        output.Should().Contain("\u25c6\u2717",
            "the cookie badge glyph (◆✗) signals the cookie state at a glance");
    }

    [Fact]
    public void StatusBar_DoesNotRenderCookieBadge_WhenMissingCookieDomainsEmpty()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Hierarchical,
                terminalWidth: 200,
                missingCookieDomains: Array.Empty<string>()));

        output.Should().NotContain("\u25c6\u2717",
            "the badge must not appear when there are no missing-cookie domains");
        output.Should().NotContain("Shift+I",
            "the recovery shortcut should not be advertised when nothing is broken");
    }

    [Fact]
    public void StatusBar_DoesNotRenderCookieBadge_WhenMissingCookieDomainsNull()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Hierarchical,
                terminalWidth: 200,
                missingCookieDomains: null));

        output.Should().NotContain("\u25c6\u2717");
    }

    [Fact]
    public void StatusBar_RendersCookieBadge_InReaderView()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Readable };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Readable,
                terminalWidth: 200,
                missingCookieDomains: new[] { "wsj.com" }));

        output.Should().Contain("wsj.com",
            "the badge is visible in reader view as well — paywalled article without auth");
    }

    [Fact]
    public void StatusBar_RendersMultipleDomains_CommaSeparated()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Hierarchical,
                terminalWidth: 200,
                missingCookieDomains: new[] { "nytimes.com", "wsj.com" }));

        output.Should().Contain("nytimes.com,wsj.com",
            "multiple missing-cookie domains should fold into a compact comma-separated list");
    }
}

/// <summary>
/// Status-bar copy regression tests for workspace-kq4b. Generic verdicts must
/// no longer render the vague "action needed" copy that misled users on
/// Cloudflare-fronted sites; instead the badge says "uncertain interruption"
/// + Shift+R retry so the user has a clear next step. Specific variants keep
/// their verb-led copy (captcha / login / paywall / etc.) unchanged.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class StatusBarRendererActionBadgeTests
{
    private readonly StatusBarRenderer _statusBar;

    public StatusBarRendererActionBadgeTests()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        _statusBar = new StatusBarRenderer(new RenderHelpers(), themeProvider);
    }

    private static string Capture(Action action)
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);
            action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void StatusBar_GenericVariant_ReadsAsUncertain_NotActionNeeded()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var verdict = new HumanActionRequired(HumanActionVariant.Generic, "www.thestar.com");

        var output = Capture(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Hierarchical,
                terminalWidth: 200,
                requiredAction: verdict));

        output.Should().NotContain("action needed at",
            "workspace-kq4b: the bare 'action needed at {domain}' copy was vague and demonstrably wrong " +
            "on healthy CF-fronted pages; the Generic badge must use the 'uncertain interruption' phrasing");
        output.Should().Contain("uncertain interruption at www.thestar.com",
            "Generic verdicts should advertise the uncertainty honestly");
        output.Should().Contain("Shift+R",
            "the badge must surface the retry affordance so the user has a clear next step");
    }

    [Fact]
    public void StatusBar_CaptchaVariant_KeepsSpecificVerbCopy()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var verdict = new HumanActionRequired(HumanActionVariant.Captcha, "www.nytimes.com");

        var output = Capture(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Hierarchical,
                terminalWidth: 200,
                requiredAction: verdict));

        output.Should().Contain("captcha at www.nytimes.com",
            "specific variants keep the verb-led copy from workspace-0b9s — only Generic was changed");
        output.Should().Contain("|",
            "captcha variants still advertise | to dock the real browser");
        output.Should().NotContain("uncertain interruption");
    }

    [Theory]
    [InlineData(HumanActionVariant.Login, "login at")]
    [InlineData(HumanActionVariant.CookieConsent, "consent at")]
    [InlineData(HumanActionVariant.TwoFactor, "2FA at")]
    [InlineData(HumanActionVariant.Paywall, "paywall at")]
    [InlineData(HumanActionVariant.RegionBlock, "region-block at")]
    [InlineData(HumanActionVariant.RedirectLoop, "redirect loop at")]
    public void StatusBar_SpecificVariants_AllUseVerbLedCopy(HumanActionVariant variant, string expected)
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var verdict = new HumanActionRequired(variant, "example.com");

        var output = Capture(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Hierarchical,
                terminalWidth: 200,
                requiredAction: verdict));

        output.Should().Contain(expected);
        output.Should().NotContain("uncertain interruption");
    }
}
