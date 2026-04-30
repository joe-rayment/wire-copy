// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Collections;

namespace WireCopy.Application.Interfaces;

/// <summary>
/// Application service for managing collections and saved links.
/// </summary>
public interface ICollectionService
{
    /// <summary>
    /// Saves a URL to the default collection. Skips if URL already exists.
    /// </summary>
    Task<CollectionItem?> SaveToDefaultCollectionAsync(string url, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a URL to a named collection (creates collection if not found). Skips if URL already exists.
    /// </summary>
    Task<CollectionItem?> SaveToCollectionByNameAsync(string collectionName, string url, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all collections.
    /// </summary>
    Task<IReadOnlyList<Collection>> GetAllCollectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new collection with the given name.
    /// </summary>
    Task<Collection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames an existing collection.
    /// </summary>
    Task RenameCollectionAsync(Guid collectionId, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection and all its items.
    /// </summary>
    Task DeleteCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all items from a collection.
    /// </summary>
    Task ClearCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a single item from its collection.
    /// </summary>
    Task RemoveItemAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an item up in its collection.
    /// </summary>
    Task MoveItemUpAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an item down in its collection.
    /// </summary>
    Task MoveItemDownAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an item as read.
    /// </summary>
    Task MarkItemAsReadAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the default collection for quick save operations.
    /// </summary>
    Task SetDefaultCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current default collection.
    /// </summary>
    Task<Collection> GetDefaultCollectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets or creates the Reading List collection, handling legacy "Read Later" rename.
    /// </summary>
    Task<Collection> GetReadingListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a URL to the Reading List with move-to-top semantics.
    /// </summary>
    Task<CollectionItem> SaveToReadingListAsync(string url, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves multiple URLs to the Reading List at the end.
    /// </summary>
    Task SaveAllToReadingListAsync(IEnumerable<(string Url, string Title)> items, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes expired items from the Reading List.
    /// </summary>
    Task<int> PurgeExpiredReadingListItemsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
}
