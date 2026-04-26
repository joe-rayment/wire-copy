// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Entities.Browser;

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Service for intelligently pre-loading pages the user is likely to navigate to next.
/// </summary>
public interface IPreloadService : IDisposable
{
    /// <summary>
    /// Notifies the pre-loader that a new page has been loaded,
    /// allowing it to enqueue that page's links for pre-loading.
    /// </summary>
    /// <param name="page">The page that was just loaded.</param>
    void NotifyPageLoaded(Page page);

    /// <summary>
    /// Notifies the pre-loader that the user's selection has changed
    /// in the link tree, allowing it to re-prioritize the pre-load queue.
    /// </summary>
    /// <param name="selectedIndex">Index of the selected node in the visible nodes list.</param>
    /// <param name="visibleNodes">Current list of visible link nodes.</param>
    /// <param name="currentPageUrl">URL of the current page (for same-origin filtering).</param>
    void NotifySelectionChanged(int selectedIndex, IReadOnlyList<LinkNode> visibleNodes, string currentPageUrl);

    /// <summary>
    /// Notifies the pre-loader that a collection view has changed,
    /// allowing it to enqueue collection item URLs for pre-loading.
    /// Uses IReadOnlyList&lt;string&gt; to avoid coupling to Collection domain.
    /// </summary>
    /// <param name="selectedIndex">Index of the selected item in the collection.</param>
    /// <param name="urls">Flat list of URLs from the collection items.</param>
    void NotifyCollectionChanged(int selectedIndex, IReadOnlyList<string> urls);

    /// <summary>
    /// Clears the pre-load queue. Call when transitioning to views
    /// that don't support pre-loading (e.g., Launcher, CollectionList).
    /// </summary>
    void ClearQueue();

    /// <summary>
    /// Enables eager mode so the next preload batch skips the idle wait
    /// and processes the queue immediately. Resets automatically after
    /// the batch completes (queue drained).
    /// </summary>
    void EnableEagerMode();

    /// <summary>
    /// Starts the background pre-loading loop.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop pre-loading.</param>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Temporarily pauses pre-loading (e.g., during user-initiated navigation).
    /// </summary>
    void Pause();

    /// <summary>
    /// Resumes pre-loading after a pause.
    /// </summary>
    void Resume();

    /// <summary>
    /// Gets current pre-loading progress for display in the UI.
    /// </summary>
    PreloadProgress GetProgress();

    /// <summary>
    /// Raised after each successful preload fetch that changes the cache state.
    /// Subscribers can use this to trigger a UI refresh of the status bar.
    /// Fires on a background thread — subscribers must handle thread safety.
    /// </summary>
    event Action? ProgressChanged;

    /// <summary>
    /// Waits for an in-flight preload of the given URL to complete.
    /// Returns the result if successful, or null if no preload is in progress
    /// or the timeout expires.
    /// </summary>
    /// <param name="url">The URL to wait for.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The preload result, or null if not available.</returns>
    Task<PageLoadResult?> WaitForInFlightAsync(string url, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the set of URLs that have been extracted and stored in the article content cache
    /// during background preloading. Used by renderers to show cache indicators for collection
    /// items that are available in the persistent article cache.
    /// </summary>
    /// <returns>Set of original (non-normalized) URLs with article cache entries.</returns>
    IReadOnlySet<string> GetArticleCachedUrls();

    /// <summary>
    /// Checks if a URL's domain has been identified as requiring JavaScript rendering
    /// (browser-based loading) for content extraction.
    /// </summary>
    bool IsDomainNeedsJs(string url);
}
