// Licensed under the MIT License. See LICENSE in the repository root.

using System.Threading.Channels;

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Detects terminal resize events and signals them via a channel.
/// </summary>
public interface IResizeDetector : IDisposable
{
    /// <summary>
    /// Channel reader that emits true when a resize is detected.
    /// </summary>
    ChannelReader<bool> Resizes { get; }

    /// <summary>
    /// Starts polling for resize events.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);
}
