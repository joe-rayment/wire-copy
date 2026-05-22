// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.Cache;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class CacheDiagnosticsTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly DiskCacheStore _diskStore;
    private readonly InMemoryPageCache _cache;
    private readonly CacheConfiguration _config;

    public CacheDiagnosticsTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "wirecopy-diag-test-" + Guid.NewGuid().ToString("N"));
        _config = new CacheConfiguration
        {
            MaxSizeBytes = 10 * 1024 * 1024,
            MaxEntries = 50,
            DefaultTtlSeconds = 3600,
            MaxEntrySizeBytes = 5 * 1024 * 1024,
            EvictionSweepIntervalSeconds = 3600,
            MaxDiskSizeBytes = 500L * 1024 * 1024,
        };
        _diskStore = new DiskCacheStore(_cacheDir, NullLogger<InMemoryPageCache>.Instance, _config.MaxDiskSizeBytes);
        _cache = new InMemoryPageCache(
            Options.Create(_config),
            NullLogger<InMemoryPageCache>.Instance,
            _diskStore);
    }

    public void Dispose()
    {
        _cache.Dispose();
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
    }

    #region CacheStats new properties

    [Fact]
    public void CacheStats_UsagePercent_ReturnsCorrectPercentage()
    {
        var stats = new CacheStats
        {
            TotalSizeBytes = 50 * 1024 * 1024,
            MaxSizeBytes = 100 * 1024 * 1024
        };

        stats.UsagePercent.Should().Be(50.0);
    }

    [Fact]
    public void CacheStats_UsagePercent_ZeroWhenMaxSizeIsZero()
    {
        var stats = new CacheStats
        {
            TotalSizeBytes = 100,
            MaxSizeBytes = 0
        };

        stats.UsagePercent.Should().Be(0);
    }

    [Fact]
    public void CacheStats_UsagePercent_FullCache()
    {
        var stats = new CacheStats
        {
            TotalSizeBytes = 100 * 1024 * 1024,
            MaxSizeBytes = 100 * 1024 * 1024
        };

        stats.UsagePercent.Should().Be(100.0);
    }

    [Fact]
    public void CacheStats_UsagePercent_EmptyCache()
    {
        var stats = new CacheStats
        {
            TotalSizeBytes = 0,
            MaxSizeBytes = 100 * 1024 * 1024
        };

        stats.UsagePercent.Should().Be(0);
    }

    [Fact]
    public void CacheStats_DiskUsagePercent_ReturnsCorrectPercentage()
    {
        var stats = new CacheStats
        {
            DiskCacheSizeBytes = 250L * 1024 * 1024,
            MaxDiskSizeBytes = 500L * 1024 * 1024
        };

        stats.DiskUsagePercent.Should().Be(50.0);
    }

    [Fact]
    public void CacheStats_DiskUsagePercent_ZeroWhenMaxDiskSizeIsZero()
    {
        var stats = new CacheStats
        {
            DiskCacheSizeBytes = 100,
            MaxDiskSizeBytes = 0
        };

        stats.DiskUsagePercent.Should().Be(0);
    }

    [Fact]
    public void CacheStats_FormattedDiskSize_FormatsCorrectly()
    {
        var stats = new CacheStats
        {
            DiskCacheSizeBytes = 5 * 1024 * 1024,
            MaxDiskSizeBytes = 500L * 1024 * 1024
        };

        stats.FormattedDiskSize.Should().Be("5.0 MB");
        stats.FormattedMaxDiskSize.Should().Be("500.0 MB");
    }

    [Fact]
    public void CacheStats_FormattedDiskSize_ByteRange()
    {
        var stats = new CacheStats
        {
            DiskCacheSizeBytes = 512,
            MaxDiskSizeBytes = 1024
        };

        stats.FormattedDiskSize.Should().Be("512 B");
        stats.FormattedMaxDiskSize.Should().Be("1.0 KB");
    }

    [Fact]
    public void CacheStats_ArticleCacheCount_DefaultsToZero()
    {
        var stats = new CacheStats();
        stats.ArticleCacheCount.Should().Be(0);
    }

    [Fact]
    public void CacheStats_ArticleCacheCount_CanBeSet()
    {
        var stats = new CacheStats { ArticleCacheCount = 42 };
        stats.ArticleCacheCount.Should().Be(42);
    }

    #endregion

    #region DiskCacheStore diagnostics

    [Fact]
    public void DiskCacheStore_GetFileCount_ReturnsZeroWhenDirectoryDoesNotExist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N"));
        var store = new DiskCacheStore(dir, NullLogger<InMemoryPageCache>.Instance);

        store.GetFileCount().Should().Be(0);
    }

    [Fact]
    public void DiskCacheStore_GetFileCount_CountsJsonFiles()
    {
        var (result1, meta1) = CreateEntry("https://example.com/1", "<html>page 1</html>");
        var (result2, meta2) = CreateEntry("https://example.com/2", "<html>page 2</html>");
        var (result3, meta3) = CreateEntry("https://example.com/3", "<html>page 3</html>");

        _diskStore.Write(meta1.NormalizedUrl, result1, meta1);
        _diskStore.Write(meta2.NormalizedUrl, result2, meta2);
        _diskStore.Write(meta3.NormalizedUrl, result3, meta3);

        _diskStore.GetFileCount().Should().Be(3);
    }

    [Fact]
    public void DiskCacheStore_GetTotalSizeBytes_ReturnsZeroWhenDirectoryDoesNotExist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N"));
        var store = new DiskCacheStore(dir, NullLogger<InMemoryPageCache>.Instance);

        store.GetTotalSizeBytes().Should().Be(0);
    }

    [Fact]
    public void DiskCacheStore_GetTotalSizeBytes_SumsFileSizes()
    {
        var (result, metadata) = CreateEntry("https://example.com/sized", "<html>" + new string('X', 1000) + "</html>");
        _diskStore.Write(metadata.NormalizedUrl, result, metadata);

        var totalSize = _diskStore.GetTotalSizeBytes();
        totalSize.Should().BeGreaterThan(0);

        // Verify it matches actual file sizes
        var actualSize = new DirectoryInfo(_cacheDir).EnumerateFiles("*.json").Sum(f => f.Length);
        totalSize.Should().Be(actualSize);
    }

    [Fact]
    public void DiskCacheStore_GetFileCount_ReturnsZeroAfterClearAll()
    {
        var (result, metadata) = CreateEntry("https://example.com/cleartest", "<html>test</html>");
        _diskStore.Write(metadata.NormalizedUrl, result, metadata);
        _diskStore.GetFileCount().Should().Be(1);

        _diskStore.ClearAll();
        _diskStore.GetFileCount().Should().Be(0);
    }

    [Fact]
    public void DiskCacheStore_MaxDiskSizeBytes_ExposesConfiguredValue()
    {
        _diskStore.MaxDiskSizeBytes.Should().Be(_config.MaxDiskSizeBytes);
    }

    #endregion

    #region InMemoryPageCache.GetStats with disk stats

    [Fact]
    public void GetStats_IncludesDiskCacheStats()
    {
        var result = CreatePageLoadResult("https://example.com/disktest", "<html>disk test</html>");
        _cache.Put("https://example.com/disktest", result);

        // Give disk write a moment to complete (async write-behind)
        Thread.Sleep(1000);

        var stats = _cache.GetStats();
        stats.DiskCacheFileCount.Should().BeGreaterOrEqualTo(1);
        stats.DiskCacheSizeBytes.Should().BeGreaterThan(0);
        stats.MaxDiskSizeBytes.Should().Be(_config.MaxDiskSizeBytes);
    }

    [Fact]
    public void GetStats_DiskStatsAreZeroWhenNoDiskStore()
    {
        var config = new CacheConfiguration
        {
            DiskCacheEnabled = false,
            EvictionSweepIntervalSeconds = 3600,
        };

        using var cache = new InMemoryPageCache(
            Options.Create(config),
            NullLogger<InMemoryPageCache>.Instance,
            diskStore: null);

        var stats = cache.GetStats();
        stats.DiskCacheFileCount.Should().Be(0);
        stats.DiskCacheSizeBytes.Should().Be(0);
        stats.MaxDiskSizeBytes.Should().Be(0);
    }

    #endregion

    #region :cache command enhanced output

    [Fact]
    public async Task CacheCommand_NoArgs_ShowsEnhancedStats()
    {
        var (ctx, options, statusMessages) = CreateCommandContext();

        // Add a page to cache
        ctx.PageCache.Put("https://example.com/test",
            CreatePageLoadResult("https://example.com/test", "<html>test</html>"));

        await SearchCommandHandler.HandleCommandLineInput(ctx, "cache", options, CancellationToken.None);

        statusMessages.Should().HaveCount(1);
        var msg = statusMessages[0];
        msg.Should().Contain("Cache:");
        msg.Should().Contain("pages");
        msg.Should().Contain("hit rate");
        msg.Should().Contain("%"); // usage percentage
    }

    [Fact]
    public async Task CacheCommand_Info_ShowsDetailedStats()
    {
        var (ctx, options, statusMessages) = CreateCommandContext();

        await SearchCommandHandler.HandleCommandLineInput(ctx, "cache info", options, CancellationToken.None);

        statusMessages.Should().HaveCount(1);
        var msg = statusMessages[0];
        msg.Should().Contain("Memory:");
        msg.Should().Contain("Disk:");
        msg.Should().Contain("Articles:");
        msg.Should().Contain("Hit rate:");
        msg.Should().Contain("hits");
        msg.Should().Contain("misses");
    }

    [Fact]
    public async Task CacheCommand_Info_ShowsArticleCount()
    {
        var (ctx, options, statusMessages) = CreateCommandContext();

        // Mock preload service to return article URLs
        ctx.PreloadService.GetArticleCachedUrls().Returns(
            new HashSet<string> { "https://example.com/a1", "https://example.com/a2" });

        await SearchCommandHandler.HandleCommandLineInput(ctx, "cache info", options, CancellationToken.None);

        var msg = statusMessages[0];
        msg.Should().Contain("Articles: 2 cached");
    }

    [Fact]
    public async Task CacheCommand_NoArgs_IncludesArticleCountWhenNonZero()
    {
        var (ctx, options, statusMessages) = CreateCommandContext();

        ctx.PreloadService.GetArticleCachedUrls().Returns(
            new HashSet<string> { "https://example.com/a1" });

        await SearchCommandHandler.HandleCommandLineInput(ctx, "cache", options, CancellationToken.None);

        var msg = statusMessages[0];
        msg.Should().Contain("Articles: 1");
    }

    [Fact]
    public async Task CacheCommand_NoArgs_OmitsArticleSectionWhenZero()
    {
        var (ctx, options, statusMessages) = CreateCommandContext();

        ctx.PreloadService.GetArticleCachedUrls().Returns(
            new HashSet<string>());

        await SearchCommandHandler.HandleCommandLineInput(ctx, "cache", options, CancellationToken.None);

        var msg = statusMessages[0];
        msg.Should().NotContain("Articles:");
    }

    #endregion

    #region FormatBytes edge cases (tested via public formatted properties)

    [Fact]
    public void FormattedSize_ZeroBytes()
    {
        var stats = new CacheStats { TotalSizeBytes = 0 };
        stats.FormattedSize.Should().Be("0 B");
    }

    [Fact]
    public void FormattedSize_ExactlyOneKB()
    {
        var stats = new CacheStats { TotalSizeBytes = 1024 };
        stats.FormattedSize.Should().Be("1.0 KB");
    }

    [Fact]
    public void FormattedSize_ExactlyOneMB()
    {
        var stats = new CacheStats { TotalSizeBytes = 1024 * 1024 };
        stats.FormattedSize.Should().Be("1.0 MB");
    }

    [Fact]
    public void FormattedSize_JustUnderOneKB()
    {
        var stats = new CacheStats { TotalSizeBytes = 1023 };
        stats.FormattedSize.Should().Be("1023 B");
    }

    [Fact]
    public void FormattedMaxDiskSize_LargeValue()
    {
        var stats = new CacheStats { MaxDiskSizeBytes = 500L * 1024 * 1024 };
        stats.FormattedMaxDiskSize.Should().Be("500.0 MB");
    }

    #endregion

    #region Helpers

    private static PageLoadResult CreatePageLoadResult(string url, string html)
    {
        return PageLoadResult.Successful(url, html, new PageMetadata { Title = "Test" });
    }

    private static (PageLoadResult Result, CacheEntryMetadata Metadata) CreateEntry(string url, string html)
    {
        var result = PageLoadResult.Successful(url, html, new PageMetadata { Title = "Test" });
        var metadata = new CacheEntryMetadata
        {
            RequestUrl = url,
            FinalUrl = url,
            NormalizedUrl = UrlNormalizer.Normalize(url),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(html) + 1024,
        };

        return (result, metadata);
    }

    private static (CommandContext Ctx, RenderOptions Options, List<string> StatusMessages) CreateCommandContext()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        var navigationService = new NavigationService(logger);

        var page = Domain.Entities.Browser.Page.Create(
            "https://example.com",
            "<html><body>Test</body></html>",
            new PageMetadata { Title = "Test" });
        navigationService.NavigateTo(page);

        var statusMessages = new List<string>();
        var originalSetStatus = navigationService;

        var inputHandler = Substitute.For<IInputHandler>();
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var pageCache = Substitute.For<IPageCache>();
        pageCache.GetStats().Returns(new CacheStats
        {
            EntryCount = 5,
            TotalSizeBytes = 2 * 1024 * 1024,
            MaxSizeBytes = 100 * 1024 * 1024,
            HitCount = 10,
            MissCount = 3,
            DiskCacheFileCount = 8,
            DiskCacheSizeBytes = 15 * 1024 * 1024,
            MaxDiskSizeBytes = 500L * 1024 * 1024,
        });

        var preloadService = Substitute.For<IPreloadService>();
        preloadService.GetArticleCachedUrls().Returns(new HashSet<string>());

        var options = new RenderOptions
        {
            TerminalWidth = 120,
            TerminalHeight = 40,
            MaxContentWidth = 80
        };

        var ctx = new CommandContext
        {
            NavigationService = navigationService,
            Renderer = Substitute.For<IPageRenderer>(),
            InputHandler = inputHandler,
            ScopeFactory = Substitute.For<IServiceScopeFactory>(),
            Logger = Substitute.For<ILogger>(),
            PageCache = pageCache,
            LineCacheManager = new LineCacheManager(navigationService, themeProvider),
            ThemeProvider = themeProvider,
            PreloadService = preloadService,
            LayoutVariantProvider = Substitute.For<ILayoutVariantProvider>(),
            NavigateToAsync = (_, _, _) => Task.CompletedTask,
            ForceRefreshAsync = (_, _, _) => Task.CompletedTask,
            InteractiveRefreshAsync = (_, _, _) => Task.CompletedTask,
            OpenInteractiveBrowserAsync = (_, _, _) => Task.CompletedTask,
            RenderCurrentPageAsync = (_, _) =>
            {
                var msg = navigationService.CurrentContext.StatusMessage;
                if (msg != null)
                {
                    statusMessages.Add(msg);
                }

                return Task.CompletedTask;
            },
            RefreshCollectionsAsync = _ => Task.CompletedTask,
            RefreshBookmarksAsync = _ => Task.CompletedTask,
            GetCurrentRenderOptions = () => options,
            CreateCollectionService = _ => Substitute.For<Application.Interfaces.ICollectionService>(),
            GetReaderViewportHeight = _ => 20,
            GetHierarchicalViewportHeight = _ => 20,
            AdjustScrollForSelection = (_, _) => { },
            ScrollToSearchMatch = (_, _) => { },
        };

        return (ctx, options, statusMessages);
    }

    #endregion
}
