// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Populates the shared <see cref="System.Net.CookieContainer"/> from persisted
/// cookies before the HTTP client is first used. Replaces the previous
/// sync-over-async block inside the HttpClient handler factory.
/// </summary>
internal sealed class CookieContainerWarmupService : IHostedService
{
    private readonly IHttpCookieRefresher _refresher;
    private readonly ILogger<CookieContainerWarmupService> _logger;

    public CookieContainerWarmupService(
        IHttpCookieRefresher refresher,
        ILogger<CookieContainerWarmupService> logger)
    {
        _refresher = refresher;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _refresher.RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Cookie loading failure must never crash startup. The HttpClient will
            // simply make unauthenticated requests until cookies are written by a
            // subsequent browser login.
            _logger.LogWarning(ex, "Failed to warm cookie container at startup; continuing without it");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
