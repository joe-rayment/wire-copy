// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Runs the podcast output-folder purge once at app startup so files older than
/// <see cref="PodcastConfiguration.OutputRetentionHours"/> are removed even when
/// the user never opens the podcast feature in this session.
/// </summary>
internal sealed class OutputFolderPurgeStartupService : IHostedService
{
    private readonly OutputFolderPurger _purger;
    private readonly PodcastConfiguration _config;
    private readonly ILogger<OutputFolderPurgeStartupService> _logger;

    public OutputFolderPurgeStartupService(
        OutputFolderPurger purger,
        IOptions<PodcastConfiguration> config,
        ILogger<OutputFolderPurgeStartupService> logger)
    {
        _purger = purger;
        _config = config.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_config.OutputRetentionHours <= 0)
        {
            return Task.CompletedTask;
        }

        var folder = _config.ResolveOutputFolderPath();
        var ttl = TimeSpan.FromHours(_config.OutputRetentionHours);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _purger.PurgeOldFilesAsync(folder, ttl, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Host is shutting down before the purge completed; nothing to do.
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Startup output-folder purge failed (non-fatal)");
                }
            },
            cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
