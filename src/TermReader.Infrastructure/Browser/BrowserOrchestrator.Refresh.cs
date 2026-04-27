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
    private async Task ForceRefreshAsync(string url, RenderOptions options, CancellationToken cancellationToken)
    {
        _preloadService.Pause();

        try
        {
            _renderer.RenderLoading(url);

            var loadResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = url, Headless = _browserConfig.Headless, ForceRefresh = true },
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

            // Show prompt and wait for user to accept or cancel
            _renderer.RenderInteractiveRefresh(url);

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
