// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;

namespace WireCopy.Infrastructure.Browser;

public partial class BrowserOrchestrator
{
    /// <summary>
    /// Creates render options from current terminal dimensions.
    /// </summary>
    private RenderOptions GetCurrentRenderOptions()
    {
        var width = Console.WindowWidth;
        var height = Console.WindowHeight;

        // workspace-vzmr: the app renders to its REAL terminal size, always. The old
        // overlap model (browser docked over the terminal, app squeezed into the
        // uncovered columns) is gone — the user owns window placement; the dock only
        // positions the slim browser window beside the terminal. The flag still feeds
        // the status bar's docked affordance; it no longer affects layout.
        var browserDocked = (_browserSession as IBrowserSession)?.IsWindowDocked ?? false;
        var (contentLeftOffset, renderWidth) = (0, width);

        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        var use256 = string.Equals(colorTerm, "truecolor", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(colorTerm, "24bit", StringComparison.OrdinalIgnoreCase)
                  || !string.IsNullOrEmpty(colorTerm);

        // Launcher chrome is rendered by LauncherRenderer (header + URL bar +
        // bookmark grid + footer) and does NOT consume cache/podcast/paywall
        // /layout-label fields. Computing them here is wasted work — and
        // GetMergedCachedUrls + GetMissingPaywalledCookieDomains are
        // genuinely expensive (set unions, async-cached lookups). Short-circuit
        // when on the launcher so those fields stay null/empty/zero.
        // CurrentContext is a COMPUTED snapshot (announcement TTL check +
        // activity lock + sort) — materialize it once per options build
        // instead of once per consumed field.
        var navContext = _navigationService.CurrentContext;
        var isLauncher = navContext.ViewMode == ViewMode.Launcher;

        // Surface the typed human-action signal raised by the preload service so
        // status-bar / reader-view consumers can render variant-aware copy
        // (workspace-0b9s). When non-null, the status bar replaces the legacy
        // 🍪✗ cookie badge with "⏸ {verb} at {domain} · |:open".
        // workspace-c8v3: also populate CacheProgress on the launcher when the
        // user has explicitly toggled the prefetch detail overlay on, so the
        // panel has something to render. Otherwise keep the launcher fast.
        var needPreloadProgress = !isLauncher || _commandContext.IsPreloadDetailVisible;
        var preloadProgress = needPreloadProgress ? _preloadService.GetProgress() : null;
        var requiredAction = preloadProgress?.BlockedAction;

        return new RenderOptions
        {
            TerminalWidth = renderWidth,
            TerminalHeight = height,
            MaxContentWidth = ComputeContentWidth(renderWidth),
            Use256Colors = use256,
            CachedUrls = isLauncher ? null : GetMergedCachedUrls(),
            CacheProgress = preloadProgress,
            StatusMessage = navContext.StatusMessage,
            PodcastButtonState = isLauncher ? 0 : GetPodcastButtonState(),
            PodcastProgressFraction = isLauncher ? 0 : _commandContext.PodcastGenerationProgress,
            PodcastArticleCount = isLauncher ? 0 : GetPodcastArticleCount(),
            PodcastFeedUrl = isLauncher ? null : _commandContext.PodcastFeedUrl,
            CacheUsagePercent = isLauncher ? 0 : GetCacheUsagePercent(),
            LayoutVariantLabel = isLauncher ? null : GetLayoutVariantLabel(),
            LayoutVariant = _layoutVariantProvider.GetCurrentVariant(navContext.ViewMode),
            MissingCookieDomains = isLauncher
                ? null
                : _preloadService.GetMissingPaywalledCookieDomains(
                    navContext.CurrentPage?.Url),
            ShowSetupHint = HasIncompleteSetup(),
            ScheduledRunBadge = isLauncher ? GetScheduledRunBadge() : null,
            ReadingListItemCount = isLauncher ? GetReadingListItemCount() : null,
            RequiredAction = requiredAction,
            ShowPreloadDetail = _commandContext.IsPreloadDetailVisible,
            BrowserDocked = browserDocked,
            ContentLeftOffset = contentLeftOffset,
        };
    }

    /// <summary>
    /// Returns the live non-expired saved-item count for the Reading List, used
    /// by the launcher tile's subtitle (workspace-fbcn). Counts only items within
    /// the same TTL the purge uses (<see cref="ReadingListExpiryHours"/>) so the
    /// subtitle never shows a stale total for items that have since expired
    /// (workspace-hlzy). Backed by a single COUNT query — no row materialization —
    /// so the sync wait over SQLite is sub-millisecond. On failure (DB unavailable
    /// etc.) returns 0 so the empty-state copy renders rather than letting the
    /// launcher fail to render.
    /// </summary>
    private int GetReadingListItemCount()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var collections = scope.ServiceProvider.GetRequiredService<ICollectionService>();
            return collections
                .CountReadingListItemsAsync(TimeSpan.FromHours(ReadingListExpiryHours))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read Reading List item count for launcher subtitle");
            return 0;
        }
    }

    /// <summary>
    /// workspace-frpl.13 (B11) — the launcher badge for unacknowledged scheduled-run
    /// failures/recoveries (null when nothing needs attention). Read on each launcher
    /// frame so a run that finished while the user was away surfaces on next focus.
    /// </summary>
    private string? GetScheduledRunBadge()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider
                .GetService<Application.Interfaces.Scheduling.IScheduledRunRepository>();
            if (repo is null)
            {
                return null;
            }

            var unacked = repo.GetUnacknowledgedFinishedRunsAsync().GetAwaiter().GetResult();
            return Scheduling.ScheduledRunBadge.Describe(unacked);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read scheduled-run badge for launcher");
            return null;
        }
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
