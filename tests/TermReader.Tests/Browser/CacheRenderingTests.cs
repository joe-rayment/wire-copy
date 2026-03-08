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

        output.Should().Contain("caching 3/10");
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

        output.Should().NotContain("caching");
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

        output.Should().NotContain("caching");
        output.Should().NotContain("all cached");
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

    #region ArticleRenderer - Reader Status Bar Cache Badge

    [Fact]
    public void ArticleReaderStatusBar_ShowsCacheBadge_WhenFromCache()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            IsFromCache = true,
            CachedAt = DateTime.UtcNow.AddMinutes(-2),
        };

        var output = CaptureConsoleOutput(() =>
            _article.RenderReaderStatusBar(context, 100, 80, 20, 120));

        output.Should().Contain("cached");
        output.Should().Contain("2m ago");
    }

    [Fact]
    public void ArticleReaderStatusBar_NoCacheBadge_WhenNotFromCache()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Readable };

        var output = CaptureConsoleOutput(() =>
            _article.RenderReaderStatusBar(context, 100, 80, 20, 120));

        output.Should().NotContain("cached");
    }

    [Fact]
    public void ArticleReaderStatusBar_ShowsReaderLabel()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Readable };

        var output = CaptureConsoleOutput(() =>
            _article.RenderReaderStatusBar(context, 100, 80, 20, 120));

        output.Should().Contain("Reader");
    }

    [Fact]
    public void ArticleReaderStatusBar_ShowsLineAndWidthInfo()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            ScrollOffset = 9, // L10 (0-based -> 1-based)
        };

        var output = CaptureConsoleOutput(() =>
            _article.RenderReaderStatusBar(context, 100, 80, 20, 120));

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
}
