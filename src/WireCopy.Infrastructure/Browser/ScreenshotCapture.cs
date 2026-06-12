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

    /// <summary>
    /// workspace-romy.1: cap for the setup wizard. The old 750ms default raced a
    /// capture this file documents as taking 0.5-3s, so the wizard almost always
    /// proceeded text-only — the model never saw the page it was asked to lay
    /// out. The wizard's first model round-trip takes far longer than 4s, and
    /// the capture is started while the entry card renders, so waiting here
    /// costs the user nothing.
    /// </summary>
    public static readonly TimeSpan WizardCap = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Reads the pixel dimensions from a PNG's IHDR chunk (bytes 16-23) for
    /// attach telemetry. Returns null when the buffer is not a parseable PNG.
    /// </summary>
    public static (int Width, int Height)? TryReadPngSize(byte[]? png)
    {
        if (png == null || png.Length < 24)
        {
            return null;
        }

        // PNG signature: 89 50 4E 47 0D 0A 1A 0A, then IHDR length+type.
        if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
        {
            return null;
        }

        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return width > 0 && height > 0 ? (width, height) : null;
    }

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
