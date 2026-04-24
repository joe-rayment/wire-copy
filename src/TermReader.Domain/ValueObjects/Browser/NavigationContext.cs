// Educational and personal use only.

using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Domain.ValueObjects.Browser;

/// <summary>
/// Represents the current state of the browser navigation session.
/// Includes current page, history, and UI state.
/// </summary>
public record NavigationContext
{
    /// <summary>
    /// Currently displayed page.
    /// </summary>
    public Page? CurrentPage { get; init; }

    /// <summary>
    /// Current view mode (Hierarchical or Readable).
    /// </summary>
    public ViewMode ViewMode { get; init; } = ViewMode.Hierarchical;

    /// <summary>
    /// Index of currently selected link in hierarchical view.
    /// </summary>
    public int SelectedLinkIndex { get; init; }

    /// <summary>
    /// Current scroll offset (for pagination/viewport management).
    /// </summary>
    public int ScrollOffset { get; init; }

    /// <summary>
    /// Number of pages in back history.
    /// </summary>
    public int BackHistoryCount { get; init; }

    /// <summary>
    /// Number of pages in forward history.
    /// </summary>
    public int ForwardHistoryCount { get; init; }

    /// <summary>
    /// Timestamp when current page was loaded.
    /// </summary>
    public DateTime LoadedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the current page has readable content available.
    /// </summary>
    public bool HasReadableContent => CurrentPage?.ReadableContent != null;

    /// <summary>
    /// Whether user can navigate back in history.
    /// </summary>
    public bool CanGoBack => BackHistoryCount > 0;

    /// <summary>
    /// Whether user can navigate forward in history.
    /// </summary>
    public bool CanGoForward => ForwardHistoryCount > 0;

    /// <summary>
    /// Current search query, if any.
    /// </summary>
    public string? SearchQuery { get; init; }

    /// <summary>
    /// Index of the current search match (in the list of matching paragraph indices).
    /// </summary>
    public int SearchMatchIndex { get; init; }

    /// <summary>
    /// Transient status message displayed for one render cycle (e.g., theme name after cycling).
    /// </summary>
    public string? StatusMessage { get; init; }

    /// <summary>
    /// Whether the current page was served from the cache.
    /// </summary>
    public bool IsFromCache { get; init; }

    /// <summary>
    /// When the cached version was originally loaded (null if fresh).
    /// </summary>
    public DateTime? CachedAt { get; init; }

    /// <summary>
    /// Whether the current page's link tree was organized by AI hierarchy analysis.
    /// </summary>
    public bool IsAiHierarchy { get; init; }

    /// <summary>
    /// Absolute line index of the reader cursor (for dyslexia reading position indicators).
    /// Separate from ScrollOffset — the cursor moves within and beyond the viewport.
    /// </summary>
    public int ReaderCursorLine { get; init; }

    /// <summary>
    /// Whether speed reading mode is currently active (auto-scrolling line highlighter).
    /// </summary>
    public bool IsSpeedReadActive { get; init; }

    /// <summary>
    /// Current speed reading rate in words per minute.
    /// </summary>
    public int SpeedReadWpm { get; init; } = 250;

    /// <summary>
    /// Whether the layout preview carousel is active.
    /// </summary>
    public bool IsInPreviewMode { get; init; }

    /// <summary>
    /// Label for the current preview layout (e.g., "Layout 1/3 · AI Layout").
    /// Null when not in preview mode.
    /// </summary>
    public string? PreviewLabel { get; init; }

    /// <summary>
    /// Active toast notification to render as an overlay, or null if none.
    /// </summary>
    public ToastNotification? ActiveToast { get; init; }
}
