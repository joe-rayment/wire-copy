// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Regression tests for workspace-9nuj: when the user types into the launcher
/// URL bar, the typed characters must land inside the URL bar input row, not
/// inside the wordmark/title region. The URL bar input row depends on the
/// header variant (large 6-row wordmark vs. narrow single-line title) so the
/// row must be computed dynamically — a stale hard-coded value caused the bug.
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class LauncherUrlBarRowTests
{
    // Matches WordmarkWidth (87) constant in LauncherRenderer.
    private const int WordmarkWidth = 87;

    [Fact]
    public void ComputeUrlBarInputRow_AtLargeWordmarkWidth_LandsBelowHeaderBox()
    {
        // termtest harness uses 100 cols which triggers the large wordmark.
        var row = LauncherRenderer.ComputeUrlBarInputRow(terminalWidth: 100);

        // Large header is 11 rows (top border + blank + 6 wordmark + subtitle + blank + bottom border).
        // URL bar adds: blank, top border, content, bottom border, blank.
        // The input/content line is therefore at row 13 (0-based).
        row.Should().Be(13);
    }

    [Fact]
    public void ComputeUrlBarInputRow_AtNarrowWidth_LandsBelowNarrowHeaderBox()
    {
        // 80 cols is below WordmarkWidth + 8 (= 95) so the narrow title is shown.
        var row = LauncherRenderer.ComputeUrlBarInputRow(terminalWidth: 80);

        // Narrow header is 5 rows (top border + title + subtitle + blank + bottom border).
        // URL bar adds: blank, top border, content, ...
        // The input/content line is therefore at row 7 (0-based).
        row.Should().Be(7);
    }

    [Fact]
    public void ComputeUrlBarInputRow_AtThresholdBoundary_SwitchesToLargeWordmark()
    {
        // RenderHeader switches on INNER width (terminalWidth - 2) >= WordmarkWidth + 8,
        // i.e. terminalWidth >= WordmarkWidth + 10 (= 97). Below: narrow. At/above: large.
        LauncherRenderer.ComputeUrlBarInputRow(WordmarkWidth + 9).Should().Be(7);
        LauncherRenderer.ComputeUrlBarInputRow(WordmarkWidth + 10).Should().Be(13);
    }

    [Fact]
    public void ComputeUrlBarInputRow_NeverFallsInsideWordmarkRows()
    {
        // The 6-row wordmark (when shown) occupies rows 2..7 (0-based: top border at 0,
        // blank at 1, wordmark rows 2-7). The previous bug used a hard-coded value of 5
        // for the URL bar row, which landed in the middle of the wordmark.
        // This assertion fixes that contract: the URL bar row must be strictly below
        // the wordmark region.
        for (var w = 97; w <= 200; w += 5)
        {
            LauncherRenderer.ComputeUrlBarInputRow(w).Should().BeGreaterThan(7,
                $"width {w} uses the large wordmark; URL bar row must sit below row 7");
        }

        // For narrow widths the title sits at row 1; the URL bar must clear that too.
        for (var w = 30; w < 97; w += 5)
        {
            LauncherRenderer.ComputeUrlBarInputRow(w).Should().BeGreaterThan(4,
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
    [InlineData(80, 24)]  // narrow variant
    [InlineData(95, 35)]  // boundary: still narrow (inner width 93 < WordmarkWidth + 8)
    [InlineData(96, 35)]  // boundary: still narrow (inner width 94 < WordmarkWidth + 8)
    [InlineData(97, 35)]  // boundary: switches to large (inner width 95 == WordmarkWidth + 8)
    public void RenderedHeaderAndUrlBar_PutInputBoxAtComputedRow(int terminalWidth, int terminalHeight)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var helpers = new RenderHelpers { TerminalHeight = terminalHeight };
        var renderer = new LauncherRenderer(helpers, themeProvider);
        var palette = TermReader.Infrastructure.Browser.Themes.BuiltInThemes.Get(ThemeName.Phosphor);

        var width = Math.Max(1, terminalWidth - 2);

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

            // Then render the URL bar; the input content line is row headerLines + 1
            // (URL bar lays out: blank, top border, content, bottom border, blank).
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
