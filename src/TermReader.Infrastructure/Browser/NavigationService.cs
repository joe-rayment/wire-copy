// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Manages browser navigation history with back/forward support.
/// Core navigation only - collection and launcher state are delegated
/// to CollectionNavigationState and LauncherNavigationState.
/// </summary>
public class NavigationService : INavigationService
{
    private static readonly TimeSpan StatusMessageDuration = TimeSpan.FromSeconds(3);

    private readonly ILogger<NavigationService> _logger;
    private readonly Stack<Page> _backHistory = new();
    private readonly Stack<Page> _forwardHistory = new();
    private Page? _currentPage;
    private ViewMode _currentViewMode = ViewMode.Hierarchical;
    private int _selectedLinkIndex;
    private int _scrollOffset;
    private string? _searchQuery;
    private int _searchMatchIndex;
    private string? _statusMessage;
    private DateTime? _statusMessageSetAt;
    private bool _isFromCache;
    private DateTime? _cachedAt;

    // Delegated state managers
    private readonly CollectionNavigationState _collectionState;
    private readonly LauncherNavigationState _launcherState;

    public NavigationService(ILogger<NavigationService> logger)
    {
        _logger = logger;
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
        StatusMessage = GetActiveStatusMessage(),
        IsFromCache = _isFromCache,
        CachedAt = _cachedAt
    };

    public Page? CurrentPage => _currentPage;

    public bool CanGoBack => _backHistory.Count > 0;

    public bool CanGoForward => _forwardHistory.Count > 0;

    public int BackHistoryCount => _backHistory.Count;

    public int ForwardHistoryCount => _forwardHistory.Count;

    public void NavigateTo(Page page)
    {
        if (_currentPage != null)
        {
            _backHistory.Push(_currentPage);
            _logger.LogDebug("Pushed {Title} to back history (count: {Count})",
                _currentPage.Metadata.Title,
                _backHistory.Count);
        }

        // Clear forward history when navigating to new page
        _forwardHistory.Clear();

        _currentPage = page;
        _selectedLinkIndex = 0;
        _scrollOffset = 0;
        _currentViewMode = ViewMode.Hierarchical;

        _logger.LogInformation("Navigated to: {Title} ({Url})",
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
            _forwardHistory.Push(_currentPage);
        }

        _currentPage = _backHistory.Pop();
        _selectedLinkIndex = 0;
        _scrollOffset = 0;
        _currentViewMode = ViewMode.Hierarchical;

        _logger.LogInformation("Navigated back to: {Title}",
            _currentPage.Metadata.Title);

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
            _backHistory.Push(_currentPage);
        }

        _currentPage = _forwardHistory.Pop();
        _selectedLinkIndex = 0;
        _scrollOffset = 0;
        _currentViewMode = ViewMode.Hierarchical;

        _logger.LogInformation("Navigated forward to: {Title}",
            _currentPage.Metadata.Title);

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
        foreach (var page in _backHistory.Take(maxItems))
        {
            history.Add($"  {page.Metadata.Title}");
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

    /// <summary>
    /// Sets a status message that auto-expires after a few seconds.
    /// </summary>
    public void SetStatusMessage(string message)
    {
        _statusMessage = message;
        _statusMessageSetAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Clears the status message immediately.
    /// </summary>
    public void ClearStatusMessage()
    {
        _statusMessage = null;
        _statusMessageSetAt = null;
    }

    /// <summary>
    /// Sets cache metadata for the current page (shown in status bar).
    /// </summary>
    public void SetCacheInfo(bool isFromCache, DateTime? cachedAt)
    {
        _isFromCache = isFromCache;
        _cachedAt = cachedAt;
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
            _currentPage = _backHistory.Pop();
        }

        return true;
    }

    private string? GetActiveStatusMessage()
    {
        if (_statusMessage == null || _statusMessageSetAt == null)
        {
            return null;
        }

        if (DateTime.UtcNow - _statusMessageSetAt.Value > StatusMessageDuration)
        {
            _statusMessage = null;
            _statusMessageSetAt = null;
            return null;
        }

        return _statusMessage;
    }
}
