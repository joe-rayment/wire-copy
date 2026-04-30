// Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Default <see cref="IAutoCookieRefresher"/> implementation. Hooks into the
/// page-load pipeline and, when a foreground load lands on a paywalled domain
/// in a logged-in state, performs the same import flow as the manual
/// <c>:cookies import</c> command:
///
/// <list type="number">
///   <item>Read cookies from the live Playwright browser context.</item>
///   <item>Persist them via <see cref="ICookieManager.SaveCookiesAsync"/>.</item>
///   <item>Refresh the shared HTTP cookie container via <see cref="IHttpCookieRefresher.RefreshAsync"/>.</item>
/// </list>
///
/// <para>
/// Throttled per-host with <see cref="CooldownWindow"/> to avoid running on
/// every page load. Failures are logged but never thrown.
/// </para>
/// </summary>
public class AutoCookieRefresher : IAutoCookieRefresher
{
    /// <summary>
    /// Minimum interval between auto-imports for the same host. Manual
    /// <c>:cookies import</c> bypasses this throttle.
    /// </summary>
    public static readonly TimeSpan CooldownWindow = TimeSpan.FromHours(24);

    private readonly IBrowserSession _browserSession;
    private readonly ICookieManager _cookieManager;
    private readonly IHttpCookieRefresher _httpRefresher;
    private readonly BrowserConfiguration _browserConfig;
    private readonly NavigationService? _navigationService;
    private readonly ILogger<AutoCookieRefresher> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _lastRefreshUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    public AutoCookieRefresher(
        IBrowserSession browserSession,
        ICookieManager cookieManager,
        IHttpCookieRefresher httpRefresher,
        BrowserConfiguration browserConfig,
        NavigationService? navigationService,
        ILogger<AutoCookieRefresher> logger,
        TimeProvider? timeProvider = null)
    {
        _browserSession = browserSession;
        _cookieManager = cookieManager;
        _httpRefresher = httpRefresher;
        _browserConfig = browserConfig;
        _navigationService = navigationService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<bool> MaybeRefreshAsync(string url, string? html, CancellationToken cancellationToken = default)
    {
        // Gate 1: paywalled domain.
        if (string.IsNullOrWhiteSpace(url) || !_browserConfig.IsPaywalledDomain(url))
        {
            return false;
        }

        string host;
        try
        {
            host = new Uri(url).Host;
        }
        catch
        {
            return false;
        }

        // Gate 2: cooldown.
        if (_lastRefreshUtc.TryGetValue(host, out var last))
        {
            var sinceLast = _timeProvider.GetUtcNow().UtcDateTime - last;
            if (sinceLast < CooldownWindow)
            {
                _logger.LogDebug(
                    "AutoCookieRefresher: skipping {Host} (cooldown, {Elapsed} < {Window})",
                    host,
                    sinceLast,
                    CooldownWindow);
                return false;
            }
        }

        // Gate 3: logged-in markup heuristic.
        if (!LoggedInPaywallDetector.LooksLoggedIn(html))
        {
            _logger.LogDebug(
                "AutoCookieRefresher: skipping {Host} (markup does not look logged-in)",
                host);
            return false;
        }

        // Gate 4: browser session must have a context to read cookies from.
        if (!_browserSession.HasBrowserContext)
        {
            _logger.LogDebug(
                "AutoCookieRefresher: skipping {Host} (no browser context)",
                host);
            return false;
        }

        // Mark before the work runs so concurrent loads don't pile on.
        _lastRefreshUtc[host] = _timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            var aggregated = new Dictionary<(string Name, string Domain, string Path), StoredCookie>();
            foreach (var domain in _browserConfig.PaywalledDomains)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var domainUrl = $"https://{domain}/";
                IReadOnlyList<StoredCookie> domainCookies;
                try
                {
                    domainCookies = await _browserSession.GetCookiesForUrlAsync(domainUrl).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "AutoCookieRefresher: failed to read cookies for {Domain}", domain);
                    continue;
                }

                foreach (var c in domainCookies)
                {
                    aggregated[(c.Name, c.Domain, c.Path)] = c;
                }
            }

            if (aggregated.Count == 0)
            {
                _logger.LogDebug(
                    "AutoCookieRefresher: no cookies returned by browser for paywalled domains; skipping save for {Host}",
                    host);
                return true;
            }

            var toSave = aggregated.Values.ToList();
            await _cookieManager.SaveCookiesAsync(toSave, cancellationToken).ConfigureAwait(false);

            try
            {
                await _httpRefresher.RefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "AutoCookieRefresher: cookies saved for {Host} but HTTP refresh failed",
                    host);
            }

            _logger.LogInformation(
                "AutoCookieRefresher: imported {Count} cookies for {Host} (logged-in markup detected)",
                toSave.Count,
                host);

            // Best-effort user notification — surface once per refresh so the
            // user knows the background HTTP container has fresh cookies.
            try
            {
                _navigationService?.SetStatusMessage(
                    $"Cookies refreshed for {host}",
                    TimeSpan.FromSeconds(4));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AutoCookieRefresher: failed to set status message");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoCookieRefresher: import failed for {Host}", host);
            return true;
        }
    }
}
