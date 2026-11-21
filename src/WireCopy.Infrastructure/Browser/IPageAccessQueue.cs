// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Playwright;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Priority levels for browser page access.
/// Foreground requests (user navigation) preempt background requests (article scraping).
/// </summary>
public enum PageAccessPriority
{
    /// <summary>
    /// User-initiated navigation. Gets immediate priority over background tasks.
    /// </summary>
    Foreground,

    /// <summary>
    /// Background article scraping. Yields to foreground requests.
    /// </summary>
    Background,
}

/// <summary>
/// Serializes access to the shared browser page with priority levels.
/// Since the browser context is single-threaded, all access must be serialized.
/// Foreground requests always preempt background requests.
/// </summary>
public interface IPageAccessQueue
{
    /// <summary>
    /// Gets a value indicating whether a background task currently holds the page.
    /// Useful for logging and diagnostics.
    /// </summary>
    bool IsBackgroundActive { get; }

    /// <summary>
    /// Acquires the browser page for exclusive use. Blocks until available.
    /// Foreground requests are served before background requests.
    /// </summary>
    /// <param name="priority">The priority level of the request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lease that must be disposed to release the page.</returns>
    Task<PageLease> AcquireAsync(PageAccessPriority priority, CancellationToken cancellationToken);
}

/// <summary>
/// Represents exclusive access to the browser page. Dispose to release.
/// </summary>
public sealed class PageLease : IDisposable
{
    // The lease the current async call chain is working under, once its holder has said so
    // with CoverCallees. It flows down into everything the holder awaits and never back up.
    private static readonly AsyncLocal<PageLease?> Covering = new();

    private readonly Action _release;
    private volatile bool _disposed;

    internal PageLease(IPage page, Action release)
    {
        Page = page;
        _release = release;
    }

    /// <summary>
    /// Gets a value indicating whether the calling code runs underneath a lease that its own
    /// caller holds and declared with <see cref="CoverCallees"/>. The queue is not re-entrant:
    /// a page user that asked for a second lease there would wait on its own caller until the
    /// wait timed out, so it uses the page under the caller's lease instead (workspace-q06ia.1).
    /// </summary>
    public static bool HeldByCaller => Covering.Value is { _disposed: false };

    /// <summary>
    /// Gets the Playwright page for use during this lease.
    /// </summary>
    public IPage Page { get; }

    /// <summary>
    /// Declares that everything the holder awaits inside the returned scope runs under this
    /// lease. Without it, a holder that calls a lease-taking page user (the page loader's own
    /// browser fetch) queues behind itself: the podcast article fetch did exactly that for
    /// 30 seconds per article and then fell back to plain HTTP (workspace-q06ia.1).
    /// </summary>
    /// <returns>A scope to dispose before the lease itself is released.</returns>
    public IDisposable CoverCallees()
    {
        var previous = Covering.Value;
        Covering.Value = this;
        return new CoverScope(previous);
    }

    /// <summary>
    /// Releases the browser page back to the queue.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _release();
    }

    private sealed class CoverScope : IDisposable
    {
        private readonly PageLease? _previous;

        public CoverScope(PageLease? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            Covering.Value = _previous;
        }
    }
}
