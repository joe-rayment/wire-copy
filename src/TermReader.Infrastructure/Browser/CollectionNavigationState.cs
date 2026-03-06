// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Manages collection-specific navigation state (active collection, selection indices, scroll offsets).
/// Extracted from NavigationService to separate collection concerns from core navigation.
/// </summary>
public class CollectionNavigationState
{
    private readonly ILogger _logger;

    private Collection? _activeCollection;
    private bool _inCollectionsMode;
    private ViewMode _preCollectionsViewMode;
    private int _preCollectionsScrollOffset;
    private int _collectionListScrollOffset;
    private int _collectionItemScrollOffset;
    private int _collectionSelectedIndex;
    private int _collectionItemSelectedIndex;

    private CollectionReturnPoint? _collectionReturnPoint;

    public CollectionNavigationState(ILogger logger)
    {
        _logger = logger;
    }

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
    /// Gets or sets the scroll offset for the collection list.
    /// </summary>
    public int CollectionListScrollOffset
    {
        get => _collectionListScrollOffset;
        set => _collectionListScrollOffset = Math.Max(0, value);
    }

    /// <summary>
    /// Gets or sets the scroll offset for collection items.
    /// </summary>
    public int CollectionItemScrollOffset
    {
        get => _collectionItemScrollOffset;
        set => _collectionItemScrollOffset = Math.Max(0, value);
    }

    /// <summary>
    /// Gets the view mode that was active before entering collections.
    /// </summary>
    public ViewMode PreCollectionsViewMode => _preCollectionsViewMode;

    /// <summary>
    /// Gets the scroll offset that was active before entering collections.
    /// </summary>
    public int PreCollectionsScrollOffset => _preCollectionsScrollOffset;

    /// <summary>
    /// Enters collections mode, saving the current view state.
    /// </summary>
    public void EnterCollections(ViewMode currentViewMode, int currentScrollOffset)
    {
        _preCollectionsViewMode = currentViewMode;
        _preCollectionsScrollOffset = currentScrollOffset;
        _inCollectionsMode = true;
        _collectionSelectedIndex = 0;
        _collectionListScrollOffset = 0;

        _logger.LogDebug("Entered collections mode");
    }

    /// <summary>
    /// Opens a specific collection's items view.
    /// </summary>
    public void EnterCollection(Collection collection)
    {
        _activeCollection = collection;
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

        _logger.LogDebug("Returned to collection list");
    }

    /// <summary>
    /// Exits collections mode entirely.
    /// </summary>
    public void ExitCollections()
    {
        _inCollectionsMode = false;
        _activeCollection = null;

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
    /// Returns the collection return data, or null if no return point exists.
    /// </summary>
    public (Collection Collection, int ItemScrollOffset, int ItemSelectedIndex)? TryRestoreReturnPoint()
    {
        if (_collectionReturnPoint == null)
        {
            return null;
        }

        _inCollectionsMode = true;
        _activeCollection = _collectionReturnPoint.Collection;
        _collectionItemScrollOffset = _collectionReturnPoint.ItemScrollOffset;
        _collectionItemSelectedIndex = _collectionReturnPoint.ItemSelectedIndex;
        _collectionReturnPoint = null;

        _logger.LogDebug("Restored collection return point: {Name}", _activeCollection?.Name);
        return (_activeCollection!, _collectionItemScrollOffset, _collectionItemSelectedIndex);
    }

    private sealed record CollectionReturnPoint(
        Collection Collection,
        int ItemScrollOffset,
        int ItemSelectedIndex);
}
