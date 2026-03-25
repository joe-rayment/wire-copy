// Educational and personal use only.

using Microsoft.Extensions.Logging;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Serializes access to the shared browser page with priority levels.
/// Foreground requests preempt background requests via a two-level semaphore:
/// background tasks must acquire both the main lock and a foreground-check gate,
/// while foreground tasks only need the main lock and can signal background to yield.
/// </summary>
internal sealed class PageAccessQueue : IPageAccessQueue, IDisposable
{
    private readonly IBrowserSession _browserSession;
    private readonly ILogger<PageAccessQueue> _logger;
    private readonly SemaphoreSlim _mainLock = new(1, 1);
    private readonly SemaphoreSlim _foregroundWaiting = new(0, 1);
    private volatile bool _backgroundActive;
    private volatile bool _foregroundRequested;
    private volatile bool _disposed;

    public PageAccessQueue(IBrowserSession browserSession, ILogger<PageAccessQueue> logger)
    {
        _browserSession = browserSession;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsBackgroundActive => _backgroundActive;

    /// <inheritdoc />
    public async Task<PageLease> AcquireAsync(
        PageAccessPriority priority,
        bool headless,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (priority == PageAccessPriority.Foreground)
        {
            return await AcquireForegroundAsync(headless, cancellationToken);
        }

        return await AcquireBackgroundAsync(headless, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mainLock.Dispose();
        _foregroundWaiting.Dispose();
    }

    private async Task<PageLease> AcquireForegroundAsync(bool headless, CancellationToken cancellationToken)
    {
        if (!_browserSession.IsBrowserAvailable)
        {
            throw new InvalidOperationException("Browser is unavailable on this platform");
        }

        _logger.LogDebug("Foreground requesting browser page access");

        if (_backgroundActive)
        {
            _logger.LogDebug("Background task active, signaling preemption");
            _foregroundRequested = true;
        }

        // Foreground waits up to 30s for the lock (background should yield quickly)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        await _mainLock.WaitAsync(cts.Token);

        _foregroundRequested = false;

        try
        {
            var page = await _browserSession.GetOrCreatePageAsync(headless);
            _logger.LogDebug("Foreground acquired browser page");
            return new PageLease(page, () => ReleaseForeground());
        }
        catch
        {
            _mainLock.Release();
            throw;
        }
    }

    private async Task<PageLease> AcquireBackgroundAsync(bool headless, CancellationToken cancellationToken)
    {
        if (!_browserSession.IsBrowserAvailable)
        {
            throw new InvalidOperationException("Browser is unavailable on this platform");
        }

        _logger.LogDebug("Background requesting browser page access");

        // Background waits up to 60s for the lock
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        await _mainLock.WaitAsync(cts.Token);

        _backgroundActive = true;

        try
        {
            var page = await _browserSession.GetOrCreatePageAsync(headless);
            _logger.LogDebug("Background acquired browser page");
            return new PageLease(page, () => ReleaseBackground());
        }
        catch
        {
            _backgroundActive = false;
            _mainLock.Release();
            throw;
        }
    }

    private void ReleaseForeground()
    {
        _logger.LogDebug("Foreground releasing browser page");

        try
        {
            _mainLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed during shutdown
        }
    }

    private void ReleaseBackground()
    {
        _logger.LogDebug("Background releasing browser page");
        _backgroundActive = false;

        try
        {
            _mainLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed during shutdown
        }

        // If foreground was waiting, signal it
        if (_foregroundRequested)
        {
            try
            {
                _foregroundWaiting.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signaled
            }
            catch (ObjectDisposedException)
            {
                // Disposed during shutdown
            }
        }
    }
}
