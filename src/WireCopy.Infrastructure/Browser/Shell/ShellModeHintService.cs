// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;

namespace WireCopy.Infrastructure.Browser.Shell;

/// <summary>
/// Surfaces the shell's input-mode changes in the TUI status bar: entering browser mode
/// (a click into the live pane, or a checkpoint/login restore) teaches "Esc returns to the
/// reader"; returning announces that keys flow to the app again. No-ops in terminal mode.
/// </summary>
public sealed class ShellModeHintService : IHostedService
{
    private readonly IShellChannel _channel;
    private readonly NavigationService _navigation;
    private readonly ILogger<ShellModeHintService> _logger;

    /// <summary>Initializes the hint service.</summary>
    public ShellModeHintService(
        IShellChannel channel,
        NavigationService navigation,
        ILogger<ShellModeHintService> logger)
    {
        _channel = channel;
        _navigation = navigation;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_channel.IsConnected)
        {
            _channel.ModeChanged += OnModeChanged;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.ModeChanged -= OnModeChanged;
        return Task.CompletedTask;
    }

    private void OnModeChanged(string mode)
    {
        _logger.LogInformation("Shell input mode changed: {Mode}", mode);
        if (mode == "browser")
        {
            _navigation.SetStatusMessage("Browser mode — Esc returns to the reader", TimeSpan.FromSeconds(8));
        }
        else
        {
            _navigation.SetStatusMessage("Reader mode — keys go to the app", TimeSpan.FromSeconds(4));
        }
    }
}
