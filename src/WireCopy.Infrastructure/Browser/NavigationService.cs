// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Manages browser navigation history with back/forward support.
/// Core navigation only - collection and launcher state are delegated
/// to CollectionNavigationState and LauncherNavigationState.
/// </summary>
public class NavigationService : INavigationService
{
    private static readonly TimeSpan StatusMessageDuration = TimeSpan.FromSeconds(3);

    private readonly ILogger<NavigationService> _logger;
    private readonly Stack<HistoryEntry> _backHistory = new();
    private readonly Stack<HistoryEntry> _forwardHistory = new();
    private Page? _currentPage;
    private ViewMode _currentViewMode = ViewMode.Hierarchical;
    private int _selectedLinkIndex;
    private int _scrollOffset;
    private string? _searchQuery;
    private int _searchMatchIndex;
    private readonly Dictionary<string, ActivityIndicator> _activities = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _activityLock = new();
    private StatusAnnouncement? _announcement;
    private DateTime? _announcementSetAt;
    private TimeSpan _announcementTtl = StatusMessageDuration;
    private readonly TimeProvider _clock;
    private bool _isFromCache;
    private DateTime? _cachedAt;
    private bool _isAiHierarchy;
    private int _readerCursorLine;
    private bool _speedReadActive;
    private int _speedReadWpm = 750;
    private ToastNotification? _activeToast;

    // Layout preview state
    private List<LayoutCandidate>? _previewLayouts;
    private int _previewIndex;
    private bool _isInPreviewMode;
    private NavigationTree? _originalTree;

    // Delegated state managers
    private readonly CollectionNavigationState _collectionState;
    private readonly LauncherNavigationState _launcherState;

    public NavigationService(ILogger<NavigationService> logger, TimeProvider? clock = null)
    {
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _collectionState = new CollectionNavigationState(logger);
        _launcherState = new LauncherNavigationState(logger);
    }

    /// <summary>
    /// Gets the collection navigation state manager.
    /// </summary>
    public CollectionNavigationState CollectionState => _collectionState;

    /// <summary>
    /// Gets the launcher navigation state manager.
    /// </summary>
    public LauncherNavigationState LauncherState => _launcherState;

    public NavigationContext CurrentContext => new()
    {
        CurrentPage = _currentPage,
        ViewMode = _currentViewMode,
        SelectedLinkIndex = _selectedLinkIndex,
        ScrollOffset = _scrollOffset,
        BackHistoryCount = _backHistory.Count,
        ForwardHistoryCount = _forwardHistory.Count,
        LoadedAt = _currentPage?.LoadedAt ?? DateTime.UtcNow,
        SearchQuery = _searchQuery,
        SearchMatchIndex = _searchMatchIndex,
        StatusMessage = GetActiveAnnouncement()?.Text,
        ActiveAnnouncement = GetActiveAnnouncement(),
        ActiveActivity = GetTopActivity(),
        IsFromCache = _isFromCache,
        CachedAt = _cachedAt,
        IsAiHierarchy = _isAiHierarchy,
        ReaderCursorLine = _readerCursorLine,
        IsSpeedReadActive = _speedReadActive,
        SpeedReadWpm = _speedReadWpm,
        IsInPreviewMode = _isInPreviewMode,
        PreviewLabel = _isInPreviewMode ? GetCurrentPreviewLabel() : null,
        ActiveToast = _activeToast,
    };

    public Page? CurrentPage => _currentPage;

    public bool CanGoBack => _backHistory.Count > 0;

    public bool CanGoForward => _forwardHistory.Count > 0;

    public int BackHistoryCount => _backHistory.Count;

    public int ForwardHistoryCount => _forwardHistory.Count;

    public int ReaderCursorLine => _readerCursorLine;

    public bool IsSpeedReadActive => _speedReadActive;

    public int SpeedReadWpm => _speedReadWpm;

    /// <summary>
    /// Whether the layout preview carousel is currently active.
    /// </summary>
    public bool IsInPreviewMode => _isInPreviewMode;

    public void NavigateTo(Page page)
    {
        if (_currentPage != null)
        {
            _backHistory.Push(new HistoryEntry(_currentPage, _currentViewMode));
            _logger.LogDebug("Pushed {Title} to back history (count: {Count})",
                _currentPage.Metadata.Title,
                _backHistory.Count);
        }

        // Clear forward history when navigating to new page
        _forwardHistory.Clear();

        _currentPage = page;
        _selectedLinkIndex = 0;
        _scrollOffset = 0;
        _readerCursorLine = 0;
        _speedReadActive = false;
        _currentViewMode = ViewMode.Hierarchical;

        _logger.LogInformation("Navigated to: {Title} ({Url})",
            page.Metadata.Title,
            page.Url);
    }

    /// <summary>
    /// Replaces the current page in-place without altering history, view mode,
    /// or scroll position. Used for refresh operations where the user should
    /// stay in the same view they were in before the refresh.
    /// </summary>
    public void ReplaceCurrent(Page page)
    {
        _currentPage = page;

        _logger.LogInformation("Replaced current page: {Title} ({Url})",
            page.Metadata.Title,
            page.Url);
    }

    public Page? GoBack()
    {
        if (!CanGoBack)
        {
            _logger.LogDebug("Cannot go back - at start of history");
            return null;
        }

        if (_currentPage != null)
        {
            _forwardHistory.Push(new HistoryEntry(_currentPage, _currentViewMode));
        }

        var entry = _backHistory.Pop();
        _currentPage = entry.Page;
        _selectedLinkIndex = 0;
        _scrollOffset = 0;
        _readerCursorLine = 0;
        _speedReadActive = false;
        _currentViewMode = entry.ViewMode;

        _logger.LogInformation("Navigated back to: {Title} (view: {ViewMode})",
            _currentPage.Metadata.Title,
            _currentViewMode);

        return _currentPage;
    }

    public Page? GoForward()
    {
        if (!CanGoForward)
        {
            _logger.LogDebug("Cannot go forward - at end of history");
            return null;
        }

        if (_currentPage != null)
        {
            _backHistory.Push(new HistoryEntry(_currentPage, _currentViewMode));
        }

        var entry = _forwardHistory.Pop();
        _currentPage = entry.Page;
        _selectedLinkIndex = 0;
        _scrollOffset = 0;
        _readerCursorLine = 0;
        _speedReadActive = false;
        _currentViewMode = entry.ViewMode;

        _logger.LogInformation("Navigated forward to: {Title} (view: {ViewMode})",
            _currentPage.Metadata.Title,
            _currentViewMode);

        return _currentPage;
    }

    public IEnumerable<string> GetHistoryTitles(int maxItems = 10)
    {
        var history = new List<string>();

        // Add current page
        if (_currentPage != null)
        {
            history.Add($"\u2192 {_currentPage.Metadata.Title} (current)");
        }

        // Add back history
        foreach (var entry in _backHistory.Take(maxItems))
        {
            history.Add($"  {entry.Page.Metadata.Title}");
        }

        return history;
    }

    public void ClearHistory()
    {
        _backHistory.Clear();
        _forwardHistory.Clear();
        _currentPage = null;
        _selectedLinkIndex = 0;
        _scrollOffset = 0;
        _readerCursorLine = 0;
        _speedReadActive = false;

        _logger.LogInformation("Navigation history cleared");
    }

    /// <summary>
    /// Updates the selected link index in the current context.
    /// </summary>
    public void SetSelectedIndex(int index)
    {
        _selectedLinkIndex = Math.Max(0, index);
    }

    /// <summary>
    /// Updates the scroll offset in the current context.
    /// </summary>
    public void SetScrollOffset(int offset)
    {
        _scrollOffset = Math.Max(0, offset);
    }

    public void SetReaderCursorLine(int line)
    {
        _readerCursorLine = Math.Max(0, line);
    }

    /// <summary>
    /// workspace-wef6.4: announces a state change into the status line's
    /// Transient channel — glyph + copy + the keys that control the new state
    /// ("▶ Speed reading 350 WPM — &lt;:slower &gt;:faster f:stop"). The
    /// announcement survives every re-render until its TTL elapses
    /// (clock-based; the render-count toast bug class cannot recur here).
    /// </summary>
    /// <param name="glyph">Leading glyph from the app's glyph language (⏸ ▶ ✓ ⚡ ⇉), or null.</param>
    /// <param name="text">The announcement copy.</param>
    /// <param name="keys">Keys controlling the announced state, or null.</param>
    /// <param name="ttl">Time to live; defaults to 3 seconds.</param>
    /// <param name="shortText">Compact fallback for squeezed lines (e.g. "▶350").</param>
    public void Announce(
        string? glyph,
        string text,
        IReadOnlyList<StatusKeyHint>? keys = null,
        TimeSpan? ttl = null,
        string? shortText = null)
    {
        _announcement = new StatusAnnouncement
        {
            Glyph = glyph,
            Text = text,
            Keys = keys ?? Array.Empty<StatusKeyHint>(),
            ShortText = shortText,
        };
        _announcementSetAt = _clock.GetUtcNow().UtcDateTime;
        _announcementTtl = ttl ?? StatusMessageDuration;
    }

    /// <summary>
    /// workspace-wef6.5: registers (or updates) a producer in the unified
    /// activity slot. The highest-priority live entry renders as the single
    /// animated "is it working" indicator. Call <see cref="ClearActivity"/>
    /// when the work finishes — activity is stateful, not TTL'd.
    /// </summary>
    /// <param name="source">Producer identity ("load", "ai", "podcast"); one live entry per source.</param>
    /// <param name="text">Indicator copy.</param>
    /// <param name="priority">Slot priority: load 0 &gt; AI 1 &gt; podcast 2 (prefetch is the derived 3).</param>
    /// <param name="percent">Optional completion percent.</param>
    public void SetActivity(string source, string text, int priority = 0, int? percent = null)
    {
        lock (_activityLock)
        {
            _activities[source] = new ActivityIndicator
            {
                Source = source,
                Text = text,
                Priority = priority,
                Percent = percent,
            };
        }
    }

    /// <summary>Removes a producer's entry from the activity slot.</summary>
    public void ClearActivity(string source)
    {
        lock (_activityLock)
        {
            _activities.Remove(source);
        }
    }

    /// <summary>
    /// Sets a status message that auto-expires after a few seconds.
    /// workspace-wef6.4: shim over <see cref="Announce"/> — the ~200 existing
    /// call sites keep working and route into the Transient channel.
    /// </summary>
    public void SetStatusMessage(string message)
    {
        Announce(glyph: null, text: message);
    }

    /// <summary>
    /// Sets a status message with a custom expiry duration.
    /// </summary>
    public void SetStatusMessage(string message, TimeSpan duration)
    {
        Announce(glyph: null, text: message, ttl: duration);
    }

    /// <summary>
    /// Clears the status message / announcement immediately.
    /// </summary>
    public void ClearStatusMessage()
    {
        _announcement = null;
        _announcementSetAt = null;
    }

    /// <summary>
    /// Shows a toast notification. Replaces any existing toast.
    /// </summary>
    public void ShowToast(ToastType type, string message, string? detail = null)
    {
        _activeToast = new ToastNotification
        {
            Type = type,
            Message = message,
            Detail = detail,
        };
    }

    /// <summary>
    /// Clears the active toast immediately.
    /// </summary>
    public void ClearToast()
    {
        _activeToast = null;
    }

    /// <summary>
    /// Marks the active toast as rendered. Non-sticky toasts are cleared on the next call.
    /// Call this after rendering the toast so auto-dismiss works on the next render pass.
    /// </summary>
    public void MarkToastRendered()
    {
        if (_activeToast == null)
        {
            return;
        }

        if (_activeToast.HasBeenRendered && !_activeToast.IsSticky)
        {
            // Non-sticky toast was already shown once — auto-dismiss
            _activeToast = null;
        }
        else
        {
            _activeToast = _activeToast with { HasBeenRendered = true };
        }
    }

    /// <summary>
    /// Sets cache metadata for the current page (shown in status bar).
    /// </summary>
    public void SetCacheInfo(bool isFromCache, DateTime? cachedAt)
    {
        _isFromCache = isFromCache;
        _cachedAt = cachedAt;
    }

    public void SetAiHierarchy(bool isAiHierarchy)
    {
        _isAiHierarchy = isAiHierarchy;
    }

    /// <summary>
    /// Enters layout preview mode with pre-computed layout candidates.
    /// Saves the current tree so it can be restored on cancel.
    /// </summary>
    public void EnterPreviewMode(List<LayoutCandidate> layouts)
    {
        if (layouts.Count == 0 || _currentPage == null)
        {
            return;
        }

        _previewLayouts = layouts;
        _previewIndex = 0;
        _isInPreviewMode = true;
        _originalTree = _currentPage.LinkTree;

        // Apply first preview
        _currentPage.SetLinkTree(layouts[0].PreviewTree);
        _selectedLinkIndex = 0;
        _scrollOffset = 0;

        _logger.LogInformation("Entered layout preview mode with {Count} candidate(s)", layouts.Count);
    }

    /// <summary>
    /// Cycles to the next or previous layout preview.
    /// </summary>
    /// <param name="direction">+1 for next, -1 for previous.</param>
    public void CyclePreview(int direction)
    {
        if (!_isInPreviewMode || _previewLayouts == null || _currentPage == null)
        {
            return;
        }

        _previewIndex = (((_previewIndex + direction) % _previewLayouts.Count) + _previewLayouts.Count) % _previewLayouts.Count;
        _currentPage.SetLinkTree(_previewLayouts[_previewIndex].PreviewTree);
        _selectedLinkIndex = 0;
        _scrollOffset = 0;
    }

    /// <summary>
    /// Applies the currently previewed layout and exits preview mode.
    /// Returns the selected candidate's config for saving.
    /// </summary>
    public LayoutCandidate? ApplyPreview()
    {
        if (!_isInPreviewMode || _previewLayouts == null)
        {
            return null;
        }

        var selected = _previewLayouts[_previewIndex];
        ExitPreviewMode();
        _logger.LogInformation("Applied layout: {Summary}", selected.Summary);
        return selected;
    }

    /// <summary>
    /// Cancels preview mode and restores the original layout.
    /// </summary>
    public void CancelPreview()
    {
        if (!_isInPreviewMode || _currentPage == null)
        {
            return;
        }

        if (_originalTree != null)
        {
            _currentPage.SetLinkTree(_originalTree);
        }

        _selectedLinkIndex = 0;
        _scrollOffset = 0;
        ExitPreviewMode();
        _logger.LogInformation("Cancelled layout preview");
    }

    /// <summary>
    /// Gets the current preview label (e.g., "Layout 1/3 · AI Layout").
    /// </summary>
    public string? GetCurrentPreviewLabel()
    {
        if (!_isInPreviewMode || _previewLayouts == null)
        {
            return null;
        }

        var current = _previewLayouts[_previewIndex];
        return $"Layout {_previewIndex + 1}/{_previewLayouts.Count} · {current.Summary}";
    }

    /// <summary>
    /// workspace-z5qz: exposes the currently-previewed candidate to the
    /// orchestrator's preview-mode dispatch so it can vary keybinding
    /// behaviour per strategy (e.g. <c>i:guidance</c> only fires when AI
    /// Curated is selected).
    /// </summary>
    public LayoutCandidate? GetCurrentPreviewCandidate()
    {
        if (!_isInPreviewMode || _previewLayouts == null || _previewLayouts.Count == 0)
        {
            return null;
        }

        return _previewLayouts[_previewIndex];
    }

    /// <summary>
    /// workspace-ujxu: index of the candidate currently being previewed
    /// (zero-based). -1 when not in preview mode. Used by the anchored
    /// chooser overlay to highlight the active row alongside each page
    /// re-render.
    /// </summary>
    public int GetCurrentPreviewIndex()
    {
        if (!_isInPreviewMode || _previewLayouts == null || _previewLayouts.Count == 0)
        {
            return -1;
        }

        return _previewIndex;
    }

    /// <summary>
    /// Toggles between Hierarchical and Readable view modes.
    /// Resets scroll offset to 0 when switching views.
    /// </summary>
    public void ToggleViewMode()
    {
        _currentViewMode = _currentViewMode == ViewMode.Hierarchical
            ? ViewMode.Readable
            : ViewMode.Hierarchical;

        // Reset scroll offset when switching views so content starts at top
        _scrollOffset = 0;
        _readerCursorLine = 0;
        _speedReadActive = false;

        _logger.LogDebug("Switched to {Mode} view", _currentViewMode);
    }

    /// <summary>
    /// Sets a specific view mode.
    /// Resets scroll offset to 0 when switching views.
    /// </summary>
    public void SetViewMode(ViewMode mode)
    {
        _currentViewMode = mode;

        // Reset scroll offset when switching views so content starts at top
        _scrollOffset = 0;
        _readerCursorLine = 0;
        _speedReadActive = false;

        _logger.LogDebug("Set view mode to {Mode}", mode);
    }

    /// <summary>
    /// Sets the current search query and resets match index.
    /// </summary>
    public void SetSearchQuery(string? query)
    {
        _searchQuery = query;
        _searchMatchIndex = 0;
    }

    /// <summary>
    /// Sets the search match index.
    /// </summary>
    public void SetSearchMatchIndex(int index)
    {
        _searchMatchIndex = Math.Max(0, index);
    }

    /// <summary>
    /// Starts speed reading mode at the current WPM rate.
    /// </summary>
    public void StartSpeedRead()
    {
        _speedReadActive = true;
        _logger.LogDebug("Speed reading started at {Wpm} WPM", _speedReadWpm);
    }

    /// <summary>
    /// Stops speed reading mode.
    /// </summary>
    public void StopSpeedRead()
    {
        _speedReadActive = false;
        _logger.LogDebug("Speed reading stopped");
    }

    /// <summary>
    /// Adjusts the speed reading WPM by the given delta (positive = faster, negative = slower).
    /// Clamps to a minimum of 50 WPM and maximum of 1000 WPM.
    /// </summary>
    public void AdjustSpeedReadWpm(int delta)
    {
        _speedReadWpm = Math.Clamp(_speedReadWpm + delta, 50, 1000);
        _logger.LogDebug("Speed reading WPM adjusted to {Wpm}", _speedReadWpm);
    }

#pragma warning disable SA1201 // Delegating properties intentionally grouped after core methods
    // Delegating properties and methods for backward compatibility

    /// <summary>
    /// Gets the currently active collection (for CollectionItems view).
    /// </summary>
    public Collection? ActiveCollection => _collectionState.ActiveCollection;

    /// <summary>
    /// Gets whether we're currently in collections mode.
    /// </summary>
    public bool InCollectionsMode => _collectionState.InCollectionsMode;

    /// <summary>
    /// Gets whether there is a saved collection return point (article opened from collection).
    /// </summary>
    public bool HasCollectionReturnPoint => _collectionState.HasReturnPoint;

    /// <summary>
    /// Gets or sets the selected index in the collection list.
    /// </summary>
    public int CollectionSelectedIndex
    {
        get => _collectionState.CollectionSelectedIndex;
        set => _collectionState.CollectionSelectedIndex = value;
    }

    /// <summary>
    /// Gets or sets the selected index in the collection items list.
    /// </summary>
    public int CollectionItemSelectedIndex
    {
        get => _collectionState.CollectionItemSelectedIndex;
        set => _collectionState.CollectionItemSelectedIndex = value;
    }

    /// <summary>
    /// Gets or sets the scroll offset for the collection list.
    /// </summary>
    public int CollectionListScrollOffset
    {
        get => _collectionState.CollectionListScrollOffset;
        set => _collectionState.CollectionListScrollOffset = value;
    }

    /// <summary>
    /// Gets or sets the scroll offset for collection items.
    /// </summary>
    public int CollectionItemScrollOffset
    {
        get => _collectionState.CollectionItemScrollOffset;
        set => _collectionState.CollectionItemScrollOffset = value;
    }

    // Launcher properties and methods

    /// <summary>
    /// Gets or sets the selected index on the launcher grid.
    /// </summary>
    public int LauncherSelectedIndex
    {
        get => _launcherState.SelectedIndex;
        set => _launcherState.SelectedIndex = value;
    }

    /// <summary>
    /// Gets or sets the scroll offset for the launcher grid.
    /// </summary>
    public int LauncherScrollOffset
    {
        get => _launcherState.ScrollOffset;
        set => _launcherState.ScrollOffset = value;
    }

    /// <summary>
    /// Gets whether the browser is currently in launcher mode.
    /// </summary>
    public bool InLauncherMode => _currentViewMode == ViewMode.Launcher;
#pragma warning restore SA1201

    /// <summary>
    /// Computes a new index for 2D grid navigation.
    /// </summary>
    public static int MoveInGrid(int currentIndex, int totalItems, int direction, int columns = 2)
    {
        return LauncherNavigationState.MoveInGrid(currentIndex, totalItems, direction, columns);
    }

    /// <summary>
    /// Enters collections mode, saving the current view state.
    /// </summary>
    public void EnterCollections()
    {
        _collectionState.EnterCollections(_currentViewMode, _scrollOffset);
        _currentViewMode = ViewMode.CollectionList;
    }

    /// <summary>
    /// Opens a specific collection's items view.
    /// </summary>
    public void EnterCollection(Collection collection)
    {
        _collectionState.EnterCollection(collection);
        _currentViewMode = ViewMode.CollectionItems;
    }

    /// <summary>
    /// Updates the active collection reference without resetting scroll/index state.
    /// Used during refresh to swap in the updated collection object while preserving UI position.
    /// </summary>
    public void UpdateActiveCollection(Collection collection)
    {
        _collectionState.UpdateActiveCollection(collection);
    }

    /// <summary>
    /// Exits from CollectionItems back to CollectionList.
    /// </summary>
    public void ExitToCollectionList()
    {
        _collectionState.ExitToCollectionList();
        _currentViewMode = ViewMode.CollectionList;
    }

    /// <summary>
    /// Exits collections mode entirely, restoring the previous view state.
    /// </summary>
    public void ExitCollections()
    {
        var preViewMode = _collectionState.PreCollectionsViewMode;
        var preScrollOffset = _collectionState.PreCollectionsScrollOffset;
        _collectionState.ExitCollections();
        _currentViewMode = preViewMode;
        _scrollOffset = preScrollOffset;
    }

    /// <summary>
    /// Saves the current collection state as a return point before navigating to an article.
    /// </summary>
    public void SaveCollectionReturnPoint()
    {
        _collectionState.SaveCollectionReturnPoint();
    }

    /// <summary>
    /// Enters launcher mode, showing the bookmark grid.
    /// </summary>
    public void EnterLauncher()
    {
        _currentViewMode = ViewMode.Launcher;
        _launcherState.Enter();
    }

    /// <summary>
    /// Checks if there is a collection return point and returns to it.
    /// Returns true if a return point was restored, false otherwise.
    /// </summary>
    public bool TryRestoreCollectionReturnPoint()
    {
        var returnData = _collectionState.TryRestoreReturnPoint();
        if (returnData == null)
        {
            return false;
        }

        _currentViewMode = ViewMode.CollectionItems;

        // Pop back history to get back to the previous page
        if (_backHistory.Count > 0)
        {
            var entry = _backHistory.Pop();
            _currentPage = entry.Page;
        }

        return true;
    }

    private void ExitPreviewMode()
    {
        _isInPreviewMode = false;
        _previewLayouts = null;
        _previewIndex = 0;
        _originalTree = null;
    }

    private ActivityIndicator? GetTopActivity()
    {
        lock (_activityLock)
        {
            return _activities.Count == 0
                ? null
                : _activities.Values.OrderBy(a => a.Priority).ThenBy(a => a.Source, StringComparer.Ordinal).First();
        }
    }

    private StatusAnnouncement? GetActiveAnnouncement()
    {
        if (_announcement == null || _announcementSetAt == null)
        {
            return null;
        }

        if (_clock.GetUtcNow().UtcDateTime - _announcementSetAt.Value > _announcementTtl)
        {
            _announcement = null;
            _announcementSetAt = null;
            return null;
        }

        return _announcement;
    }

    private record struct HistoryEntry(Page Page, ViewMode ViewMode);
}
