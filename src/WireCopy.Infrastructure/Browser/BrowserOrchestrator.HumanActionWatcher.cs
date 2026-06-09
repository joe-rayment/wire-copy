// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Captcha / login auto-resume (workspace-iv9g).
///
/// <para>
/// When the page-load pipeline surfaces a <see cref="HumanActionRequiredException"/>
/// (typically a CAPTCHA on NYTimes / WSJ / a Cloudflare-fronted site), the
/// background-load catch site in
/// <see cref="BrowserOrchestrator.CompleteBackgroundLoadAsync"/> renders the
/// variant-aware reader-view box AND starts the watcher in this file. The
/// watcher polls the headed browser page every <see cref="PollIntervalMs"/>
/// for up to <see cref="MaxWatchDuration"/>; when the content arrives
/// (HumanActionDetector returns null OR the URL host changed), the watcher
/// re-runs the load synchronously and the main-loop race picks up the
/// result via <see cref="BrowserOrchestrator.CompleteHumanActionWatcherAsync"/>.
/// </para>
///
/// <para>
/// Replaces the prior UX where the user had to back out to the link list
/// and re-click the article after solving the gate — workspace-m7nc cleared
/// the sticky badge after a navigation but never auto-loaded the content
/// for the in-progress URL.
/// </para>
/// </summary>
public partial class BrowserOrchestrator
{
    private const int PollIntervalMs = 1500;
    private static readonly TimeSpan MaxWatchDuration = TimeSpan.FromMinutes(3);

    private static bool IsWatchableVariant(HumanActionVariant variant) => variant switch
    {
        HumanActionVariant.Captcha => true,
        HumanActionVariant.Login => true,
        HumanActionVariant.CookieConsent => true,
        HumanActionVariant.TwoFactor => true,
        _ => false,
    };

    private static bool IsResolved(
        string currentUrl,
        string currentHtml,
        string? originalHost)
    {
        // URL host changed — the user navigated away from the gate page,
        // typically into the destination article. Treat as resolved.
        var currentHost = TryExtractHost(currentUrl);
        if (originalHost != null && currentHost != null
            && !string.Equals(originalHost, currentHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Current page no longer matches any HITL signature.
        var detected = HumanActionDetector.Detect(currentHtml, currentUrl, statusCode: 0);
        if (detected == null)
        {
            return true;
        }

        // Different variant fired (e.g. captcha cleared but now a cookie
        // banner is up) — keep waiting. Same variant = not yet resolved.
        return false;
    }

    private static string? TryExtractHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// <summary>
    /// Starts the background watcher when the HITL exception's variant is
    /// one a user can plausibly clear in their headed browser
    /// (CAPTCHA / Login / CookieConsent / TwoFactor). No-op for variants
    /// that don't resolve via headed interaction (Paywall, RegionBlock,
    /// RedirectLoop, Generic) and for headless runs where there is no visible
    /// window. (A RedirectLoop throws before any pollable page exists and a
    /// Cloudflare bounce won't settle on its own — the user must open it in
    /// their real browser and press R.)
    /// </summary>
    private void StartHumanActionWatcher(
        string url,
        Application.DTOs.Browser.HumanActionRequired action,
        CancellationToken cancellationToken)
    {
        if (_browserConfig.Headless)
        {
            _logger.LogDebug("Skipping human-action watcher: headless mode, no visible window to poll");
            return;
        }

        if (!IsWatchableVariant(action.Variant))
        {
            _logger.LogDebug(
                "Skipping human-action watcher: variant {Variant} cannot resolve via headed browser",
                action.Variant);
            return;
        }

        if (_browserSession is not IBrowserSession session || !session.IsBrowserAvailable)
        {
            _logger.LogDebug("Skipping human-action watcher: browser session unavailable");
            return;
        }

        // Cancel any previous watcher (e.g. user clicked through a chain of
        // gated pages — only the latest should be live).
        CancelHumanActionWatcher();

        _humanActionWatcherUrl = url;
        _humanActionWatcherCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _humanActionWatcherCts.Token;

        _humanActionWatcher = Task.Run(
            () => WatchAndReloadAsync(url, action, token),
            token);
    }

    /// <summary>
    /// Polls the headed browser page until the gate clears, then re-runs the
    /// load via <see cref="_pageLoader"/> and returns the fresh result. Returns
    /// <see langword="null"/> on timeout, cancellation, or unresolved errors —
    /// the user is left looking at the HITL reader-view box and can still
    /// press Shift+R manually.
    /// </summary>
    private async Task<PageLoadResult?> WatchAndReloadAsync(
        string url,
        Application.DTOs.Browser.HumanActionRequired action,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + MaxWatchDuration;
        var originalHost = TryExtractHost(url);

        try
        {
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);

                if (_browserSession is not IBrowserSession session || !session.IsBrowserAvailable)
                {
                    _logger.LogDebug("Human-action watcher: browser session went away, stopping");
                    return null;
                }

                string currentHtml;
                string currentUrl;
                try
                {
                    var page = await session.GetOrCreatePageAsync(headless: false).ConfigureAwait(false);
                    currentUrl = page.Url;
                    currentHtml = await page.ContentAsync().ConfigureAwait(false);
                }
                catch (Microsoft.Playwright.PlaywrightException ex) when (
                    PageLoader.LooksLikeStalePlaywrightPage(ex.Message))
                {
                    // The page just navigated out from under us — this is the
                    // classic "captcha solved" signal. The next iteration
                    // would see a fresh page anyway; jump straight to reload.
                    _logger.LogInformation(
                        "Human-action watcher: stale page exception suggests gate resolved for {Url}",
                        url);
                    return await RetryLoadAsync(url, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Human-action watcher: poll error, will retry");
                    continue;
                }

                if (IsResolved(currentUrl, currentHtml, originalHost))
                {
                    _logger.LogInformation(
                        "Human-action watcher: gate resolved for {Url} (variant {Variant})",
                        url,
                        action.Variant);
                    return await RetryLoadAsync(url, cancellationToken).ConfigureAwait(false);
                }
            }

            _logger.LogDebug(
                "Human-action watcher: timed out without resolution for {Url}",
                url);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<PageLoadResult?> RetryLoadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            _pageCache.Remove(url);
            var result = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = false, ForceRefresh = true },
                cancellationToken).ConfigureAwait(false);
            return result.Success ? result : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Human-action watcher: retry load failed for {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Called from the main loop's WhenAny race when the watcher completes.
    /// On success: rebuilds the page through the pipeline, replaces the
    /// skeleton/HITL view, and renders. On null: clears watcher state and
    /// leaves the HITL box visible — the user can still press Shift+R.
    /// </summary>
    private async Task CompleteHumanActionWatcherAsync(
        RenderOptions options,
        CancellationToken cancellationToken)
    {
        var watcher = _humanActionWatcher;
        var url = _humanActionWatcherUrl;

        _humanActionWatcher = null;
        _humanActionWatcherUrl = null;
        try
        {
            _humanActionWatcherCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        _humanActionWatcherCts = null;

        if (watcher == null || url == null)
        {
            return;
        }

        PageLoadResult? result;
        try
        {
            result = await watcher.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Human-action watcher faulted for {Url}", url);
            return;
        }

        if (result == null || !result.Success)
        {
            return;
        }

        // User may have navigated elsewhere while we were watching — only
        // apply the result if they're still on this URL.
        if (_navigationService.CurrentPage?.Url != url)
        {
            _logger.LogDebug(
                "Human-action watcher: resolved but user navigated away from {Url}",
                url);
            return;
        }

        try
        {
            var (page, _) = await _pipeline.BuildPageAsync(result, url, cancellationToken).ConfigureAwait(false);
            _navigationService.ReplaceCurrent(page);

            // Auto-switch to reader view for articles (mirrors
            // CompleteBackgroundLoadAsync).
            if (page.Classification == Domain.Enums.Browser.PageClassification.Article && page.HasReadableContent())
            {
                _navigationService.SetViewMode(ViewMode.Readable);
            }

            _navigationService.SetCacheInfo(false, null);
            _lineCacheManager.InvalidateLineCache();

            _preloadService.NotifyPageLoaded(page);
            NotifyPreloadSelectionChanged();

            await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
            PlayDecryptRevealAnimation(page);

            // workspace-exbz: gate-resolution loads complete here — engage the sidecar
            // (usually a no-op: solving the gate means a headed window already exists).
            await EnsureSidecarEngagedAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Human-action watcher: auto-loaded resolved content for {Url}",
                url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Human-action watcher: build/render failed after resolution for {Url}",
                url);
        }
    }

    /// <summary>
    /// Cancels and clears any active watcher. Called when the user navigates
    /// away or triggers a manual refresh that supersedes the watcher.
    /// </summary>
    private void CancelHumanActionWatcher()
    {
        if (_humanActionWatcher == null)
        {
            return;
        }

        try
        {
            _humanActionWatcherCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        _humanActionWatcher = null;
        _humanActionWatcherUrl = null;

        try
        {
            _humanActionWatcherCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        _humanActionWatcherCts = null;
    }
}
