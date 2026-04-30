// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Entities.Bookmarks;

/// <summary>
/// Represents a persistent bookmark displayed as a tile on the launcher home screen.
/// </summary>
public class Bookmark
{
    public Guid Id { get; private set; }

    /// <summary>
    /// Display name shown large on the launcher tile.
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// URL to navigate to when the bookmark is activated.
    /// </summary>
    public string Url { get; private set; }

    /// <summary>
    /// Sort order for grid position (lower values appear first).
    /// </summary>
    public int SortOrder { get; private set; }

    /// <summary>
    /// Timestamp when the bookmark was created.
    /// </summary>
    public DateTime CreatedAt { get; private set; }

    private Bookmark(string name, string url, int sortOrder)
    {
        Id = Guid.NewGuid();
        Name = name;
        Url = url;
        SortOrder = sortOrder;
        CreatedAt = DateTime.UtcNow;
    }

    // EF Core constructor
    private Bookmark()
    {
        Name = string.Empty;
        Url = string.Empty;
    }

    /// <summary>
    /// Creates a new bookmark with the given name and URL.
    /// </summary>
    public static Bookmark Create(string name, string url, int sortOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Bookmark name cannot be empty", nameof(name));

        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Bookmark URL cannot be empty", nameof(url));

        return new Bookmark(name.Trim(), url.Trim(), sortOrder);
    }

    /// <summary>
    /// Renames this bookmark.
    /// </summary>
    public void Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Bookmark name cannot be empty", nameof(newName));

        Name = newName.Trim();
    }

    /// <summary>
    /// Updates the bookmark URL.
    /// </summary>
    public void UpdateUrl(string newUrl)
    {
        if (string.IsNullOrWhiteSpace(newUrl))
            throw new ArgumentException("Bookmark URL cannot be empty", nameof(newUrl));

        Url = newUrl.Trim();
    }

    /// <summary>
    /// Sets the sort order for grid positioning.
    /// </summary>
    public void SetSortOrder(int order)
    {
        SortOrder = order;
    }
}
