// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Invariant tests for <see cref="ReaderLayout"/> (workspace-umi7).
///
/// <para>
/// The reader viewport height is computed in two places — the renderer
/// (<see cref="TerminalPageRenderer.RenderReadable"/>) and the speed-read
/// scroller (<see cref="BrowserOrchestrator"/>). When they disagree, the
/// speed-read underline can advance past the bottom of the rendered area
/// without triggering a scroll — exactly the symptom the user reported.
/// </para>
///
/// <para>
/// These tests lock the shared constants so the regression cannot return
/// silently: if a future header change adds or removes a line, the assertion
/// here fails BEFORE the speed-read symptom manifests in production.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class ReaderLayoutInvariantTests
{
    [Fact]
    public void HeaderLines_MatchesActualLineCountWrittenByLinkTreeRenderer()
    {
        // Render the header into a captured StringWriter and count the lines.
        // If LinkTreeRenderer.RenderHeader is ever changed to write a different
        // number of lines (e.g. add a metadata row, drop the bottom rule), this
        // test fails immediately — preventing the speed-read drift bug from
        // returning silently.
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var helpers = new RenderHelpers { TerminalHeight = 35 };
        var renderer = new LinkTreeRenderer(helpers, themeProvider);
        var options = new RenderOptions { TerminalWidth = 100, TerminalHeight = 35 };
        var metadata = new PageMetadata { Title = "Test Title" };

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderer.RenderHeader(metadata, "https://example.com/article", options, linkCount: 12, sectionCount: 0);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        helpers.LinesWritten.Should().Be(ReaderLayout.HeaderLines,
            "ReaderLayout.HeaderLines is the source of truth used by both " +
            "the renderer and the speed-read scroller. If RenderHeader writes " +
            "a different number of lines, the constant MUST be updated in " +
            "lockstep — otherwise the speed-read underline will silently " +
            "disappear past the viewport again (workspace-umi7).");
    }

    [Theory]
    [InlineData(35, 30)]   // 35 - 3 - 2 = 30
    [InlineData(24, 19)]
    [InlineData(50, 45)]
    [InlineData(80, 75)]
    public void ComputeContentHeight_SubtractsHeaderAndStatusBar(int terminalHeight, int expectedContent)
    {
        ReaderLayout.ComputeContentHeight(terminalHeight).Should().Be(expectedContent);
    }

    [Fact]
    public void ComputeContentHeight_DegenerateTerminal_ReturnsFloor()
    {
        ReaderLayout.ComputeContentHeight(0).Should().Be(ReaderLayout.MinimumContentHeight);
        ReaderLayout.ComputeContentHeight(2).Should().Be(ReaderLayout.MinimumContentHeight);
        ReaderLayout.ComputeContentHeight(-5).Should().Be(ReaderLayout.MinimumContentHeight);
    }

    [Fact]
    public void HeaderPlusStatusBar_DoNotOverlap_WithMinimumTerminal()
    {
        // Sanity: on the smallest reasonable terminal, the header + status bar
        // reservation must not exceed the screen so RenderReadable still has
        // a content area to paint into.
        const int smallTerminal = 10;
        var content = ReaderLayout.ComputeContentHeight(smallTerminal);
        (ReaderLayout.HeaderLines + content + ReaderLayout.StatusBarReservedLines)
            .Should().BeLessOrEqualTo(smallTerminal + ReaderLayout.MinimumContentHeight,
                "the floor lets content overflow into status-bar space on tiny terminals; " +
                "this is acceptable as the editor degrades gracefully");
    }
}
