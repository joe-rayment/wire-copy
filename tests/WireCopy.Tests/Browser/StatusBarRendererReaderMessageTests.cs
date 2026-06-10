// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-w7oe: transient status messages must render in READER view too.
///
/// <para>
/// Root cause was NOT the status bar renderer (the direct-render tests below
/// pass against it unchanged). The reader frame's row budget omitted the
/// end-of-content rule: header (3) + content viewport + rule (1) + status bar
/// (2) exceeded the terminal height by one row whenever the article filled the
/// viewport. <c>RenderHelpers.WriteLineCore</c> silently drops lines past
/// <c>TerminalHeight</c>, so the LAST line of the frame — the status CONTENT
/// line carrying the mode badge, reader progress, and
/// <c>NavigationContext.StatusMessage</c> — was never emitted; only the
/// separator rule survived on the bottom row. Fixed by budgeting
/// <see cref="ReaderLayout.EndOfContentRuleLines"/> in
/// <see cref="ReaderLayout.ComputeContentHeight"/>.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class StatusBarRendererReaderMessageTests
{
    private readonly StatusBarRenderer _statusBar;

    public StatusBarRendererReaderMessageTests()
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

    [Theory]
    [InlineData(200)]
    [InlineData(150)]
    [InlineData(100)]
    public void ReaderView_WithProgressSegment_StillRendersStatusMessage(int width)
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            StatusMessage = "Layout tuning cancelled",
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Readable,
                terminalWidth: width,
                readerTotalLines: 300,
                readerContentWidth: 80,
                readerViewportHeight: 40));

        output.Should().Contain("Layout tuning cancelled",
            $"reader view at width {width} must show transient status messages beside the progress segment");
    }

    [Fact]
    public void RenderReadable_FullHeightArticle_StatusContentLineIsEmitted()
    {
        // Regression for the actual workspace-w7oe failure: an article long
        // enough to fill the whole content viewport. Pre-fix, the unbudgeted
        // end-of-content rule pushed the status content line past
        // TerminalHeight, where WriteLineCore drops it silently — the message
        // (and the entire mode/progress/help row) never reached the terminal.
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var renderer = new TerminalPageRenderer(themeProvider, Substitute.For<ILogger<TerminalPageRenderer>>());

        var metadata = new PageMetadata { Title = "Long Article" };
        var page = Page.Create(
            "https://example.com/article",
            "<html><head><title>Long Article</title></head><body>body</body></html>",
            metadata);
        page.SetReadableContent(ReadableContent.Create(
            "Long Article",
            "Body text.",
            ["Paragraph one.", "Paragraph two."],
            "Author",
            new DateTime(2024, 1, 15)));

        var wrappedLines = Enumerable.Range(1, 300).Select(i => $"Article line {i}").ToList();
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            ScrollOffset = 0,
            StatusMessage = "325 WPM",
        };
        var options = new RenderOptions { TerminalWidth = 100, TerminalHeight = 35, MaxContentWidth = 80 };

        var output = CaptureConsoleOutput(() => renderer.RenderReadable(page, context, options, wrappedLines));

        output.Should().Contain("325 WPM",
            "a full-height article must leave room for the status content line — " +
            "the end-of-content rule is part of the reader row budget (workspace-w7oe)");
        output.Should().Contain("Reader",
            "the mode badge lives on the same status content line and vanishes with it");
    }
}
