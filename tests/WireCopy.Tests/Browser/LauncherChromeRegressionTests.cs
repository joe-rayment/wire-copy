// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;
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
/// Regression guard for workspace-m8x2: the launcher's rendered surface must
/// never leak StatusBar chrome (cache indicators, layout-variant labels,
/// missing-cookie badge, or a "Mode:" prefix). The shared StatusBarRenderer
/// is intentionally not invoked for the launcher view, but field population
/// drift or a stray future RenderStatusBar call would surface those markers.
/// This test renders the full launcher (header + URL bar + bookmark grid +
/// footer) at common terminal sizes and asserts none of the leak markers
/// appear.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class LauncherChromeRegressionTests
{
    private static readonly string[] LeakMarkers =
    [
        "cache ", // "cache 90%" usage warning, "cache " prefix on FormatCacheIndicator paths
        "Grid 1/", // LayoutVariantLabel from StatusBar's right column
        "\U0001F36A", // 🍪 missing-cookie badge
        "Layout:", // hypothetical layout label leak
        "Mode:", // hypothetical mode-badge leak
    ];

    [Theory]
    [InlineData(100, 35)]
    [InlineData(60, 24)]
    public void Launcher_RenderedSurface_DoesNotLeakStatusBarChrome(int width, int height)
    {
        var screen = StripAnsi(RenderLauncherScreen(width, height));

        foreach (var marker in LeakMarkers)
        {
            screen.Should().NotContain(marker,
                $"the launcher must not surface StatusBar marker '{marker}' at {width}x{height}");
        }
    }

    private static string StripAnsi(string s) =>
        Regex.Replace(s, "\x1b\\[[0-9;]*m", string.Empty);

    private static string RenderLauncherScreen(int width, int height)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var helpers = new RenderHelpers { TerminalHeight = height };
        var renderer = new LauncherRenderer(helpers, themeProvider);

        var options = new RenderOptions
        {
            TerminalWidth = width,
            TerminalHeight = height,
            ShowSetupHint = false,
        };

        var bookmarks = new List<Bookmark>
        {
            Bookmark.Create("Test", "https://example.com"),
        };

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderer.RenderLauncher(bookmarks, selectedIndex: 0, scrollOffset: 0, options);
            renderer.RenderFooter(width, bookmarks.Count);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return sw.ToString();
    }
}
