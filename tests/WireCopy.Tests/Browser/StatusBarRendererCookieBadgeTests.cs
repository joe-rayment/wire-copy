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
[Collection("ConsoleOutput")]
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
        output.Should().Contain("\U0001F36A",
            "the cookie glyph (🍪) signals the cookie state at a glance");
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

        output.Should().NotContain("\U0001F36A",
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

        output.Should().NotContain("\U0001F36A");
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
