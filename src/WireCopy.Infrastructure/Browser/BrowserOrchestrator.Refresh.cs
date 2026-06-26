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
    private async Task ForceRefreshAsync(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        _preloadService.Pause();

        try
        {
            _renderer.RenderLoading(url);

            var loadResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = _browserConfig.EffectiveHeadless, ForceRefresh = true },
                cancellationToken).ConfigureAwait(false);

            if (!loadResult.Success)
            {
                throw new InvalidOperationException($"Failed to load page: {loadResult.ErrorMessage}");
            }

            var (page, _) = await _pipeline.BuildPageAsync(loadResult, url, cancellationToken).ConfigureAwait(false);

            _navigationService.ReplaceCurrent(page);
            _navigationService.SetCacheInfo(false, null);
            _lineCacheManager.InvalidateLineCache();

            _preloadService.NotifyPageLoaded(page);
            NotifyPreloadSelectionChanged();

            // workspace-wef6.4: the loading screen covers the fetch itself,
            // so announce the outcome once the fresh page paints.
            _navigationService.Announce("✓", "Refreshed — cache bypassed", shortText: "✓ refreshed");

            await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error force-refreshing {Url}", url);
            _renderer.RenderError(ex.Message, url);
        }
        finally
        {
            _preloadService.Resume();
        }
    }

    /// <summary>
    /// workspace-kdda: navigates the app's headed browser to <paramref name="url"/>
    /// so the user can clear an active CAPTCHA / login gate that the preload
    /// service is reporting on the link list. Unlike
    /// <see cref="InteractiveRefreshAsync"/>, the current TUI page is NOT
    /// replaced — the user stays on the link list so they can continue
    /// browsing after the gate is solved.
    ///
    /// <para>
    /// System-browser <c>Process.Start</c> would land the user in a separate
    /// browser context with no shared session/cookies; solving the gate
    /// there has no effect on the in-app preloader, which is why the user
    /// previously saw "blank page when I go to the browser" and had to drill
    /// into an article to trigger a headed load.
    /// </para>
    ///
    /// <para>
    /// On resolution the headed cookies are saved + synced to the HTTP cookie
    /// jar so pending preloads pick up the post-gate session, and the
    /// preload service is notified to drop its sticky BlockedAction verdict.
    /// </para>
    /// </summary>
    private async Task OpenInteractiveBrowserAsync(
        string url,
        RenderOptions options,
        CancellationToken cancellationToken)
    {
        _preloadService.Pause();

        try
        {
            _renderer.RenderLoading(url);

            var loadResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = false, ForceRefresh = true, ForceBrowser = true },
                cancellationToken).ConfigureAwait(false);

            if (_browserSession is IBrowserSession restoreSession)
            {
                await restoreSession.RestoreWindowAsync().ConfigureAwait(false);
            }

            // Run the same bot-challenge poll the interactive refresh uses.
            // Returns null when the load didn't surface a challenge OR when
            // the poll timed out — both are "no auto-clear" cases.
            var challengeResult = await _pipeline.HandleBotChallengeIfNeededAsync(
                url,
                loadResult,
                cancellationToken,
                headlessOverride: false).ConfigureAwait(false);

            // Carry headed-browser cookies to the HTTP cookie jar so the
            // preloader picks up the post-gate session. Best-effort.
            await SaveBrowserCookiesAsync(cancellationToken).ConfigureAwait(false);

            // Drop the sticky BlockedAction verdict when we have positive
            // evidence the gate is cleared: either the bot-challenge poll
            // confirmed resolution OR the initial headed load succeeded
            // outright without a HITL signal.
            var resolved = challengeResult != null
                || (loadResult.Success && loadResult.RequiredAction == null);
            if (resolved)
            {
                _preloadService.NotifyChallengeResolved(url);
            }

            if (_browserSession is IBrowserSession minSession)
            {
                await minSession.MinimizeWindowAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening interactive browser for {Url}", url);
        }
        finally
        {
            _preloadService.Resume();
            await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InteractiveRefreshAsync(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        _preloadService.Pause();

        try
        {
            _renderer.RenderLoading(url);

            // Force headed browser for interactive refresh — skip HTTP, go straight to browser
            // so the user gets a visible window they can interact with (login, captcha, etc.)
            var loadResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = false, ForceRefresh = true, ForceBrowser = true },
                cancellationToken).ConfigureAwait(false);

            if (!loadResult.Success)
            {
                _renderer.RenderError($"Failed to load page: {loadResult.ErrorMessage}", url);
                return;
            }

            // Restore the browser window AFTER the headed browser is created and page loaded
            if (_browserSession is IBrowserSession browserSession)
            {
                await browserSession.RestoreWindowAsync().ConfigureAwait(false);
            }

            // If bot challenge detected, use the challenge polling helper (force headed)
            var challengeResult = await _pipeline.HandleBotChallengeIfNeededAsync(url, loadResult, cancellationToken, headlessOverride: false).ConfigureAwait(false);
            if (challengeResult != null)
            {
                loadResult = challengeResult;
            }

            // Show prompt and wait for user to accept or cancel. workspace-lmwm:
            // pass the surviving detection verdict (null when the headed load
            // succeeded with no gate, or after the poll auto-resolved one) so
            // the screen never claims a captcha exists on a healthy page.
            // workspace-u5vu: on a link-list page outside preview mode, Shift+I
            // is often a reach for the layout wizard — point at Ctrl+L.
            var layoutSetupHint = !_navigationService.IsInPreviewMode
                && _navigationService.CurrentContext.ViewMode == ViewMode.Hierarchical
                && _navigationService.CurrentPage?.Classification == PageClassification.LinkList;
            _renderer.RenderInteractiveRefresh(url, loadResult.RequiredAction, layoutSetupHint);

            var input = await _inputHandler.WaitForInputAsync(cancellationToken).ConfigureAwait(false);
            if (input.Type == CommandType.GoBack)
            {
                // User pressed Esc — cancel, minimize browser
                if (_browserSession is IBrowserSession cancelSession)
                {
                    await cancelSession.MinimizeWindowAsync().ConfigureAwait(false);
                }

                await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Minimize browser now that interaction is complete
            if (_browserSession is IBrowserSession interactiveSession)
            {
                await interactiveSession.MinimizeWindowAsync().ConfigureAwait(false);
            }

            // Accept: build page, cache, and re-render
            var (page, _) = await _pipeline.BuildPageAsync(loadResult, url, cancellationToken).ConfigureAwait(false);

            _pageCache.Put(url, loadResult);
            _navigationService.ReplaceCurrent(page);
            _navigationService.SetCacheInfo(true, DateTime.UtcNow);
            _lineCacheManager.InvalidateLineCache();

            // Save cookies from the headed browser session (enables future browser
            // loads of paywalled articles to use the user's login)
            await SaveBrowserCookiesAsync(cancellationToken).ConfigureAwait(false);

            _preloadService.NotifyPageLoaded(page);
            NotifyPreloadSelectionChanged();

            await RenderCurrentPageAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during interactive refresh for {Url}", url);
            _renderer.RenderError(ex.Message, url);
        }
        finally
        {
            _preloadService.Resume();
        }
    }

    /// <summary>
    /// Saves cookies from the active headed browser session for future use.
    /// Enables subsequent browser loads to use the user's login on paywalled sites.
    /// </summary>
    private async Task SaveBrowserCookiesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_browserSession is not IBrowserSession session || !session.HasActiveBrowser || !session.IsBrowserAvailable)
            {
                return;
            }

            var page = await session.GetOrCreatePageAsync(false).ConfigureAwait(false);
            var playwrightCookies = await page.Context.CookiesAsync().ConfigureAwait(false);
            var storedCookies = playwrightCookies.Select(c =>
                new Application.Interfaces.StoredCookie(
                    c.Name,
                    c.Value,
                    c.Domain ?? string.Empty,
                    c.Path ?? string.Empty,
                    c.Expires > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)c.Expires).DateTime : null)).ToList();

            await _cookieManager.SaveCookiesAsync(storedCookies, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Saved {Count} browser cookies after interactive refresh", storedCookies.Count);

            // Refresh HTTP client cookies so the preloader can use them
            await _httpCookieRefresher.RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to save browser cookies (non-fatal)");
        }
    }
}
