// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces;

namespace WireCopy.Infrastructure.Demo;

/// <summary>
/// workspace-kt19.2 — starts the loopback demo site server when (a) the demo
/// content pack is present and (b) the bookmark set references the demo origin
/// (a fresh install seeds the demo bookmarks; a user who has replaced them with
/// their own pays zero cost). Failure never blocks startup.
/// </summary>
internal sealed class DemoSiteHostedService : IHostedService
{
    private readonly IBookmarkConfigStore _configStore;
    private readonly ILogger<DemoSiteHostedService> _logger;
    private DemoSiteServer? _server;

    public DemoSiteHostedService(IBookmarkConfigStore configStore, ILogger<DemoSiteHostedService> logger)
    {
        _configStore = configStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var root = DemoSiteServer.ResolveContentRoot();
            if (root == null)
            {
                _logger.LogDebug("Demo site: no content pack found; server not started");
                return;
            }

            // The user file wins; before first-run seeding the shipped defaults
            // ARE what the reconciler will write, so consult them as a fallback.
            var config = _configStore.UserConfigExists()
                ? await _configStore.LoadUserConfigAsync(cancellationToken).ConfigureAwait(false)
                : await _configStore.LoadShippedDefaultsAsync(cancellationToken).ConfigureAwait(false);
            var referenced = config?.Bookmarks.Any(b =>
                b.Url.StartsWith(DemoSiteServer.Origin, StringComparison.OrdinalIgnoreCase)) ?? false;
            if (!referenced)
            {
                _logger.LogDebug("Demo site: no bookmark references {Origin}; server not started", DemoSiteServer.Origin);
                return;
            }

            _server = new DemoSiteServer(root, _logger);
            _server.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Demo site server failed to start; demo bookmarks will not resolve");
            _server?.Dispose();
            _server = null;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Dispose();
        return Task.CompletedTask;
    }
}
