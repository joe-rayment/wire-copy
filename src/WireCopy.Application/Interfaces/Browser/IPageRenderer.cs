// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Application.Interfaces.Browser;

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
    /// <param name="status">Optional status text (e.g., "Analyzing layout...").</param>
    void RenderLoading(string url, string? status = null);

    /// <summary>
    /// Renders the loading screen with stage and elapsed time.
    /// </summary>
    void RenderLoading(string url, string? status, long elapsedMs);

    /// <summary>
    /// Renders error message.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="url">URL that failed.</param>
    void RenderError(string message, string url);

    /// <summary>
    /// Renders a message telling the user to solve a challenge in the browser window.
    /// </summary>
    /// <param name="url">URL that triggered the challenge.</param>
    void RenderChallenge(string url);

    /// <summary>
    /// Renders a variant-aware human-action box (workspace-0b9s). Replaces the
    /// generic "Something went wrong" / "Bot challenge detected" copy with copy
    /// that names the variant (CAPTCHA, login, cookie consent, 2FA, paywall,
    /// region block, generic) and tells the user exactly what to do next.
    /// </summary>
    /// <param name="action">Typed signal describing the action required.</param>
    /// <param name="url">URL that triggered the action.</param>
    void RenderHumanAction(HumanActionRequired action, string url);

    /// <summary>
    /// Renders a message for interactive refresh mode (headed browser open for
    /// manual intervention). workspace-lmwm: the copy is verdict-aware — when no
    /// gate was detected the screen says the page loaded instead of falsely
    /// claiming a captcha exists.
    /// </summary>
    /// <param name="url">URL being loaded interactively.</param>
    /// <param name="requiredAction">
    /// Typed detection verdict from the headed load / challenge poll, or null
    /// when the page loaded cleanly with no human action detected.
    /// </param>
    /// <param name="layoutSetupHint">
    /// workspace-u5vu: true when the user pressed Shift+I on a link-list page
    /// outside preview mode — the context where they likely reached for the
    /// layout setup wizard (preview-mode Shift+I) — so the prompt points them
    /// at Ctrl+L.
    /// </param>
    void RenderInteractiveRefresh(string url, HumanActionRequired? requiredAction, bool layoutSetupHint = false);

    /// <summary>
    /// Renders a message telling the user to complete login in the browser window.
    /// workspace-mokw: when <paramref name="elapsedMs"/> / <paramref name="timeoutMs"/>
    /// are supplied, the waiting line carries a spinner plus an
    /// "Xs elapsed (Ym timeout)" clock so the poll loop doesn't look hung.
    /// </summary>
    /// <param name="url">URL that triggered the login requirement.</param>
    /// <param name="domain">Domain requiring login.</param>
    /// <param name="elapsedMs">Milliseconds spent waiting so far (0 = no clock shown).</param>
    /// <param name="timeoutMs">Total wait budget in milliseconds (0 = no clock shown).</param>
    void RenderManualLogin(string url, string domain, long elapsedMs = 0, long timeoutMs = 0);

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

    /// <summary>
    /// Starts a buffered render frame. All Write/SetCursorPosition calls until
    /// EndFrame is invoked are accumulated into a single buffer and emitted as
    /// one atomic write — preventing OS pipe-buffer flushes from splitting an
    /// SGR escape sequence mid-write (workspace-1f5a).
    /// Implementations MAY no-op if they don't buffer.
    /// </summary>
    void BeginFrame();

    /// <summary>
    /// Emits the buffered frame as a single atomic write. Safe to call when
    /// BeginFrame was not invoked (no-op).
    /// </summary>
    void EndFrame();
}
