// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Entities.Bookmarks;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Service for rendering pages to the terminal.
/// Handles both hierarchical (link tree) and readable (article) views.
/// </summary>
public interface IPageRenderer
{
    /// <summary>
    /// Renders the page in hierarchical view (link tree).
    /// </summary>
    /// <param name="page">Page to render.</param>
    /// <param name="context">Current navigation context.</param>
    /// <param name="options">Render options.</param>
    void RenderHierarchical(Page page, NavigationContext context, RenderOptions options);

    /// <summary>
    /// Renders the page in readable view (article content).
    /// </summary>
    /// <param name="page">Page to render.</param>
    /// <param name="context">Current navigation context.</param>
    /// <param name="options">Render options.</param>
    void RenderReadable(Page page, NavigationContext context, RenderOptions options, List<string>? wrappedLines = null);

    /// <summary>
    /// Renders loading indicator.
    /// </summary>
    /// <param name="url">URL being loaded.</param>
    void RenderLoading(string url);

    /// <summary>
    /// Renders error message.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="url">URL that failed.</param>
    void RenderError(string message, string url);

    /// <summary>
    /// Renders status bar at bottom of screen.
    /// </summary>
    /// <param name="context">Current navigation context.</param>
    /// <param name="mode">Current view mode.</param>
    void RenderStatusBar(NavigationContext context, ViewMode mode);

    /// <summary>
    /// Renders the collection list view (all collections).
    /// </summary>
    /// <param name="collections">List of collections to render.</param>
    /// <param name="selectedIndex">Currently selected collection index.</param>
    /// <param name="defaultCollectionId">ID of the default collection.</param>
    /// <param name="options">Render options.</param>
    void RenderCollectionList(List<Collection> collections, int selectedIndex, Guid? defaultCollectionId, int scrollOffset, RenderOptions options);

    /// <summary>
    /// Renders items within a collection.
    /// </summary>
    /// <param name="collection">Collection to render.</param>
    /// <param name="selectedIndex">Currently selected item index.</param>
    /// <param name="scrollOffset">Scroll offset for the item list.</param>
    /// <param name="options">Render options.</param>
    void RenderCollectionItems(Collection collection, int selectedIndex, int scrollOffset, RenderOptions options);

    /// <summary>
    /// Renders the launcher home screen with bookmark tiles.
    /// </summary>
    /// <param name="bookmarks">List of bookmarks to render as tiles.</param>
    /// <param name="selectedIndex">Currently selected tile index.</param>
    /// <param name="scrollOffset">Scroll offset for the grid.</param>
    /// <param name="options">Render options.</param>
    void RenderLauncher(List<Bookmark> bookmarks, int selectedIndex, int scrollOffset, RenderOptions options);

    /// <summary>
    /// Clears the terminal screen.
    /// </summary>
    void Clear();
}
