// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Options;
using TermReader.Application.Interfaces.Browser;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Browser.Cache;

/// <summary>
/// Detects user idle state by tracking the time since last activity.
/// Uses a polling timer consistent with the TerminalResizeDetector pattern.
/// </summary>
public sealed class InputIdleDetector : IIdleDetector
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly int _idleThresholdMs;
    private volatile bool _isIdle;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private TaskCompletionSource _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Timer _timer;

    public InputIdleDetector(IOptions<CacheConfiguration> config)
    {
        _idleThresholdMs = config.Value.IdleThresholdMs;
        _timer = new Timer(
            CheckIdleState,
            null,
            PollInterval,
            PollInterval);
    }

    public bool IsIdle => _isIdle;

    public void RecordActivity()
    {
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        if (_isIdle)
        {
            _isIdle = false;
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        if (_isIdle)
        {
            return;
        }

        // Wait for the idle state transition
        while (!cancellationToken.IsCancellationRequested)
        {
            var tcs = Volatile.Read(ref _idleTcs);
            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());
            await tcs.Task.ConfigureAwait(false);

            if (_isIdle)
            {
                return;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _idleTcs.TrySetCanceled();
    }

    private void CheckIdleState(object? state)
    {
        var lastActivity = new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
        var elapsed = DateTime.UtcNow - lastActivity;
        var shouldBeIdle = elapsed.TotalMilliseconds >= _idleThresholdMs;

        if (shouldBeIdle && !_isIdle)
        {
            _isIdle = true;

            // Signal any waiters
            var oldTcs = Interlocked.Exchange(
                ref _idleTcs,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            oldTcs.TrySetResult();
        }
    }
}
