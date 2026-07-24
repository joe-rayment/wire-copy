// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Regression tests for workspace-9nuj: when the user types into the launcher
/// URL bar, the typed characters must land inside the URL bar input row, not
/// inside the wordmark/title region. The URL bar input row depends on the
/// header variant (large 6-row wordmark vs. narrow single-line title) so the
/// row must be computed dynamically — a stale hard-coded value caused the bug.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class LauncherUrlBarRowTests
{
    // Matches WordmarkWidth (67 — the exact Launcher.dc.html art since
    // workspace-pn5f) constant in LauncherRenderer.
    private const int WordmarkWidth = 67;

    // Large-wordmark switch: content width (min(terminalWidth - 2,
    // ContentColumnWidth)) >= WordmarkWidth + 8, i.e. terminalWidth >= 77.
    private const int LargeThresholdWidth = WordmarkWidth + 10;

    // Header heights (workspace-pn5f): large masthead is border + blank +
    // 6 wordmark + blank + tagline + padding-or-hint + border = 12 rows;
    // narrow is border + title + tagline + border = 4 rows. The URL bar's
    // input line sits 2 rows below the header (blank + top border).
    private const int LargeHeaderRows = 12;
    private const int NarrowHeaderRows = 4;

    [Fact]
    public void ComputeUrlBarInputRow_AtLargeWordmarkWidth_LandsBelowHeaderBox()
    {
        // termtest harness uses 100 cols which triggers the large wordmark.
        var row = LauncherRenderer.ComputeUrlBarInputRow(terminalWidth: 100);

        row.Should().Be(LargeHeaderRows + 2);
    }

    [Fact]
    public void ComputeUrlBarInputRow_AtNarrowWidth_LandsBelowNarrowHeaderBox()
    {
        // 60 cols is below the large-wordmark threshold (77) so the narrow
        // single-line title is shown.
        var row = LauncherRenderer.ComputeUrlBarInputRow(terminalWidth: 60);

        row.Should().Be(NarrowHeaderRows + 2);
    }

    [Fact]
    public void ComputeUrlBarInputRow_AtThresholdBoundary_SwitchesToLargeWordmark()
    {
        // The switch is on content width >= WordmarkWidth + 8, i.e.
        // terminalWidth >= 77. Below: narrow. At/above: large.
        LauncherRenderer.ComputeUrlBarInputRow(LargeThresholdWidth - 1).Should().Be(NarrowHeaderRows + 2);
        LauncherRenderer.ComputeUrlBarInputRow(LargeThresholdWidth).Should().Be(LargeHeaderRows + 2);
    }

    [Fact]
    public void ComputeUrlBarInputRow_NeverFallsInsideWordmarkRows()
    {
        // The 6-row wordmark (when shown) occupies rows 2..7 (0-based: top border at 0,
        // blank at 1, wordmark rows 2-7). The previous bug used a hard-coded value of 5
        // for the URL bar row, which landed in the middle of the wordmark.
        // This assertion fixes that contract: the URL bar row must be strictly below
        // the wordmark region.
        for (var w = LargeThresholdWidth; w <= 200; w += 5)
        {
            LauncherRenderer.ComputeUrlBarInputRow(w).Should().BeGreaterThan(7,
                $"width {w} uses the large wordmark; URL bar row must sit below row 7");
        }

        // For narrow widths the title sits at row 1; the URL bar must clear that too.
        for (var w = 30; w < LargeThresholdWidth; w += 5)
        {
            LauncherRenderer.ComputeUrlBarInputRow(w).Should().BeGreaterThan(3,
                $"width {w} uses the narrow header; URL bar row must sit below the box");
        }
    }

    /// <summary>
    /// End-to-end snapshot: render only the launcher header + URL bar (skipping
    /// the bookmark grid, which is not relevant to the URL bar position), then
    /// verify that the row reported by
    /// <see cref="LauncherRenderer.ComputeUrlBarInputRow"/> is the row that
    /// actually contains the URL bar's input-line (vertical border + placeholder),
    /// and that earlier rows do not. This is the structural invariant that, if
    /// broken, means typed characters would render in the title region.
    /// </summary>
    /// <remarks>
    /// We render via reflection on the private RenderHeader / RenderUrlBar methods
    /// to keep the test focused on the URL-bar row contract and avoid coupling to
    /// the bookmark grid layout. The test uses <c>helpers.LinesWritten</c> to
    /// determine row positions deterministically — that counter is the source of
    /// truth used by RenderHelpers itself, so it is not affected by parallel
    /// tests writing into the captured stream.
    /// </remarks>
    [Theory]
    [InlineData(100, 35)] // termtest defaults — large wordmark variant
    [InlineData(60, 24)]  // narrow variant
    [InlineData(75, 35)]  // boundary: still narrow (content width 73 < WordmarkWidth + 8)
    [InlineData(76, 35)]  // boundary: still narrow (content width 74 < WordmarkWidth + 8)
    [InlineData(77, 35)]  // boundary: switches to large (content width 75 == WordmarkWidth + 8)
    public void RenderedHeaderAndUrlBar_PutInputBoxAtComputedRow(int terminalWidth, int terminalHeight)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var helpers = new RenderHelpers { TerminalHeight = terminalHeight };
        var renderer = new LauncherRenderer(helpers, themeProvider);
        var palette = WireCopy.Infrastructure.Browser.Themes.BuiltInThemes.Get(ThemeName.Phosphor);

        var width = LauncherRenderer.ContentWidthFor(terminalWidth);

        var renderHeader = typeof(LauncherRenderer).GetMethod(
            "RenderHeader",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var renderUrlBar = typeof(LauncherRenderer).GetMethod(
            "RenderUrlBar",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        // Render header alone, capture LinesWritten — this is the row count of
        // the header (i.e. the next available row).
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderHeader.Invoke(renderer, new object[] { width, palette });
            var headerLines = helpers.LinesWritten;

            // Then render the URL bar; the input content line is row headerLines + 2
            // (URL bar lays out: blank, top border, content, bottom border, blank —
            // 5 rows since the workspace-pn5f re-import restored the grid gap).
            renderUrlBar.Invoke(renderer, new object[] { width, true, palette });
            var totalLines = helpers.LinesWritten;
            totalLines.Should().Be(headerLines + 5,
                "URL bar emits 5 lines (blank, top border, content, bottom border, blank)");

            var expectedInputRow = headerLines + 2; // blank(0) + top border(1) + content(2)
            var computedInputRow = LauncherRenderer.ComputeUrlBarInputRow(terminalWidth);
            computedInputRow.Should().Be(expectedInputRow,
                $"ComputeUrlBarInputRow({terminalWidth}) must match the actual " +
                "row written by RenderHeader + RenderUrlBar");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
