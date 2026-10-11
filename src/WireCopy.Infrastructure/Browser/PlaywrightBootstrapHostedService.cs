// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Runs the Patchright driver self-heal once at startup (workspace-ltre follow-up) so a missing or
/// non-executable driver is restored before the first page load — turning the "Driver not found" blank
/// pane into either a working browser or a single clear, actionable log line. See
/// <see cref="PlaywrightDriverBootstrapper"/>. Best-effort: never blocks or faults startup.
/// </summary>
public sealed class PlaywrightBootstrapHostedService : IHostedService
{
    private readonly ILogger<PlaywrightBootstrapHostedService> _logger;

    public PlaywrightBootstrapHostedService(ILogger<PlaywrightBootstrapHostedService> logger) => _logger = logger;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            PlaywrightDriverBootstrapper.Ensure(_logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playwright driver startup check failed (non-fatal).");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
