// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-5oe9.13 — a bounded, self-cancelling await-pump that keeps a UI
/// "Analyzing…" indicator live during a slow model round-trip WITHOUT standing
/// up a background timer or render-loop thread. It races the work task against a
/// short delay; each time the delay wins it invokes <c>onTick</c> (which
/// re-renders) with the elapsed time, and it returns the moment the work task
/// completes — so it never ticks after completion (no leak).
/// </summary>
internal static class ProgressPump
{
    /// <summary>Default repaint cadence while a model call is pending.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    public static async Task<T> RunAsync<T>(
        Task<T> work,
        Func<TimeSpan, Task> onTick,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(onTick);

        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var delay = Task.Delay(interval, cancellationToken);
            var winner = await Task.WhenAny(work, delay).ConfigureAwait(false);
            if (winner == work)
            {
                return await work.ConfigureAwait(false);
            }

            await onTick(Stopwatch.GetElapsedTime(start)).ConfigureAwait(false);
        }
    }
}
