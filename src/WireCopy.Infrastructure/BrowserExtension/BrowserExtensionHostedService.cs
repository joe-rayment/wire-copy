// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// Registers the browser extension's native-messaging host once at app startup (workspace-m7aml),
/// off the startup path, so the extension finds the app in every installed browser without a setup step.
/// </summary>
internal sealed class BrowserExtensionHostedService : IHostedService
{
    private readonly ILogger<BrowserExtensionHostedService> _logger;

    public BrowserExtensionHostedService(ILogger<BrowserExtensionHostedService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(
            () =>
            {
                try
                {
                    var registrations = NativeHostRegistrar.Register(
                        NativeHostEnvironment.FromCurrentProcess(),
                        NativeHostCommand.FromCurrentProcess());

                    var detected = registrations.Where(r => r.Detected).Select(r => r.DisplayName).ToList();
                    _logger.LogInformation(
                        "Browser extension host registered for {Count} browser(s): {Browsers}",
                        detected.Count,
                        detected.Count == 0 ? "none detected" : string.Join(", ", detected));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Browser extension host registration failed (non-fatal)");
                }
            },
            cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
