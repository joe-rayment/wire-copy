// Educational and personal use only.

using System.Threading.Channels;
using TermReader.Application.Interfaces.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Polls Console.WindowWidth/WindowHeight at 100ms intervals to detect terminal resizes.
/// Writes to a bounded channel (capacity 1, DropOldest) so rapid resizes coalesce naturally.
/// Linux workaround: reads Console.CursorTop before Console.WindowWidth on each tick (dotnet/runtime#41274).
/// </summary>
public sealed class TerminalResizeDetector : IResizeDetector
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true
        });

    public ChannelReader<bool> Resizes => _channel.Reader;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ReadDimensions(out var lastWidth, out var lastHeight);

        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            ReadDimensions(out var width, out var height);

            if (width != lastWidth || height != lastHeight)
            {
                lastWidth = width;
                lastHeight = height;
                _channel.Writer.TryWrite(true);
            }
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
    }

    private static void ReadDimensions(out int width, out int height)
    {
        try
        {
            // Linux workaround: read CursorTop first to flush stale dimension cache
            // See: https://github.com/dotnet/runtime/issues/41274
            _ = Console.CursorTop;
            width = Console.WindowWidth;
            height = Console.WindowHeight;
        }
        catch
        {
            width = 80;
            height = 24;
        }
    }
}
