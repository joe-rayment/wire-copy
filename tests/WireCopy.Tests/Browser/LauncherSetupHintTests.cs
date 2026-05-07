// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Regression tests for workspace-ayt8: when no API credentials are configured,
/// the launcher renders a non-focusable "Set up API keys · press S" hint
/// inside the wordmark/header box (replacing the trailing blank line). The
/// `S` keystroke opens the unified Setup screen. The hint must NOT add rows
/// to the header card and must NOT be reachable via the launcher's selection
/// model — it is chrome, not a grid item.
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class LauncherSetupHintTests
{
    [Fact]
    public void ComputeHeaderPlusUrlBarLines_IdenticalRegardlessOfSetupHint()
    {
        var without = LauncherRenderer.ComputeHeaderPlusUrlBarLines(100, showSetupHint: false);
        var with = LauncherRenderer.ComputeHeaderPlusUrlBarLines(100, showSetupHint: true);

        with.Should().Be(without,
            "the setup hint lives inside the header card and adds zero rows; " +
            "all callers (scroll math, URL-bar row) must see the same offsets " +
            "regardless of first-run state");
    }

    [Fact]
    public void ComputeLayout_IdenticalHeaderLinesRegardlessOfSetupHint()
    {
        var without = LauncherRenderer.ComputeLayout(100, 35, "Grid", showSetupHint: false);
        var with = LauncherRenderer.ComputeLayout(100, 35, "Grid", showSetupHint: true);

        with.HeaderLines.Should().Be(without.HeaderLines,
            "header card height is constant — the setup hint replaces a blank line, " +
            "not an additional row");
        with.VisibleRows.Should().Be(without.VisibleRows);
    }

    [Fact]
    public void RenderLauncher_WithSetupHint_IncludesHintInsideHeaderBox()
    {
        var screen = RenderLauncherScreen(showSetupHint: true);

        screen.Should().Contain("Set up API keys",
            "the setup hint must be rendered when ShowSetupHint=true");
        screen.Should().Contain("press S",
            "the hint must advertise the dedicated keybinding");

        // Hint should appear before the URL bar in the rendered output —
        // i.e. inside the header box, not as a separate band below the URL bar.
        var hintIdx = screen.IndexOf("Set up API keys", System.StringComparison.Ordinal);
        var urlBarIdx = screen.IndexOf("Go to URL", System.StringComparison.Ordinal);
        urlBarIdx.Should().BeGreaterThan(0, "URL bar must render");
        hintIdx.Should().BeLessThan(urlBarIdx,
            "hint must render inside the header card, above the URL bar");
    }

    [Fact]
    public void RenderLauncher_WithoutSetupHint_OmitsHintLine()
    {
        var screen = RenderLauncherScreen(showSetupHint: false);

        screen.Should().NotContain("Set up API keys",
            "configured users must not see the hint anywhere");
        screen.Should().NotContain("press S",
            "configured users must not see the dedicated-keybinding affordance");
    }

    [Fact]
    public void RenderLauncher_WithEmptyBookmarks_AndSetupHint_IncludesHintInHeader()
    {
        // Empty-state path is separate from the populated grid path; both must
        // honour the flag now that the hint is baked into BuildHeaderLines.
        var screen = RenderLauncherScreen(showSetupHint: true, bookmarks: new List<Bookmark>());

        screen.Should().Contain("Set up API keys");
    }

    private static string RenderLauncherScreen(
        bool showSetupHint,
        List<Bookmark>? bookmarks = null)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var helpers = new RenderHelpers { TerminalHeight = 35 };
        var renderer = new LauncherRenderer(helpers, themeProvider);

        var options = new RenderOptions
        {
            TerminalWidth = 100,
            TerminalHeight = 35,
            ShowSetupHint = showSetupHint,
        };

        var sample = bookmarks ?? new List<Bookmark>
        {
            Bookmark.Create("Test", "https://example.com"),
        };

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            // selectedIndex doesn't affect the hint anymore — it's chrome, not focusable.
            renderer.RenderLauncher(sample, selectedIndex: 0, scrollOffset: 0, options);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return sw.ToString();
    }
}
