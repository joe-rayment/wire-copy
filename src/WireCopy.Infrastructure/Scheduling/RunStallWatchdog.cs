// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// Stops a routine run that has shown no progress for a while (workspace-q06ia.4).
/// </summary>
/// <remarks>
/// <para>
/// This replaces the scheduler's old cap of 20 minutes on the whole run. That cap dated from
/// when narration took seconds; on a local engine a morning's stories take over an hour, so
/// every scheduled run that reached narration was cut off at minute 20 while it was working.
/// A run is now stopped for standing still, never for taking long.
/// </para>
/// <para>
/// Progress is a change in the picture the run reports — a section loaded, the next story, the
/// next narrated part, a new percentage. The same picture reported again is not progress: the
/// audio-engine client re-reports an unchanged job every second while it polls.
/// </para>
/// <para>
/// A picture can be reported with a longer time than the window: a narrated part gets the time
/// audio-engine itself allows its narrator for loading the model and speaking (workspace-wxll0.6).
/// </para>
/// </remarks>
internal sealed class RunStallWatchdog : IDisposable
{
    /// <summary>Environment variable that overrides <see cref="DefaultWindow"/>, in whole seconds.</summary>
    internal const string WindowVariable = "WIRECOPY_ROUTINE_STALL_SECONDS";

    /// <summary>How long a run may show no progress before it is stopped.</summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(20);

    private readonly CancellationTokenSource _run;
    private readonly Timer _timer;
    private readonly Lock _lock = new();
    private string? _picture;
    private volatile bool _fired;
    private bool _disposed;

    public RunStallWatchdog(CancellationTokenSource run, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = run;
        Window = window;
        Patience = window;
        _timer = new Timer(_ => Fire(), null, window, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Gets the window this watchdog was started with.</summary>
    public TimeSpan Window { get; }

    /// <summary>
    /// Gets how long the picture reported last may stand: <see cref="Window"/>, or the longer time
    /// that picture was reported with. When the watchdog fired, this is how long the run stood still.
    /// </summary>
    public TimeSpan Patience { get; private set; }

    /// <summary>Gets a value indicating whether this watchdog is what stopped the run.</summary>
    public bool Fired => _fired;

    /// <summary>
    /// Reads the window from <see cref="WindowVariable"/>, falling back to
    /// <see cref="DefaultWindow"/> — and saying so — when the value is not a positive number.
    /// </summary>
    public static TimeSpan ResolveWindow(ILogger logger)
    {
        var raw = Environment.GetEnvironmentVariable(WindowVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultWindow;
        }

        if (int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 1)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        logger.LogWarning(
            "{Variable}={Value:l} is not a whole number of seconds (1 or more); a routine run is stopped after {Minutes} minutes without progress, as usual",
            WindowVariable,
            raw,
            (int)DefaultWindow.TotalMinutes);
        return DefaultWindow;
    }

    /// <summary>Describes a window the way a person would say it: "20 minutes", "45 seconds".</summary>
    public static string Describe(TimeSpan window)
    {
        if (window.TotalSeconds < 120)
        {
            var seconds = (int)Math.Round(window.TotalSeconds);
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        }

        return $"{(int)Math.Round(window.TotalMinutes)} minutes";
    }

    /// <summary>
    /// Reports the run's current picture. The clock restarts only when the picture differs from
    /// the last one reported.
    /// </summary>
    public void Report(string picture) => Report(picture, Window);

    /// <summary>
    /// Reports the run's current picture together with how long that picture may stand. For the
    /// one step that is legitimately slower than the rest: a narrated part, before which
    /// audio-engine may have to load (the first time, download) its model (workspace-wxll0.6).
    /// </summary>
    /// <param name="picture">What the run has got to.</param>
    /// <param name="patience">How long this picture may stay unchanged before the run is stopped.</param>
    public void Report(string picture, TimeSpan patience)
    {
        lock (_lock)
        {
            if (_disposed || _fired || string.Equals(picture, _picture, StringComparison.Ordinal))
            {
                return;
            }

            _picture = picture;
            Patience = patience;
            _timer.Change(patience, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
    }

    private void Fire()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // Already cancelled (the user's stop, the app closing) or already over:
                // this watchdog is then not what ended the run.
                if (_run.IsCancellationRequested)
                {
                    return;
                }

                _fired = true;
                _run.Cancel();
            }
            catch (ObjectDisposedException)
            {
                _fired = false;
            }
        }
    }
}
