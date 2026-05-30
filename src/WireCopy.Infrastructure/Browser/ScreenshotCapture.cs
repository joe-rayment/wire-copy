// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-5oe9.11 — bounds a (potentially slow, 0.5-3s Playwright) screenshot
/// capture so it never blocks the AI setup wizard. The capture races a cap: if
/// it has not resolved in time the analyzer proceeds text-only (null screenshot)
/// while the capture task is left to finish in the background (its result is
/// discarded; faults are swallowed so they aren't unobserved).
/// </summary>
internal static class ScreenshotCapture
{
    /// <summary>Default cap before the AI path gives up waiting and goes text-only.</summary>
    public static readonly TimeSpan DefaultCap = TimeSpan.FromMilliseconds(750);

    public static async Task<byte[]?> WithCapAsync(
        Func<Task<byte[]?>> capture,
        TimeSpan cap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        Task<byte[]?> captureTask;
        try
        {
            captureTask = capture();
        }
        catch
        {
            return null;
        }

        var delay = Task.Delay(cap, cancellationToken);
        var winner = await Task.WhenAny(captureTask, delay).ConfigureAwait(false);
        if (winner == captureTask)
        {
            try
            {
                return await captureTask.ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        // Cap hit: proceed text-only. Observe the lingering task's fault later so
        // it doesn't surface as an unobserved exception.
        _ = captureTask.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return null;
    }
}
