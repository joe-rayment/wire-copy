// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;

namespace WireCopy.Infrastructure.Browser.Shell;

/// <summary>
/// Boot-time handshake evidence: when running under the desktop shell, performs the hello
/// exchange and logs the shell's CDP endpoint so gates (and humans) can verify the channel
/// is live. No-ops in plain terminal mode.
/// </summary>
public sealed class ShellChannelAnnouncer : IHostedService
{
    private readonly IShellChannel _channel;
    private readonly ILogger<ShellChannelAnnouncer> _logger;

    /// <summary>Initializes the announcer.</summary>
    public ShellChannelAnnouncer(IShellChannel channel, ILogger<ShellChannelAnnouncer> logger)
    {
        _channel = channel;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_channel.IsConnected)
        {
            return;
        }

        var endpoint = await _channel.GetCdpEndpointAsync(cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            _logger.LogWarning("Shell channel present but the hello handshake failed");
            return;
        }

        _logger.LogInformation("Shell channel connected; CDP endpoint {Endpoint}", endpoint);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
