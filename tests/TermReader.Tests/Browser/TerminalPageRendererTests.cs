// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.UI;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests for TerminalPageRenderer.
///
/// Note: TerminalPageRenderer writes directly to Console (Console.Write, Console.WriteLine,
/// Console.SetCursorPosition, Console.ForegroundColor, etc.), which makes full unit testing
/// difficult without redirecting Console output. These tests verify the renderer can be
/// instantiated and called without throwing exceptions for various inputs.
///
/// Manual test scenarios (cannot be reliably automated):
/// - Verify header renders with title and URL in a box
/// - Verify link tree renders with selection highlighting
/// - Verify readable content wraps text at terminal width
/// - Verify search highlighting shows colored matches
/// - Verify scroll indicators show correct remaining count
/// - Verify status bar shows correct key bindings per view mode
/// </summary>
[Trait("Category", "Unit")]
public class TerminalPageRendererTests
{
    private readonly ILogger<TerminalPageRenderer> _logger;
    private readonly TerminalPageRenderer _sut;

    public TerminalPageRendererTests()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        _logger = Substitute.For<ILogger<TerminalPageRenderer>>();
        _sut = new TerminalPageRenderer(themeProvider, _logger);
    }

    private static Page CreateTestPage(
        string url = "https://example.com",
        string title = "Test Page",
        bool withLinks = true,
        bool withReadableContent = false)
    {
        var metadata = new PageMetadata { Title = title };
        var html = $"<html><head><title>{title}</title></head><body>Content</body></html>";
        var page = Page.Create(url, html, metadata);

        if (withLinks)
        {
            var links = new List<LinkInfo>
            {
                new LinkInfo { Url = $"{url}/link1", DisplayText = "Link One", Type = LinkType.Content, ImportanceScore = 80 },
                new LinkInfo { Url = $"{url}/link2", DisplayText = "Link Two", Type = LinkType.Navigation, ImportanceScore = 50 }
            };
            var tree = NavigationTree.Build(links);
            page.SetLinkTree(tree);
        }

        if (withReadableContent)
        {
            var readable = ReadableContent.Create(
                "Article Title",
                "This is the article content with enough words to be meaningful.",
                new List<string>
                {
                    "First paragraph of the article.",
                    "Second paragraph with more detail.",
                    "Third paragraph wrapping up."
                },
                "Author Name",
                new DateTime(2024, 1, 15));
            page.SetReadableContent(readable);
        }

        return page;
    }

    private static NavigationContext CreateContext(
        ViewMode mode = ViewMode.Hierarchical,
        int scrollOffset = 0,
        string? searchQuery = null)
    {
        return new NavigationContext
        {
            ViewMode = mode,
            ScrollOffset = scrollOffset,
            SearchQuery = searchQuery
        };
    }

    private static RenderOptions CreateOptions(int width = 80, int height = 24)
    {
        return new RenderOptions
        {
            TerminalWidth = width,
            TerminalHeight = height,
            MaxContentWidth = width
        };
    }

    [Fact]
    public void RenderHierarchical_WithValidPage_DoesNotThrow()
    {
        // Arrange
        var page = CreateTestPage(withLinks: true);
        var context = CreateContext();
        var options = CreateOptions();

        // Act & Assert - should not throw even when Console operations may fail
        var act = () => _sut.RenderHierarchical(page, context, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderHierarchical_WithNoLinks_DoesNotThrow()
    {
        // Arrange
        var page = CreateTestPage(withLinks: false);
        var context = CreateContext();
        var options = CreateOptions();

        // Act & Assert
        var act = () => _sut.RenderHierarchical(page, context, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderReadable_WithReadableContent_DoesNotThrow()
    {
        // Arrange
        var page = CreateTestPage(withReadableContent: true);
        var context = CreateContext(mode: ViewMode.Readable);
        var options = CreateOptions();

        // Act & Assert
        var act = () => _sut.RenderReadable(page, context, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderReadable_WithoutReadableContent_DoesNotThrow()
    {
        // Arrange
        var page = CreateTestPage(withReadableContent: false);
        var context = CreateContext(mode: ViewMode.Readable);
        var options = CreateOptions();

        // Act & Assert
        var act = () => _sut.RenderReadable(page, context, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderReadable_WithScrollOffset_DoesNotThrow()
    {
        // Arrange
        var page = CreateTestPage(withReadableContent: true);
        var context = CreateContext(mode: ViewMode.Readable, scrollOffset: 1);
        var options = CreateOptions();

        // Act & Assert
        var act = () => _sut.RenderReadable(page, context, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderReadable_WithSearchQuery_DoesNotThrow()
    {
        // Arrange
        var page = CreateTestPage(withReadableContent: true);
        var context = CreateContext(mode: ViewMode.Readable, searchQuery: "article");
        var options = CreateOptions();

        // Act & Assert
        var act = () => _sut.RenderReadable(page, context, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderLoading_DoesNotThrow()
    {
        // Act & Assert
        var act = () => _sut.RenderLoading("https://example.com/very/long/url/path");
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderError_DoesNotThrow()
    {
        // Act & Assert
        var act = () => _sut.RenderError("Connection refused", "https://example.com");
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderStatusBar_HierarchicalMode_DoesNotThrow()
    {
        // Arrange
        var context = CreateContext(mode: ViewMode.Hierarchical);

        // Act & Assert
        var act = () => _sut.RenderStatusBar(context, ViewMode.Hierarchical);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderStatusBar_ReadableMode_DoesNotThrow()
    {
        // Arrange
        var context = CreateContext(mode: ViewMode.Readable);

        // Act & Assert
        var act = () => _sut.RenderStatusBar(context, ViewMode.Readable);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderStatusBar_WithSearchQuery_DoesNotThrow()
    {
        // Arrange
        var context = CreateContext(searchQuery: "test");

        // Act & Assert
        var act = () => _sut.RenderStatusBar(context, ViewMode.Hierarchical);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderStatusBar_WithBackHistory_DoesNotThrow()
    {
        // Arrange
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            BackHistoryCount = 3
        };

        // Act & Assert
        var act = () => _sut.RenderStatusBar(context, ViewMode.Hierarchical);
        act.Should().NotThrow();
    }

    [Fact]
    public void Clear_DoesNotThrow()
    {
        // Act & Assert
        var act = () => _sut.Clear();
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderHierarchical_WithGroupedTree_DoesNotThrow()
    {
        // Arrange - Test with grouped tree structure (like real usage)
        var page = CreateTestPage(withLinks: false);
        var groupedLinks = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new List<LinkInfo>
            {
                new LinkInfo { Url = "https://example.com/1", DisplayText = "Article One", Type = LinkType.Content, ImportanceScore = 80 },
                new LinkInfo { Url = "https://example.com/2", DisplayText = "Article Two", Type = LinkType.Content, ImportanceScore = 70 }
            },
            [LinkType.Navigation] = new List<LinkInfo>
            {
                new LinkInfo { Url = "https://example.com/nav1", DisplayText = "Home", Type = LinkType.Navigation, ImportanceScore = 30 }
            }
        };
        var tree = NavigationTree.BuildWithGroups(groupedLinks);
        page.SetLinkTree(tree);

        var context = CreateContext();
        var options = CreateOptions();

        // Act & Assert
        var act = () => _sut.RenderHierarchical(page, context, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderHierarchical_WithNarrowTerminal_DoesNotThrow()
    {
        // Arrange
        var page = CreateTestPage(withLinks: true);
        var context = CreateContext();
        var options = CreateOptions(width: 40, height: 10);

        // Act & Assert
        var act = () => _sut.RenderHierarchical(page, context, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderReadable_WithManyParagraphs_DoesNotThrow()
    {
        // Arrange - Create page with many paragraphs to test scroll indicators
        var metadata = new PageMetadata { Title = "Long Article" };
        var page = Page.Create("https://example.com/long", "<html><body>Long</body></html>", metadata);

        var paragraphs = Enumerable.Range(1, 50)
            .Select(i => $"This is paragraph {i} of a very long article with lots of content.")
            .ToList();
        var readable = ReadableContent.Create("Long Article", string.Join(" ", paragraphs), paragraphs);
        page.SetReadableContent(readable);

        var context = CreateContext(mode: ViewMode.Readable, scrollOffset: 5);
        var options = CreateOptions();

        // Act & Assert
        var act = () => _sut.RenderReadable(page, context, options);
        act.Should().NotThrow();
    }

    #region Behavioral verification (Console.Out redirect)

    private string CaptureConsoleOutput(Action action)
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
    public void RenderLoading_OutputContainsLoadingText()
    {
        var output = CaptureConsoleOutput(() => _sut.RenderLoading("https://example.com"));
        output.Should().Contain("Loading");
    }

    [Fact]
    public void RenderLoading_OutputContainsUrl()
    {
        var output = CaptureConsoleOutput(() => _sut.RenderLoading("https://example.com/page"));
        output.Should().Contain("example.com");
    }

    [Fact]
    public void RenderError_OutputContainsErrorMessage()
    {
        var output = CaptureConsoleOutput(
            () => _sut.RenderError("Connection refused", "https://example.com"));
        output.Should().Contain("Connection refused");
    }

    [Fact]
    public void RenderError_OutputContainsHelpHint()
    {
        var output = CaptureConsoleOutput(
            () => _sut.RenderError("Error", "https://example.com"));
        output.Should().ContainAny("q", "quit", "back");
    }

    [Fact]
    public void RenderHierarchical_OutputContainsPageTitle()
    {
        var page = CreateTestPage(title: "My Test Page", withLinks: true);
        var context = CreateContext();
        var options = CreateOptions();

        var output = CaptureConsoleOutput(() => _sut.RenderHierarchical(page, context, options));
        output.Should().Contain("My Test Page");
    }

    [Fact]
    public void RenderHierarchical_OutputContainsLinkText()
    {
        var page = CreateTestPage(withLinks: true);
        var context = CreateContext();
        var options = CreateOptions();

        var output = CaptureConsoleOutput(() => _sut.RenderHierarchical(page, context, options));
        output.Should().ContainAny("Link One", "Link Two");
    }

    [Fact]
    public void RenderHierarchical_OutputContainsDomainText()
    {
        var page = CreateTestPage(withLinks: true);
        var context = CreateContext();
        var options = CreateOptions();

        var output = CaptureConsoleOutput(() => _sut.RenderHierarchical(page, context, options));
        output.Should().Contain("example.com");
    }

    [Fact]
    public void RenderReadable_OutputContainsArticleTitle()
    {
        var page = CreateTestPage(withReadableContent: true);
        var context = CreateContext(mode: ViewMode.Readable);
        var options = CreateOptions();

        // Headline is now part of the scrollable line cache (not a sticky header).
        // Pass wrappedLines containing the styled uppercase title.
        var wrappedLines = new List<string>
        {
            string.Empty,
            "  \x1b[1m\x1b[38;5;15mARTICLE TITLE\x1b[0m",
            "  \x1b[38;5;40m\x1b[2mBy Author Name | Jan 15, 2024 | 1 min read\x1b[0m",
            string.Empty,
            "  First paragraph of the article.",
            string.Empty,
            "  Second paragraph with more detail.",
            string.Empty,
            "  Third paragraph wrapping up.",
            string.Empty
        };

        var output = CaptureConsoleOutput(() => _sut.RenderReadable(page, context, options, wrappedLines));
        output.Should().Contain("ARTICLE TITLE");
    }

    [Fact]
    public void RenderReadable_OutputContainsParagraphContent()
    {
        var page = CreateTestPage(withReadableContent: true);
        var context = CreateContext(mode: ViewMode.Readable);
        var options = CreateOptions();

        var output = CaptureConsoleOutput(() => _sut.RenderReadable(page, context, options));
        output.Should().Contain("First paragraph");
    }

    #endregion
}
