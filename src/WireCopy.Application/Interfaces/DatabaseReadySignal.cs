// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.Interfaces;

/// <summary>
/// Announces that the database schema exists and is current, so background work that
/// queries it can wait instead of racing the migration.
/// </summary>
/// <remarks>
/// workspace-xpr5t. Since workspace-5wkwq the EF migration runs CONCURRENTLY with
/// <c>host.StartAsync()</c>, and two hosted services sweep for orphaned rows the moment
/// they start. On a first run with no wirecopy.db those sweeps hit tables that do not
/// exist yet, and EF logs the failed command at Error with a full stack trace — around
/// forty lines of what looks like a broken app before the launcher has even painted.
/// Catching the exception in the sweep cannot help: the log line comes from EF's own
/// command interceptor, before anything of ours sees it. The only fix is not to run the
/// query yet.
///
/// The wait is bounded. The sweeps are best-effort and non-fatal by contract, so a host
/// that never signals (a test host, a future entry point that skips the migration) must
/// degrade to today's behaviour rather than leaving a task parked forever.
/// </remarks>
public sealed class DatabaseReadySignal
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes once the schema is usable. Also completes when initialization FAILED —
    /// waiters are unblocked either way, and a genuinely broken database reports itself
    /// through the waiter's own error handling rather than through a hang.
    /// </summary>
    public Task Ready => _ready.Task;

    /// <summary>Marks the schema usable. Safe to call more than once.</summary>
    public void MarkReady() => _ready.TrySetResult();

    /// <summary>
    /// Waits for <see cref="Ready"/>, giving up after <paramref name="timeout"/> so a
    /// host that never signals cannot strand the caller.
    /// </summary>
    /// <returns><c>true</c> if the signal arrived, <c>false</c> if the wait timed out.</returns>
    public async Task<bool> WaitAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (_ready.Task.IsCompleted)
        {
            return true;
        }

        var delay = Task.Delay(timeout ?? DefaultTimeout, cancellationToken);
        var winner = await Task.WhenAny(_ready.Task, delay).ConfigureAwait(false);
        return winner == _ready.Task;
    }
}
