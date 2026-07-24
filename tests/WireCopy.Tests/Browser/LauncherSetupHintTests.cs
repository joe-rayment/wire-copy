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
/// the launcher renders a non-focusable "Optional: add API keys · press c" hint
/// inside the wordmark/header box (replacing the trailing blank line). The
/// `c` keystroke opens the unified Setup screen (rebound from `S` in
/// workspace-jby8 to avoid the save-letter overlap). The hint must NOT add rows
/// to the header card and must NOT be reachable via the launcher's selection
/// model — it is chrome, not a grid item.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class LauncherSetupHintTests
{
    [Fact]
    public void ComputeHeaderPlusUrlBarLines_SetupHint_DoesNotChangeLargeHeaderHeight()
    {
        var without = LauncherRenderer.ComputeHeaderPlusUrlBarLines(100, showSetupHint: false);
        var with = LauncherRenderer.ComputeHeaderPlusUrlBarLines(100, showSetupHint: true);

        // workspace-pn5f: the large masthead card always carries a bottom
        // padding slot (Launcher.dc.html padding-bottom); the hint occupies
        // that slot when shown, so the header height NEVER changes — the
        // original workspace-ayt8 "hint must not add rows" contract.
        with.Should().Be(without,
            "the setup hint occupies the large masthead's padding slot, adding no rows");
    }

    [Fact]
    public void ComputeHeaderPlusUrlBarLines_SetupHintAddsOneRowOnNarrowHeader()
    {
        var without = LauncherRenderer.ComputeHeaderPlusUrlBarLines(60, showSetupHint: false);
        var with = LauncherRenderer.ComputeHeaderPlusUrlBarLines(60, showSetupHint: true);

        with.Should().Be(without + 1,
            "the compact narrow card has no padding slot, so the hint adds exactly one row");
    }

    [Fact]
    public void ComputeLayout_SetupHint_DoesNotChangeLargeHeaderLines()
    {
        var without = LauncherRenderer.ComputeLayout(100, 35, "Grid", showSetupHint: false);
        var with = LauncherRenderer.ComputeLayout(100, 35, "Grid", showSetupHint: true);

        with.HeaderLines.Should().Be(without.HeaderLines,
            "the hint rides the large masthead's padding slot (workspace-pn5f)");
    }

    [Fact]
    public void RenderLauncher_WithSetupHint_IncludesHintInsideHeaderBox()
    {
        var screen = RenderLauncherScreen(showSetupHint: true);

        screen.Should().Contain("Optional: add API keys",
            "the setup hint must be rendered when ShowSetupHint=true");
        screen.Should().Contain("press c",
            "the hint must advertise the dedicated keybinding");

        // Hint should appear before the URL bar in the rendered output —
        // i.e. inside the header box, not as a separate band below the URL bar.
        var hintIdx = screen.IndexOf("Optional: add API keys", System.StringComparison.Ordinal);
        var urlBarIdx = screen.IndexOf("Go to URL", System.StringComparison.Ordinal);
        urlBarIdx.Should().BeGreaterThan(0, "URL bar must render");
        hintIdx.Should().BeLessThan(urlBarIdx,
            "hint must render inside the header card, above the URL bar");
    }

    [Fact]
    public void RenderLauncher_WithoutSetupHint_OmitsHintLine()
    {
        var screen = RenderLauncherScreen(showSetupHint: false);

        screen.Should().NotContain("Optional: add API keys",
            "configured users must not see the hint anywhere");
        screen.Should().NotContain("→ Optional: add API keys for podcasts & AI layout · press c",
            "configured users must not see the dedicated-keybinding affordance");
    }

    [Fact]
    public void RenderLauncher_WithEmptyBookmarks_AndSetupHint_IncludesHintInHeader()
    {
        // Empty-state path is separate from the populated grid path; both must
        // honour the flag now that the hint is baked into BuildHeaderLines.
        var screen = RenderLauncherScreen(showSetupHint: true, bookmarks: new List<Bookmark>());

        screen.Should().Contain("Optional: add API keys");
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
