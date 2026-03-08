// Educational and personal use only.

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
}
