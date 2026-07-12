// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.Interfaces.Browser;

namespace WireCopy.Infrastructure.Browser.Shell;

/// <summary>
/// Plain-terminal-mode stand-in: no shell is attached, every call no-ops. Keeps callers
/// free of null checks and terminal mode zero-cost.
/// </summary>
public sealed class NullShellChannel : IShellChannel
{
    /// <inheritdoc />
    public event Action<string>? ModeChanged
    {
        add
        {
            // Intentionally no-op: there is no shell to raise events.
        }

        remove
        {
            // Intentionally no-op: there is no shell to raise events.
        }
    }

    /// <inheritdoc />
    public bool IsConnected => false;

    /// <inheritdoc />
    public Task<string?> GetCdpEndpointAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<bool> SetPaneVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> SetModeAsync(string mode, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> CreatePageAsync(string tag, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
