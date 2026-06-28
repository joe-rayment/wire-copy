// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for cache badge rendering in StatusBarRenderer and ArticleRenderer.
/// Uses Console.Out redirect to capture rendered text.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
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

        output.Should().Contain("3/10");
    }

    [Fact]
    public void StatusBar_Hierarchical_SilentWhenFullyCached()
    {
        // workspace-wef6.2: full completion renders NO persistent badge — the
        // orchestrator announces "✓ all N cached" transiently instead, so the
        // chrome doesn't carry "✓ cached" forever post-warm.
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 5,
            CachedCount = 5,
            NeedsBrowserCount = 0,
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120, progress));

        output.Should().NotContain("cached");
        output.Should().NotContain("5/5");
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

        // IsComplete with mixed cached+needsJs shows partial count with checkmark
        output.Should().Contain("3/5");
        output.Should().Contain("\u2713");
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

        output.Should().Contain("5/8");
    }

    [Fact]
    public void StatusBar_CollectionItems_SilentWhenFullyCached()
    {
        // workspace-wef6.2: see the Hierarchical twin — completion is a
        // transient announcement, not permanent chrome.
        var context = new NavigationContext { ViewMode = ViewMode.CollectionItems };
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 4,
            CachedCount = 4,
            NeedsBrowserCount = 0,
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.CollectionItems, 120, progress));

        output.Should().NotContain("4/4");
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
    public void UnifiedStatusBar_ReaderView_HasNoModeBadge()
    {
        // workspace-wef6.2: reader view is visually obvious; the badge is gone.
        var context = new NavigationContext { ViewMode = ViewMode.Readable };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Readable, 120,
                readerTotalLines: 100, readerContentWidth: 80, readerViewportHeight: 20));

        output.Should().NotContain("ReaderView");
    }

    [Fact]
    public void UnifiedStatusBar_ReaderView_ShowsProgressPercent()
    {
        // workspace-wef6.2: "29%" (plus "~N min left" with a word count)
        // replaces the L/W trivia.
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Readable,
            ScrollOffset = 9,
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Readable, 120,
                readerTotalLines: 100, readerContentWidth: 80, readerViewportHeight: 20));

        output.Should().Contain("29%");
        output.Should().NotContain("L10/100");
        output.Should().NotContain("W80");
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

    #region Single-Line Status Bar Format

    [Fact]
    public void StatusBar_OutputsSingleContentLine()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            CurrentPage = WireCopy.Domain.Entities.Browser.Page.Create(
                "https://example.com",
                "<html></html>",
                new WireCopy.Domain.ValueObjects.Browser.PageMetadata { Title = "Test" }),
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 80));

        // workspace-wef6.2: no mode badge / duplicate domain — the line carries
        // the help affordance and adaptive hints instead.
        output.Should().Contain(":help");
        output.Should().NotContain("LinkView");
        output.Should().NotContain("example.com");
    }

    [Fact]
    public void StatusBar_LinkView_HasNoModeLabel()
    {
        // workspace-wef6.2: the LinkView badge was boilerplate; only the
        // collection views keep a mode badge.
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 80));

        output.Should().NotContain("LinkView");
    }

    [Fact]
    public void StatusBar_DoesNotDuplicateDomain_WhenPageHasUrl()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            CurrentPage = WireCopy.Domain.Entities.Browser.Page.Create(
                "https://example.com/article",
                "<html></html>",
                new WireCopy.Domain.ValueObjects.Browser.PageMetadata { Title = "Test" }),
        };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, 120));

        output.Should().NotContain("example.com",
            "the header already shows the domain — the bar must not duplicate it (workspace-wef6.2)");
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
    public void FormatProgressBar_ShowsCountAndCachedLabel()
    {
        var p = BuiltInThemes.Get(ThemeName.Phosphor);
        var bar = StatusBarRenderer.FormatProgressBar(3, 10, p);

        bar.Should().Contain("3/10");
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

    #region HumanAction Badge (workspace-0b9s)

    [Fact]
    public void StatusBar_RequiredAction_RendersPauseGlyphAndVerb()
    {
        // Live-rendered status-bar badge for the workspace-0b9s typed verdict.
        // Covers the fact that the bead's "⏸ {verb} at {domain} · Shift+O:open"
        // badge replaces the legacy "🍪✗ nytimes.com" copy when a typed verdict
        // is active.
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var action = new HumanActionRequired(HumanActionVariant.Captcha, "www.nytimes.com");

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Hierarchical,
                120,
                requiredAction: action));

        output.Should().Contain("⏸", "pause glyph (⏸) is the badge anchor");
        output.Should().Contain("captcha", "Captcha variant verb is 'captcha'");
        output.Should().Contain("www.nytimes.com");
        output.Should().Contain("|");
    }

    [Fact]
    public void StatusBar_RequiredAction_TakesPrecedenceOverLegacyCookieBadge()
    {
        // When both signals are present the typed verdict wins — drops the
        // misleading 🍪✗ copy that read as "something about cookies" when the
        // actual block was a CAPTCHA.
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var action = new HumanActionRequired(HumanActionVariant.Login, "wsj.com");

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(
                context,
                ViewMode.Hierarchical,
                120,
                missingCookieDomains: new[] { "wsj.com" },
                requiredAction: action));

        output.Should().Contain("⏸");
        output.Should().NotContain("\U0001F36A", "legacy cookie cookie-glyph badge must not be drawn alongside the typed verdict");
    }

    #endregion
}
