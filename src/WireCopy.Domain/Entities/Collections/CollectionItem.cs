// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Entities.Collections;

/// <summary>
/// Represents a saved link within a collection.
/// </summary>
public class CollectionItem
{
    /// <summary>
    /// Unique identifier for this item.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// ID of the parent collection.
    /// </summary>
    public Guid CollectionId { get; private set; }

    /// <summary>
    /// URL of the saved page.
    /// </summary>
    public string Url { get; private set; }

    /// <summary>
    /// Display title of the saved page.
    /// </summary>
    public string Title { get; private set; }

    /// <summary>
    /// Timestamp when this item was saved.
    /// </summary>
    public DateTime SavedAt { get; private set; }

    /// <summary>
    /// Whether this item has been read/visited.
    /// </summary>
    public bool IsRead { get; private set; }

    /// <summary>
    /// Sort order for display (lower values appear first).
    /// </summary>
    public int SortOrder { get; private set; }

    private CollectionItem(Guid collectionId, string url, string title, int sortOrder)
    {
        Id = Guid.NewGuid();
        CollectionId = collectionId;
        Url = url;
        Title = title;
        SavedAt = DateTime.UtcNow;
        IsRead = false;
        SortOrder = sortOrder;
    }

    // EF Core constructor
    private CollectionItem()
    {
        Url = string.Empty;
        Title = string.Empty;
    }

    /// <summary>
    /// Creates a new collection item.
    /// </summary>
    public static CollectionItem Create(Guid collectionId, string url, string title, int sortOrder = 0)
    {
        if (collectionId == Guid.Empty)
            throw new ArgumentException("Collection ID cannot be empty", nameof(collectionId));

        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL cannot be empty", nameof(url));

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty", nameof(title));

        return new CollectionItem(collectionId, url, title, sortOrder);
    }

    /// <summary>
    /// Sets the sort order for this item.
    /// </summary>
    internal void SetSortOrder(int sortOrder)
    {
        SortOrder = sortOrder;
    }

    /// <summary>
    /// Marks this item as read.
    /// </summary>
    public void MarkAsRead()
    {
        IsRead = true;
    }
}
