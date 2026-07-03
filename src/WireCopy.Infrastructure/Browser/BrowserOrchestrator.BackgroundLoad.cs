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
    /// Commands the main loop still honours while a background page load is
    /// painting the skeleton screen. workspace-v04i: TogglePreloadDetail is
    /// allowed so the user can open the prefetch panel at exactly the moment
    /// they're wondering whether the load has stalled — the overlay paints
    /// over the skeleton via the normal render path.
    /// </summary>
    internal static bool IsCommandAllowedDuringBackgroundLoad(CommandType type)
    {
        return type is CommandType.Quit
            or CommandType.GoBack
            or CommandType.NoOp
            or CommandType.TerminalResized
            or CommandType.AnimationTick
            or CommandType.TogglePreloadDetail;
    }

    /// <summary>
    /// Starts loading a page in the background. The main loop will pick up the result
    /// via _backgroundPageLoad in its WhenAny race.
    /// </summary>
    private void StartBackgroundLoad(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        _backgroundLoadUrl = url;
        _backgroundLoadOptions = options;
        _backgroundLoadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _backgroundLoadCts.Token;

        _backgroundPageLoad = Task.Run(
            async () =>
            {
                try
                {
                    return await LoadPageAsync(url, token).ConfigureAwait(false);
                }
                catch
                {
                    _preloadService.Resume();
                    throw;
                }
            },
            token);
    }

    /// <summary>
    /// Cancels any in-progress background page load.
    /// </summary>
    private void CancelBackgroundLoad()
    {
        if (_backgroundPageLoad == null)
        {
            return;
        }

        try
        {
            _backgroundLoadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        _backgroundPageLoad = null;
        _backgroundLoadUrl = null;
        _backgroundLoadOptions = null;

        try
        {
            _backgroundLoadCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        _backgroundLoadCts = null;
        _navigationService.ClearActivity("load");
        _preloadService.Resume();
    }

    /// <summary>
    /// Called from the main loop when background page load completes.
    /// Replaces the skeleton page with the real page.
    /// </summary>
    private async Task CompleteBackgroundLoadAsync(RenderOptions options, CancellationToken cancellationToken)
    {
        var loadTask = _backgroundPageLoad;
        var url = _backgroundLoadUrl;
        var loadOptions = _backgroundLoadOptions ?? options;

        _backgroundPageLoad = null;
        _backgroundLoadUrl = null;
        _backgroundLoadOptions = null;

        try
        {
            _backgroundLoadCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        _backgroundLoadCts = null;

        if (loadTask == null || url == null)
        {
            return;
        }

        try
        {
            var page = await loadTask.ConfigureAwait(false);

            // Only replace if we're still on the skeleton for this URL
            if (_navigationService.CurrentPage?.Url != url)
            {
                _logger.LogDebug("Background load completed but user navigated away from {Url}", url);
                _preloadService.Resume();
                return;
            }

            // Use ReplaceCurrent to avoid pushing skeleton onto back history
            _navigationService.ReplaceCurrent(page);

            // Auto-switch to reader view for article pages with readable content
            if (page.Classification == PageClassification.Article && page.HasReadableContent())
            {
                _navigationService.SetViewMode(ViewMode.Readable);
            }

            var isFromCache = _lastLoadFetchMethod == FetchMethod.Cached;
            var cachedAt = isFromCache ? _pageCache.GetCachedAt(url) : null;
            _navigationService.SetCacheInfo(isFromCache, cachedAt);
            _lineCacheManager.InvalidateLineCache();

            _preloadService.NotifyPageLoaded(page);
            NotifyPreloadSelectionChanged();

            await RenderCurrentPageAsync(loadOptions, cancellationToken).ConfigureAwait(false);

            PlayDecryptRevealAnimation(page);

            // workspace-exbz: background loads (full cache miss → skeleton → load)
            // complete here, not in CompleteNavigation — engage the sidecar too.
            await EnsureSidecarEngagedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Background page load was cancelled for {Url}", url);
        }
        catch (HumanActionRequiredException hitl)
        {
            _logger.LogWarning(
                "Background page load blocked on human action ({Variant} at {Domain}) for {Url}",
                hitl.RequiredAction.Variant,
                hitl.RequiredAction.Domain,
                url);

            // If still on skeleton, render the variant-aware HITL box so the user
            // knows exactly what to do (solve CAPTCHA / log in / accept cookies / …)
            // instead of the generic "Something went wrong" error.
            if (_navigationService.CurrentPage?.Url == url)
            {
                _renderer.RenderHumanAction(hitl.RequiredAction, url);

                // workspace-iv9g: start the headed-browser watcher so the app
                // auto-loads content as soon as the user solves the gate in
                // their browser window. No-op in headless mode and for
                // variants that can't resolve via headed interaction
                // (Paywall, RegionBlock, Generic).
                StartHumanActionWatcher(url, hitl.RequiredAction, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background page load failed for {Url}", url);

            // If still on skeleton, show error
            if (_navigationService.CurrentPage?.Url == url)
            {
                _renderer.RenderError(ex.Message, url);
            }
        }
        finally
        {
            _navigationService.ClearActivity("load");
            _preloadService.Resume();
        }
    }

    private bool HasActiveBackgroundLoad()
    {
        return _backgroundPageLoad != null && !_backgroundPageLoad.IsCompleted;
    }

    /// <summary>
    /// Called from the main loop when background quality retry completes.
    /// Replaces the current page with the improved version if it has better content.
    /// </summary>
    private async Task CompleteQualityRetryAsync(RenderOptions options, CancellationToken cancellationToken)
    {
        var retryTask = _qualityRetryTask;
        var url = _qualityRetryUrl;

        _qualityRetryTask = null;
        _qualityRetryUrl = null;

        if (retryTask == null || url == null)
        {
            return;
        }

        try
        {
            var result = await retryTask.ConfigureAwait(false);

            // Only replace if we're still on the same URL
            if (_navigationService.CurrentPage?.Url != url)
            {
                _logger.LogDebug("Quality retry completed but user navigated away from {Url}", url);
                return;
            }

            var currentPage = _navigationService.CurrentPage;
            var improvedPage = result.Page;

            // Only replace if the improved page is actually better
            var currentWordCount = currentPage?.ReadableContent?.WordCount ?? 0;
            var improvedWordCount = improvedPage.ReadableContent?.WordCount ?? 0;
            var currentPaywalled = currentPage?.ReadableContent?.IsPaywalled ?? false;
            var improvedPaywalled = improvedPage.ReadableContent?.IsPaywalled ?? false;

            if (improvedWordCount > currentWordCount || (currentPaywalled && !improvedPaywalled))
            {
                _logger.LogInformation(
                    "Quality retry improved page: {Url} (words: {Old} → {New}, paywall: {OldPw} → {NewPw})",
                    url,
                    currentWordCount,
                    improvedWordCount,
                    currentPaywalled,
                    improvedPaywalled);

                _navigationService.ReplaceCurrent(improvedPage);
                _lastLoadFetchMethod = result.FetchMethod;

                // Auto-switch to reader view if article now has readable content
                if (improvedPage.Classification == PageClassification.Article && improvedPage.HasReadableContent())
                {
                    _navigationService.SetViewMode(ViewMode.Readable);
                }

                _lineCacheManager.InvalidateLineCache();

                // workspace-u45c: the quality retry silently swapped the page
                // under the user — announce it so the sudden content change
                // reads as an upgrade, not a glitch.
                _navigationService.Announce("⟳", "Found better content", shortText: "⟳ improved");

                await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);

                // Parity with the background-load completion path: reveal the
                // improved content with the same animation.
                PlayDecryptRevealAnimation(improvedPage);
            }
            else
            {
                _logger.LogDebug(
                    "Quality retry did not improve page: {Url} (words: {Old} → {New})",
                    url,
                    currentWordCount,
                    improvedWordCount);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Quality retry was cancelled for {Url}", url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quality retry failed for {Url}", url);
        }
    }
}
