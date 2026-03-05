// Educational and personal use only.

namespace TermReader.Domain.Entities.Collections;

/// <summary>
/// Aggregate root for a named collection of saved links.
/// Collections allow users to organize bookmarked pages.
/// </summary>
public class Collection
{
    private readonly List<CollectionItem> _items = new();

    /// <summary>
    /// Unique identifier for this collection.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// Display name of the collection.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// Sort order for display (lower values appear first).
    /// </summary>
    public int SortOrder { get; private set; }

    /// <summary>
    /// Timestamp when the collection was created.
    /// </summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Timestamp when the collection was last modified.
    /// </summary>
    public DateTime UpdatedAt { get; private set; }

    /// <summary>
    /// Items saved in this collection.
    /// </summary>
    public IReadOnlyList<CollectionItem> Items => _items.AsReadOnly();

    private Collection(string name, int sortOrder)
    {
        Id = Guid.NewGuid();
        Name = name;
        SortOrder = sortOrder;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    // EF Core constructor
    private Collection()
    {
        Name = string.Empty;
    }

    /// <summary>
    /// Creates a new collection with the given name.
    /// </summary>
    public static Collection Create(string name, int sortOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Collection name cannot be empty", nameof(name));

        return new Collection(name.Trim(), sortOrder);
    }

    /// <summary>
    /// Renames this collection.
    /// </summary>
    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Collection name cannot be empty", nameof(newName));

        Name = newName.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Adds a new item to the collection. Returns the created item.
    /// </summary>
    public CollectionItem AddItem(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL cannot be empty", nameof(url));

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty", nameof(title));

        var nextSortOrder = _items.Count > 0 ? _items.Max(i => i.SortOrder) + 1 : 0;
        var item = CollectionItem.Create(Id, url.Trim(), title.Trim(), nextSortOrder);
        _items.Add(item);
        UpdatedAt = DateTime.UtcNow;
        return item;
    }

    /// <summary>
    /// Removes an item from the collection by its ID.
    /// </summary>
    public void RemoveItem(Guid itemId)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId);
        if (item != null)
        {
            _items.Remove(item);
            UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Moves an item up in the list (earlier position).
    /// </summary>
    public void MoveItemUp(Guid itemId)
    {
        var index = _items.FindIndex(i => i.Id == itemId);
        if (index > 0)
        {
            // Swap sort orders so EF detects the change
            var currentOrder = _items[index].SortOrder;
            _items[index].SetSortOrder(_items[index - 1].SortOrder);
            _items[index - 1].SetSortOrder(currentOrder);

            (_items[index], _items[index - 1]) = (_items[index - 1], _items[index]);
            UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Moves an item down in the list (later position).
    /// </summary>
    public void MoveItemDown(Guid itemId)
    {
        var index = _items.FindIndex(i => i.Id == itemId);
        if (index >= 0 && index < _items.Count - 1)
        {
            // Swap sort orders so EF detects the change
            var currentOrder = _items[index].SortOrder;
            _items[index].SetSortOrder(_items[index + 1].SortOrder);
            _items[index + 1].SetSortOrder(currentOrder);

            (_items[index], _items[index + 1]) = (_items[index + 1], _items[index]);
            UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Removes all items from the collection.
    /// </summary>
    public void Clear()
    {
        _items.Clear();
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Checks if the collection contains a URL (case-insensitive).
    /// </summary>
    public bool ContainsUrl(string url)
    {
        return _items.Any(i => string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase));
    }
}
