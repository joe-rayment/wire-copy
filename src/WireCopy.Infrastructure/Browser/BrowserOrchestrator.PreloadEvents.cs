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
    /// True when the preloader is doing (or has queued) work, i.e. the open
    /// detail panel should keep refreshing on the main-loop tick even though
    /// no ProgressChanged event has fired (workspace-v04i heartbeat).
    /// </summary>
    internal static bool ShouldHeartbeatRefresh(PreloadProgress progress)
    {
        return !string.IsNullOrEmpty(progress.CurrentlyFetchingUrl) || progress.UpcomingUrls.Count > 0;
    }

    /// <summary>
    /// Decides whether a progress change warrants a re-render: always when the
    /// prefetch detail panel is open (it overlays every view, workspace-v04i);
    /// otherwise only in the views whose status bar shows preload progress.
    /// </summary>
    internal static bool ShouldRefreshForProgress(bool panelVisible, ViewMode viewMode)
    {
        return panelVisible || viewMode is ViewMode.Hierarchical or ViewMode.CollectionItems;
    }

    /// <summary>
    /// Handles an animation timer tick. Performs a lightweight render update
    /// for just the animated region (e.g., status bar) without processing
    /// the full command pipeline. This keeps animation overhead minimal.
    /// </summary>
    private async Task HandleAnimationTickAsync(RenderOptions options, CancellationToken cancellationToken)
    {
        try
        {
            // Re-render only the current page — renderers can check AnimationState
            // to decide which regions need updating for the current frame.
            await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during animation tick render");
        }
    }

    /// <summary>
    /// Called on a background thread when the preload service caches a new page.
    /// Sets a dirty flag that the main input loop checks periodically.
    /// </summary>
    private void OnPreloadProgressChanged()
    {
        _progressDirty = true;

        try
        {
            var progress = _preloadService.GetProgress();
            var prevCount = _prevCachedCount;
            var prevComplete = _prevIsComplete;
            _prevCachedCount = progress.CachedCount;
            _prevIsComplete = progress.IsComplete;

            if (progress.TotalCacheableLinks <= 0)
            {
                return;
            }

            // workspace-wef6.2: cache-warm completion announces once as a
            // transient ("✓ all N cached") and then the bar stays silent —
            // the old "✓ cached" badge sat in the chrome forever post-warm.
            if (progress.IsComplete && !prevComplete && progress.CachedCount > 0)
            {
                _navigationService.SetStatusMessage($"✓ all {progress.CachedCount} cached");
            }

            // Skip animations if disabled or not in a view that shows cache progress
            if (_browserConfig.DisableAnimations)
            {
                return;
            }

            var viewMode = _navigationService.CurrentContext.ViewMode;
            if (viewMode != ViewMode.Hierarchical && viewMode != ViewMode.CollectionItems)
            {
                return;
            }

            var palette = Themes.BuiltInThemes.Get(_themeProvider.CurrentTheme);
            var width = Console.WindowWidth;

            // Item pulse: a new item was just cached
            if (progress.CachedCount > prevCount && prevCount >= 0)
            {
                // The count text is rendered near the right side of the status bar content line.
                // Use a rough estimate for column position; exact position varies with other badges.
                var col = Math.Max(0, width - 25);
                var row = Console.WindowHeight - 1;
                UI.Renderers.StatusBarRenderer.PlayCacheItemPulse(
                    palette, progress.CachedCount, progress.TotalCacheableLinks, col, row);
            }

            // Warm wave: cache warming just completed
            if (progress.IsComplete && !prevComplete && progress.CachedCount > 0)
            {
                UI.Renderers.StatusBarRenderer.PlayCacheWarmWave(palette, width);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error playing cache animation");
        }
    }

    /// <summary>
    /// Checks whether preload progress has changed and enough time has elapsed
    /// since the last progress-driven render (debounce to at most once per second).
    /// If so, re-renders the current page to update the status bar.
    /// </summary>
    private async Task CheckAndRenderProgressAsync(CancellationToken cancellationToken)
    {
        var panelVisible = _commandContext.IsPreloadDetailVisible;

        // workspace-v04i heartbeat: while the detail panel is open and the
        // preloader is busy, refresh even without a ProgressChanged event so
        // ElapsedOnCurrent visibly ticks and the 8s/30s stall states can
        // appear for a wedged fetch that emits no events. A fully idle
        // preloader still skips the render so an open panel costs nothing.
        if (!_progressDirty && (!panelVisible || !ShouldHeartbeatRefresh(_preloadService.GetProgress())))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastProgressRender).TotalMilliseconds < 1000)
        {
            return;
        }

        _progressDirty = false;
        _lastProgressRender = now;

        // Refresh wherever the panel is open (it overlays any view); otherwise
        // only for views that display preload progress in the status bar.
        var viewMode = _navigationService.CurrentContext.ViewMode;
        if (!ShouldRefreshForProgress(panelVisible, viewMode))
        {
            return;
        }

        try
        {
            // Re-read options to get fresh CacheProgress
            var freshOptions = GetCurrentRenderOptions();
            await RenderCurrentPageAsync(freshOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error rendering progress update");
        }
    }

    /// <summary>
    /// Notifies the pre-load service of the current selection, adapting to the active view mode.
    /// </summary>
    private void NotifyPreloadSelectionChanged()
    {
        var viewMode = _navigationService.CurrentContext.ViewMode;

        switch (viewMode)
        {
            case ViewMode.CollectionItems:
            {
                var collection = _navigationService.ActiveCollection;
                if (collection == null || collection.Items.Count == 0)
                {
                    return;
                }

                var urls = collection.Items.Select(item => item.Url).ToList();
                _preloadService.NotifyCollectionChanged(
                    _navigationService.CollectionItemSelectedIndex,
                    urls);
                break;
            }

            case ViewMode.Hierarchical:
            {
                // When viewing an article opened from a collection, let the collection queue continue
                if (_navigationService.HasCollectionReturnPoint)
                {
                    return;
                }

                var page = _navigationService.CurrentPage;
                var tree = page?.LinkTree;
                if (tree == null)
                {
                    return;
                }

                var allNodes = tree.GetAllNodes().ToList();
                var selectedNode = tree.CurrentSelection;
                var selectedIndex = selectedNode != null ? allNodes.IndexOf(selectedNode) : 0;

                _preloadService.NotifySelectionChanged(
                    Math.Max(0, selectedIndex),
                    allNodes,
                    page!.Url);
                break;
            }

            case ViewMode.Readable:
            {
                // When reading an article opened from a collection, let the collection queue continue
                if (_navigationService.HasCollectionReturnPoint)
                {
                    return;
                }

                break;
            }

            case ViewMode.Launcher:
            case ViewMode.CollectionList:
                _preloadService.ClearQueue();
                break;
        }
    }
}
