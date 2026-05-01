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
/// Regression tests for workspace-fth0: when no API credentials are configured,
/// the launcher must surface a focusable "set up API keys" hint row above the
/// bookmark grid (instead of forcing the user into the Setup screen).
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class LauncherSetupHintTests
{
    [Fact]
    public void ComputeHeaderPlusUrlBarLines_WithSetupHint_AddsThreeLines()
    {
        var without = LauncherRenderer.ComputeHeaderPlusUrlBarLines(100, showSetupHint: false);
        var with = LauncherRenderer.ComputeHeaderPlusUrlBarLines(100, showSetupHint: true);

        with.Should().Be(without + 3,
            "the setup hint banner is rendered as 3 lines (blank + content + blank)");
    }

    [Fact]
    public void ComputeLayout_WithSetupHint_ReservesThreeExtraHeaderLines()
    {
        var without = LauncherRenderer.ComputeLayout(100, 35, "Grid", showSetupHint: false);
        var with = LauncherRenderer.ComputeLayout(100, 35, "Grid", showSetupHint: true);

        // HeaderLines is the line offset (in the virtual content stream) where
        // the bookmark grid begins. The setup hint occupies 3 lines between the
        // URL bar and the grid, so HeaderLines must grow by exactly 3.
        with.HeaderLines.Should().Be(without.HeaderLines + 3);
    }

    [Fact]
    public void SetupHintSelectedIndex_IsMinusTwo()
    {
        // Sentinel contract: -2 is the setup-hint slot, distinct from -1 (URL bar).
        LauncherRenderer.SetupHintSelectedIndex.Should().Be(-2);
    }

    [Fact]
    public void RenderLauncher_WithSetupHint_IncludesHintLine()
    {
        var screen = RenderLauncherScreen(showSetupHint: true, selectedIndex: 0);

        screen.Should().Contain("Set up API keys",
            "the setup hint banner must be rendered when ShowSetupHint=true");
    }

    [Fact]
    public void RenderLauncher_WithoutSetupHint_OmitsHintLine()
    {
        var screen = RenderLauncherScreen(showSetupHint: false, selectedIndex: 0);

        screen.Should().NotContain("Set up API keys",
            "the setup hint banner must NOT be rendered for configured users");
    }

    [Fact]
    public void RenderLauncher_WithEmptyBookmarks_AndSetupHint_IncludesHintLine()
    {
        // Empty-state path is separate from the grid path; both must honour the flag.
        var screen = RenderLauncherScreen(showSetupHint: true, selectedIndex: -2, bookmarks: new List<Bookmark>());

        screen.Should().Contain("Set up API keys");
    }

    private static string RenderLauncherScreen(
        bool showSetupHint,
        int selectedIndex,
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
            renderer.RenderLauncher(sample, selectedIndex, scrollOffset: 0, options);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return sw.ToString();
    }
}
