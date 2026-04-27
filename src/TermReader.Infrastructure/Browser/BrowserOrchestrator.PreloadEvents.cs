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
        if (!_progressDirty)
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

        // Only refresh for views that display preload progress
        var viewMode = _navigationService.CurrentContext.ViewMode;
        if (viewMode != ViewMode.Hierarchical && viewMode != ViewMode.CollectionItems)
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
