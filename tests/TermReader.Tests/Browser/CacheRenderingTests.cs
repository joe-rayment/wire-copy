// Educational and personal use only.

using FluentAssertions;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests for cache badge rendering in StatusBarRenderer and ArticleRenderer.
/// Uses Console.Out redirect to capture rendered text.
/// </summary>
[Trait("Category", "Unit")]
[Collection("ConsoleOutput")]
public class CacheRenderingTests
{
    private readonly IThemeProvider _themeProvider;
    private readonly RenderHelpers _helpers;
    private readonly StatusBarRenderer _statusBar;
    private readonly ArticleRenderer _article;

    public CacheRenderingTests()
    {
        _themeProvider = Substitute.For<IThemeProvider>();
        _themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        _helpers = new RenderHelpers();
        _statusBar = new StatusBarRenderer(_helpers, _themeProvider);
        _article = new ArticleRenderer(_helpers, _themeProvider);
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

    #region StatusBarRenderer - Hierarchical Cache Progress

    [Fact]
    public void StatusBar_Hierarchical_ShowsCachingProgress()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 10,
            CachedCount = 3,
            NeedsBrowserCount = 0,
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120, progress));

        output.Should().Contain("3/10 cached");
    }

    [Fact]
    public void StatusBar_Hierarchical_ShowsAllCached_WhenComplete()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 5,
            NeedsBrowserCount = 0,
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120, progress));

        output.Should().Contain("all cached");
    }

    [Fact]
    public void StatusBar_Hierarchical_ShowsAllCached_WhenNeedsBrowserAccountsForRest()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 3,
            NeedsBrowserCount = 2,
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120, progress));

        // IsComplete is true because CachedCount + NeedsBrowserCount >= Total
        output.Should().Contain("all cached");
    }

    [Fact]
    public void StatusBar_Hierarchical_NoCacheBadge_WhenNoProgress()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120));

        output.Should().NotContain("cached");
    }

    [Fact]
    public void StatusBar_Hierarchical_NoCacheBadge_WhenZeroLinks()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 0,
            CachedCount = 0,
            NeedsBrowserCount = 0,
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120, progress));

        output.Should().NotContain("cached");
    }

    #endregion

    #region StatusBarRenderer - CollectionItems Cache Progress

    [Fact]
    public void StatusBar_CollectionItems_ShowsCachingProgress()
    {
        var context = new NavigationContext { ViewMode = ViewMode.CollectionItems };
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 8,
            CachedCount = 5,
            NeedsBrowserCount = 0,
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.CollectionItems, 120, progress));

        output.Should().Contain("5/8 cached");
    }

    [Fact]
    public void StatusBar_CollectionItems_ShowsAllCached_WhenComplete()
    {
        var context = new NavigationContext { ViewMode = ViewMode.CollectionItems };
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 4,
            CachedCount = 4,
            NeedsBrowserCount = 0,
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.CollectionItems, 120, progress));

        output.Should().Contain("all cached");
    }

    [Fact]
    public void StatusBar_CollectionItems_NoCacheBadge_WhenNoProgress()
    {
        var context = new NavigationContext { ViewMode = ViewMode.CollectionItems };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.CollectionItems, 120));

        output.Should().NotContain("cached");
    }

    #endregion

    #region StatusBarRenderer - Non-Hierarchical Per-Page Cache Badge

    [Fact]
    public void StatusBar_Readable_ShowsPerPageCacheBadge_WhenFromCache()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            IsFromCache = true,
            CachedAt = DateTime.UtcNow.AddMinutes(-5),
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Readable, 120));

        output.Should().Contain("cached");
        output.Should().Contain("5m ago");
    }

    [Fact]
    public void StatusBar_Readable_NoCacheBadge_WhenNotFromCache()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Readable };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Readable, 120));

        output.Should().NotContain("cached");
    }

    #endregion

    #region Unified StatusBar - Reader View

    [Fact]
    public void UnifiedStatusBar_ReaderView_ShowsCacheBadge_WhenFromCache()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            IsFromCache = true,
            CachedAt = DateTime.UtcNow.AddMinutes(-2),
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Readable, 120,
                readerTotalLines: 100, readerContentWidth: 80, readerViewportHeight: 20));

        output.Should().Contain("cached");
        output.Should().Contain("2m ago");
    }

    [Fact]
    public void UnifiedStatusBar_ReaderView_NoCacheBadge_WhenNotFromCache()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Readable };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Readable, 120,
                readerTotalLines: 100, readerContentWidth: 80, readerViewportHeight: 20));

        output.Should().NotContain("cached");
    }

    [Fact]
    public void UnifiedStatusBar_ReaderView_ShowsReaderLabel()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Readable };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Readable, 120,
                readerTotalLines: 100, readerContentWidth: 80, readerViewportHeight: 20));

        output.Should().Contain("ReaderView");
    }

    [Fact]
    public void UnifiedStatusBar_ReaderView_ShowsLineAndWidthInfo()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            ScrollOffset = 9,
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Readable, 120,
                readerTotalLines: 100, readerContentWidth: 80, readerViewportHeight: 20));

        output.Should().Contain("L10/100");
        output.Should().Contain("W80");
    }

    #endregion

    #region PreloadProgress DTO

    [Fact]
    public void PreloadProgress_IsComplete_WhenAllCached()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 5,
            NeedsBrowserCount = 0,
        };

        progress.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void PreloadProgress_IsComplete_WhenCachedPlusNeedsBrowserEqualsTotal()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 10,
            CachedCount = 6,
            NeedsBrowserCount = 4,
        };

        progress.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void PreloadProgress_NotComplete_WhenPending()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 10,
            CachedCount = 3,
            NeedsBrowserCount = 2,
        };

        progress.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void PreloadProgress_IsComplete_WhenZeroTotal()
    {
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 0,
            CachedCount = 0,
            NeedsBrowserCount = 0,
        };

        progress.IsComplete.Should().BeTrue();
    }

    #endregion

    #region Two-Line Status Bar Format

    [Fact]
    public void StatusBar_OutputsTwoContentLines()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            CurrentPage = TermReader.Domain.Entities.Browser.Page.Create(
                "https://example.com",
                "<html></html>",
                new TermReader.Domain.ValueObjects.Browser.PageMetadata { Title = "Test" }),
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 80));

        // Line 1: mode label
        output.Should().Contain("LinkView");
        // Line 2: domain
        output.Should().Contain("example.com");
    }

    [Fact]
    public void StatusBar_Line1_ContainsModeLabel()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 80));

        output.Should().Contain("LinkView");
    }

    [Fact]
    public void StatusBar_Line2_ShowsDomain_WhenPageHasUrl()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            CurrentPage = TermReader.Domain.Entities.Browser.Page.Create(
                "https://example.com/article",
                "<html></html>",
                new TermReader.Domain.ValueObjects.Browser.PageMetadata { Title = "Test" }),
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120));

        output.Should().Contain("example.com");
    }

    #endregion

    #region Adaptive Hints

    [Fact]
    public void AdaptiveHints_FullTier_AtWideWidth()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);
        var hints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Hierarchical, p, 100);

        // Full tier includes save-all
        hints.Should().Contain("save-all");
    }

    [Fact]
    public void AdaptiveHints_DropsToSmaller_AtNarrowWidth()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);
        var hints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Hierarchical, p, 30);

        // Should drop to compact tier
        hints.Should().NotContain("save-all");
    }

    [Fact]
    public void AdaptiveHints_EmptyString_AtVeryNarrowWidth()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);
        var hints = StatusBarRenderer.GetAdaptiveHints(ViewMode.Hierarchical, p, 3);

        hints.Should().BeEmpty();
    }

    #endregion

    #region Progress Bar

    [Fact]
    public void FormatProgressBar_ShowsBlockCharactersAndCount()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);
        var bar = StatusBarRenderer.FormatProgressBar(3, 10, p);

        bar.Should().Contain("3/10 cached");
        bar.Should().Contain("\u2588"); // full block (eighth-block progress bar)
    }

    #endregion

    #region Cache Usage Warning

    [Fact]
    public void StatusBar_ShowsCacheWarning_WhenAbove90Percent()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120, cacheUsagePercent: 95));

        output.Should().Contain("cache 95%");
    }

    [Fact]
    public void StatusBar_NoCacheWarning_WhenBelow90Percent()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120, cacheUsagePercent: 50));

        output.Should().NotContain("cache 50%");
    }

    #endregion
}
