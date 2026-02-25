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
/// </summary>
public class NavigationService : INavigationService
{
    private readonly ILogger<NavigationService> _logger;
    private readonly Stack<Page> _backHistory = new();
    private readonly Stack<Page> _forwardHistory = new();
    private Page? _currentPage;
    private ViewMode _currentViewMode = ViewMode.Hierarchical;
    private int _selectedLinkIndex;
    private int _scrollOffset;
    private string? _searchQuery;
    private int _searchMatchIndex;

    // Collection state
    private Collection? _activeCollection;
    private bool _inCollectionsMode;
    private ViewMode _preCollectionsViewMode;
    private int _preCollectionsScrollOffset;
    private int _collectionScrollOffset;
    private int _collectionItemScrollOffset;
    private int _collectionSelectedIndex;
    private int _collectionItemSelectedIndex;

    // Return point for navigating back from an article opened from a collection
    private CollectionReturnPoint? _collectionReturnPoint;

    private record CollectionReturnPoint(
        Collection Collection,
        int ItemScrollOffset,
        int ItemSelectedIndex);

    public NavigationService(ILogger<NavigationService> logger)
    {
        _logger = logger;
    }

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
        SearchMatchIndex = _searchMatchIndex
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
            history.Add($"→ {_currentPage.Metadata.Title} (current)");
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

    // Collection navigation methods

    /// <summary>
    /// Gets the currently active collection (for CollectionItems view).
    /// </summary>
    public Collection? ActiveCollection => _activeCollection;

    /// <summary>
    /// Gets whether we're currently in collections mode.
    /// </summary>
    public bool InCollectionsMode => _inCollectionsMode;

    /// <summary>
    /// Gets or sets the selected index in the collection list.
    /// </summary>
    public int CollectionSelectedIndex
    {
        get => _collectionSelectedIndex;
        set => _collectionSelectedIndex = Math.Max(0, value);
    }

    /// <summary>
    /// Gets or sets the selected index in the collection items list.
    /// </summary>
    public int CollectionItemSelectedIndex
    {
        get => _collectionItemSelectedIndex;
        set => _collectionItemSelectedIndex = Math.Max(0, value);
    }

    /// <summary>
    /// Enters collections mode, saving the current view state.
    /// </summary>
    public void EnterCollections()
    {
        _preCollectionsViewMode = _currentViewMode;
        _preCollectionsScrollOffset = _scrollOffset;
        _inCollectionsMode = true;
        _currentViewMode = ViewMode.CollectionList;
        _collectionSelectedIndex = 0;
        _collectionScrollOffset = 0;

        _logger.LogDebug("Entered collections mode");
    }

    /// <summary>
    /// Opens a specific collection's items view.
    /// </summary>
    public void EnterCollection(Collection collection)
    {
        _activeCollection = collection;
        _currentViewMode = ViewMode.CollectionItems;
        _collectionItemSelectedIndex = 0;
        _collectionItemScrollOffset = 0;

        _logger.LogDebug("Entered collection: {Name}", collection.Name);
    }

    /// <summary>
    /// Exits from CollectionItems back to CollectionList.
    /// </summary>
    public void ExitToCollectionList()
    {
        _activeCollection = null;
        _currentViewMode = ViewMode.CollectionList;

        _logger.LogDebug("Returned to collection list");
    }

    /// <summary>
    /// Exits collections mode entirely, restoring the previous view state.
    /// </summary>
    public void ExitCollections()
    {
        _inCollectionsMode = false;
        _activeCollection = null;
        _currentViewMode = _preCollectionsViewMode;
        _scrollOffset = _preCollectionsScrollOffset;

        _logger.LogDebug("Exited collections mode");
    }

    /// <summary>
    /// Saves the current collection state as a return point before navigating to an article.
    /// </summary>
    public void SaveCollectionReturnPoint()
    {
        if (_activeCollection != null)
        {
            _collectionReturnPoint = new CollectionReturnPoint(
                _activeCollection,
                _collectionItemScrollOffset,
                _collectionItemSelectedIndex);
        }
    }

    /// <summary>
    /// Checks if there is a collection return point and returns to it.
    /// Returns true if a return point was restored, false otherwise.
    /// </summary>
    public bool TryRestoreCollectionReturnPoint()
    {
        if (_collectionReturnPoint == null)
        {
            return false;
        }

        _inCollectionsMode = true;
        _activeCollection = _collectionReturnPoint.Collection;
        _currentViewMode = ViewMode.CollectionItems;
        _collectionItemScrollOffset = _collectionReturnPoint.ItemScrollOffset;
        _collectionItemSelectedIndex = _collectionReturnPoint.ItemSelectedIndex;
        _collectionReturnPoint = null;

        // Pop back history to get back to the previous page
        if (_backHistory.Count > 0)
        {
            _currentPage = _backHistory.Pop();
        }

        _logger.LogDebug("Restored collection return point: {Name}", _activeCollection?.Name);
        return true;
    }
}
