// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-1m3h.4: the end-of-article footer (— end — / word count / read time)
/// must be part of the cached line list itself so it scrolls into view as real
/// content on long articles, instead of only being painted when spare viewport
/// rows remained (which only short articles ever had).
/// </summary>
[Trait("Category", "Unit")]
public class LineCacheEndFooterTests
{
    private readonly NavigationService _navigationService;
    private readonly LineCacheManager _sut;
    private readonly RenderOptions _options;

    public LineCacheEndFooterTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _navigationService = new NavigationService(logger);

        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        _sut = new LineCacheManager(_navigationService, themeProvider);

        _options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 24,
            MaxContentWidth = 60,
        };
    }

    private void NavigateToArticle(params string[] paragraphs)
    {
        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com/article",
            "<html><body>Test</body></html>",
            new Domain.ValueObjects.Browser.PageMetadata { Title = "Test Article" });
        page.SetReadableContent(Domain.Entities.Browser.ReadableContent.Create(
            "Test Article", "Test content", paragraphs.ToList()));
        _navigationService.NavigateTo(page);
        _navigationService.SetViewMode(ViewMode.Readable);
    }

    [Fact]
    public void EnsureLineCache_AppendsFooterAsRealCachedLines()
    {
        NavigateToArticle("First paragraph with several words.", "Second paragraph here.");

        _sut.EnsureLineCache(_options);

        var lines = _sut.CachedLines!;
        var footerStart = _sut.ContentLineCount;

        lines.Count.Should().Be(footerStart + 4, "the footer is exactly 4 lines: blank, — end —, stats, blank");
        lines[footerStart].Should().BeEmpty("footer line 1 is a blank separator");
        lines[footerStart + 1].Should().Contain("— end —");
        lines[footerStart + 2].Should().Contain("min read");
        lines[footerStart + 2].Should().Contain("words");
        lines[footerStart + 3].Should().BeEmpty("footer line 4 is a blank separator");
    }

    [Fact]
    public void EnsureLineCache_FooterStatsMatchParagraphWordCount()
    {
        NavigateToArticle("one two three", "four five");

        _sut.EnsureLineCache(_options);

        var stats = _sut.CachedLines![_sut.ContentLineCount + 2];
        stats.Should().Contain("5 words");
        stats.Should().Contain("1 min read", "5 words at 250 wpm rounds up to 1 minute");
    }

    [Fact]
    public void EnsureLineCache_FooterPresentForLongArticles()
    {
        // The old bug: on articles longer than the viewport the footer never
        // rendered because it was only painted into SPARE viewport rows.
        // As cached lines it exists regardless of article length.
        var paragraphs = Enumerable.Range(1, 60)
            .Select(i => $"Paragraph {i} with some words in it.")
            .ToArray();
        NavigateToArticle(paragraphs);

        _sut.EnsureLineCache(_options);

        _sut.CachedLines!.Count.Should().BeGreaterThan(60, "sanity: article far exceeds a viewport");
        _sut.CachedLines.Should().Contain(l => l.Contains("— end —"),
            "the footer must exist in the scrollable line list even when no spare viewport rows exist");
        _sut.ContentLineCount.Should().Be(_sut.CachedLines!.Count - 4);
    }

    [Fact]
    public void InvalidateLineCache_ResetsContentLineCount()
    {
        NavigateToArticle("Some paragraph.");
        _sut.EnsureLineCache(_options);
        _sut.ContentLineCount.Should().BeGreaterThan(0);

        _sut.InvalidateLineCache();

        _sut.ContentLineCount.Should().Be(0);
    }

    [Fact]
    public void EnsureLineCache_Rewrap_KeepsFooterAtNewWidth()
    {
        NavigateToArticle("A reasonably long paragraph that wraps differently at different widths, with enough words to matter.");
        _sut.EnsureLineCache(_options);
        var narrowCount = _sut.CachedLines!.Count;

        _sut.InvalidateLineCache();
        _sut.EnsureLineCache(_options with { MaxContentWidth = 100 });

        _sut.CachedLines!.Count.Should().Be(_sut.ContentLineCount + 4);
        _sut.CachedLines.Should().Contain(l => l.Contains("— end —"));
        _sut.CachedLines!.Count.Should().BeLessThanOrEqualTo(narrowCount, "wider wrap produces no more lines");
    }

    [Fact]
    public void SetCacheForTesting_DefaultsContentLineCountToAllLines()
    {
        _sut.SetCacheForTesting(new List<string> { "a", "b", "c" }, 60);

        _sut.ContentLineCount.Should().Be(3);
    }

    [Fact]
    public void BuildEndOfArticleFooterLines_CentersWithinContentWidth()
    {
        var content = Domain.Entities.Browser.ReadableContent.Create(
            "T", "t", new List<string> { "one two three four" });
        var palette = BuiltInThemes.Get(ThemeName.Phosphor);

        var footer = LineCacheManager.BuildEndOfArticleFooterLines(content, maxWidth: 60, palette);

        footer.Should().HaveCount(4);
        var endLine = footer[1];
        var visible = LineCacheManager.VisibleLength(endLine);
        var leadingSpaces = endLine.TakeWhile(c => c == ' ').Count();
        leadingSpaces.Should().Be((60 - "— end —".Length) / 2,
            "the marker is centered in the content column; the renderer centers the column itself");
        visible.Should().BeLessThanOrEqualTo(60);
    }
}
