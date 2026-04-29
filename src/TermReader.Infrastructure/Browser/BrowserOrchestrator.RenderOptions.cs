// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.CommandHandlers;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI;

namespace TermReader.Infrastructure.Browser;

public partial class BrowserOrchestrator
{
    /// <summary>
    /// Creates render options from current terminal dimensions.
    /// </summary>
    private RenderOptions GetCurrentRenderOptions()
    {
        var width = Console.WindowWidth;
        var height = Console.WindowHeight;
        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        var use256 = string.Equals(colorTerm, "truecolor", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(colorTerm, "24bit", StringComparison.OrdinalIgnoreCase)
                  || !string.IsNullOrEmpty(colorTerm);
        return new RenderOptions
        {
            TerminalWidth = width,
            TerminalHeight = height,
            MaxContentWidth = ComputeContentWidth(width),
            Use256Colors = use256,
            CachedUrls = GetMergedCachedUrls(),
            CacheProgress = _preloadService.GetProgress(),
            PodcastButtonState = GetPodcastButtonState(),
            PodcastProgressFraction = _commandContext.PodcastGenerationProgress,
            PodcastArticleCount = GetPodcastArticleCount(),
            CacheUsagePercent = GetCacheUsagePercent(),
            LayoutVariantLabel = GetLayoutVariantLabel(),
            LayoutVariant = _layoutVariantProvider.GetCurrentVariant(_navigationService.CurrentContext.ViewMode),
        };
    }

    private int ComputeContentWidth(int terminalWidth)
    {
        var min = Math.Min(MinContentWidth, terminalWidth - 2);

        if (_commandContext.ContentWidthOverride.HasValue)
        {
            return Math.Clamp(
                Math.Min(_commandContext.ContentWidthOverride.Value, terminalWidth - 2),
                min,
                MaxContentWidth);
        }

        // In reader view, use the layout variant to determine content width
        var isReaderView = _navigationService.CurrentContext.ViewMode == ViewMode.Readable;
        if (isReaderView)
        {
            var variant = _layoutVariantProvider.GetCurrentVariant(ViewMode.Readable);
            var readerWidth = variant switch
            {
                "FullWidth" => terminalWidth - 2,
                "Narrow" => Math.Min(50, terminalWidth - 2),
                _ => Math.Min(60, terminalWidth - 2), // Comfortable (default) — narrow for tighter, more readable lines
            };
            return Math.Clamp(readerWidth, min, MaxContentWidth);
        }

        return Math.Clamp(terminalWidth - 2, min, MaxContentWidth);
    }

    private bool IsTtsConfigured()
    {
        if (!_ttsServiceResolved)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                _ttsService = scope.ServiceProvider.GetService<ITtsService>();
            }
            catch
            {
                // Podcast services may not be registered
            }

            _ttsServiceResolved = true;
        }

        return _ttsService?.IsConfigured ?? false;
    }

    /// <summary>
    /// Returns the union of page-cached URLs and article-cached URLs from preloading.
    /// Used by renderers to show cache indicators for collection items.
    /// </summary>
    private double GetCacheUsagePercent()
    {
        var stats = _pageCache.GetStats();
        return stats?.UsagePercent ?? 0;
    }

    private string? GetLayoutVariantLabel()
    {
        var mode = _navigationService.CurrentContext.ViewMode;
        var total = _layoutVariantProvider.GetTotalVariants(mode);
        if (total <= 1)
        {
            return null;
        }

        var variant = _layoutVariantProvider.GetCurrentVariant(mode);
        var index = _layoutVariantProvider.GetCurrentIndex(mode) + 1; // 1-based
        return $"{variant} {index}/{total}";
    }

    private IReadOnlySet<string> GetMergedCachedUrls()
    {
        var pageCachedUrls = _pageCache.GetCachedUrls();
        var articleCachedUrls = _preloadService.GetArticleCachedUrls();

        if (articleCachedUrls.Count == 0)
        {
            return pageCachedUrls;
        }

        var merged = new HashSet<string>(pageCachedUrls);
        merged.UnionWith(articleCachedUrls);
        return merged;
    }

    /// <summary>
    /// Returns the podcast button state integer for RenderOptions.
    /// 0=Idle, 2=Disabled, 3=Unconfigured, 4=Selected, 5=Generating.
    /// </summary>
    private int GetPodcastButtonState()
    {
        // Generating state takes priority — active podcast generation in progress
        if (_commandContext.IsPodcastGenerating)
        {
            return 5; // Generating
        }

        // Empty collections → Disabled (dimmed/inactive button)
        if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems
            && _navigationService.ActiveCollection is { } col
            && col.Items.Count == 0)
        {
            return 2; // Disabled
        }

        if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems
            && _navigationService.CollectionItemSelectedIndex == -1)
        {
            return 4; // Selected
        }

        return IsTtsConfigured() ? 0 : 3; // Idle or Unconfigured
    }

    /// <summary>
    /// Returns the number of articles in the active collection for podcast CTA metadata display.
    /// </summary>
    private int GetPodcastArticleCount()
    {
        if (_navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems
            && _navigationService.ActiveCollection is { } col)
        {
            return col.Items.Count;
        }

        return 0;
    }
}
