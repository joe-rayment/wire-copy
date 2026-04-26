// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Refreshes the shared HTTP CookieContainer from persistent storage.
/// Called after browser-based login saves new cookies.
/// </summary>
internal sealed class HttpCookieRefresher : IHttpCookieRefresher
{
    private readonly CookieContainer _container;
    private readonly ICookieManager _cookieManager;
    private readonly ILogger<HttpCookieRefresher> _logger;

    public HttpCookieRefresher(
        CookieContainer container,
        ICookieManager cookieManager,
        ILogger<HttpCookieRefresher> logger)
    {
        _container = container;
        _cookieManager = cookieManager;
        _logger = logger;
    }

    public async Task RefreshAsync()
    {
        try
        {
            var cookies = await _cookieManager.LoadCookiesAsync();
            var added = 0;

            foreach (var cookie in cookies)
            {
                try
                {
                    _container.Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                    added++;
                }
                catch (CookieException ex)
                {
                    _logger.LogDebug(ex, "Skipped invalid cookie {Name} for {Domain}", cookie.Name, cookie.Domain);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogDebug(ex, "Skipped malformed cookie {Name} for {Domain}", cookie.Name, cookie.Domain);
                }
            }

            _logger.LogInformation("Refreshed HTTP cookie container with {Count} cookies", added);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh HTTP cookie container");
        }
    }
}
