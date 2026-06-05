// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Entities.Collections;

namespace WireCopy.Infrastructure.Collections;

/// <summary>
/// Application service for managing collections and saved links.
/// </summary>
public class CollectionService : ICollectionService
{
    private readonly ICollectionRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<CollectionService> _logger;

    public CollectionService(ICollectionRepository repository, IUnitOfWork unitOfWork, ILogger<CollectionService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CollectionItem?> SaveToDefaultCollectionAsync(string url, string title, CancellationToken cancellationToken = default)
    {
        var collection = await _repository.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await SaveItemToCollection(collection, url, title, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CollectionItem?> SaveToCollectionByNameAsync(string collectionName, string url, string title, CancellationToken cancellationToken = default)
    {
        var collection = await _repository.GetByNameAsync(collectionName, cancellationToken).ConfigureAwait(false);
        if (collection == null)
        {
            collection = Collection.Create(collectionName);
            await _repository.AddAsync(collection, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Created new collection: {Name}", collectionName);
        }

        return await SaveItemToCollection(collection, url, title, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Collection>> GetAllCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await MergeLegacyReadLaterAsync(cancellationToken).ConfigureAwait(false);
        return await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Collection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetByNameAsync(name, cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            throw new InvalidOperationException($"A collection named '{name}' already exists");
        }

        var collection = Collection.Create(name);
        await _repository.AddAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Created collection: {Name}", name);
        return collection;
    }

    public async Task RenameCollectionAsync(Guid collectionId, string newName, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken).ConfigureAwait(false);
        collection.Rename(newName);
        await _repository.UpdateAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Renamed collection {Id} to {Name}", collectionId, newName);
    }

    public async Task DeleteCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken).ConfigureAwait(false);
        await _repository.DeleteAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Deleted collection: {Name}", collection.Name);
    }

    public async Task ClearCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken).ConfigureAwait(false);
        collection.Clear();
        await _repository.UpdateAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Cleared collection: {Name}", collection.Name);
    }

    public async Task RemoveItemAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken).ConfigureAwait(false);
        collection.RemoveItem(itemId);
        await _repository.UpdateAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveItemUpAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken).ConfigureAwait(false);
        collection.MoveItemUp(itemId);
        await _repository.UpdateAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveItemDownAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken).ConfigureAwait(false);
        collection.MoveItemDown(itemId);
        await _repository.UpdateAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkItemAsReadAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken).ConfigureAwait(false);
        var item = collection.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            throw new InvalidOperationException($"Item {itemId} not found in collection {collectionId}");
        }

        item.MarkAsRead();
        await _repository.UpdateAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetDefaultCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        // Verify the collection exists
        await GetCollectionOrThrow(collectionId, cancellationToken).ConfigureAwait(false);
        await _repository.SetLastUsedCollectionIdAsync(collectionId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Set default collection to {Id}", collectionId);
    }

    public async Task<Collection> GetDefaultCollectionAsync(CancellationToken cancellationToken = default)
    {
        var lastUsedId = await _repository.GetLastUsedCollectionIdAsync(cancellationToken).ConfigureAwait(false);
        if (lastUsedId.HasValue)
        {
            var collection = await _repository.GetByIdAsync(lastUsedId.Value, cancellationToken).ConfigureAwait(false);
            if (collection != null)
            {
                return collection;
            }
        }

        var defaultCollection = await _repository.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return defaultCollection;
    }

    public async Task<Collection> GetReadingListAsync(CancellationToken cancellationToken = default)
    {
        return await GetOrCreateReadingListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CollectionItem> SaveToReadingListAsync(string url, string title, CancellationToken cancellationToken = default)
    {
        var collection = await GetOrCreateReadingListAsync(cancellationToken).ConfigureAwait(false);
        var item = collection.AddOrMoveToTop(url, title);
        await _repository.UpdateAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Saved {Url} to Reading List (move-to-top)", url);
        return item;
    }

    public async Task SaveAllToReadingListAsync(IEnumerable<(string Url, string Title)> items, CancellationToken cancellationToken = default)
    {
        var collection = await GetOrCreateReadingListAsync(cancellationToken).ConfigureAwait(false);
        collection.AddItemsAtEnd(items);
        await _repository.UpdateAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Saved multiple items to Reading List");
    }

    public async Task<int> PurgeExpiredReadingListItemsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var collection = await _repository.GetByNameAsync("Reading List", cancellationToken).ConfigureAwait(false);
        if (collection == null)
        {
            return 0;
        }

        var removed = collection.RemoveExpiredItems(maxAge);
        if (removed > 0)
        {
            await _repository.UpdateAsync(collection, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Purged {Count} expired items from Reading List", removed);
        }

        return removed;
    }

    public Task<int> CountReadingListItemsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        return _repository.CountReadingListItemsAsync(maxAge, cancellationToken);
    }

    private async Task MergeLegacyReadLaterAsync(CancellationToken cancellationToken)
    {
        var legacy = await _repository.GetByNameAsync("Read Later", cancellationToken).ConfigureAwait(false);
        if (legacy == null)
        {
            return;
        }

        var readingList = await _repository.GetByNameAsync("Reading List", cancellationToken).ConfigureAwait(false);
        if (readingList != null)
        {
            // Both exist — merge items from legacy into Reading List, then delete legacy
            foreach (var item in legacy.Items.Where(item => !readingList.ContainsUrl(item.Url)))
            {
                readingList.AddItem(item.Url, item.Title);
            }

            await _repository.UpdateAsync(readingList, cancellationToken).ConfigureAwait(false);
            await _repository.DeleteAsync(legacy, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Merged legacy 'Read Later' ({Count} items) into Reading List", legacy.Items.Count);
        }
        else
        {
            // Only legacy exists — rename it
            legacy.Rename("Reading List");
            await _repository.UpdateAsync(legacy, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Renamed legacy 'Read Later' to 'Reading List'");
        }
    }

    private async Task<Collection> GetOrCreateReadingListAsync(CancellationToken cancellationToken)
    {
        // Try "Reading List" first
        var collection = await _repository.GetByNameAsync("Reading List", cancellationToken).ConfigureAwait(false);
        if (collection != null)
        {
            return collection;
        }

        // Rename legacy "Read Later" to "Reading List" if found
        var legacy = await _repository.GetByNameAsync("Read Later", cancellationToken).ConfigureAwait(false);
        if (legacy != null)
        {
            legacy.Rename("Reading List");
            await _repository.UpdateAsync(legacy, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return legacy;
        }

        // Create new Reading List
        collection = Collection.Create("Reading List");
        await _repository.AddAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Created Reading List collection");
        return collection;
    }

    private async Task<CollectionItem?> SaveItemToCollection(Collection collection, string url, string title, CancellationToken cancellationToken)
    {
        // Duplicate URL detection: silently skip if URL already exists
        if (collection.ContainsUrl(url))
        {
            _logger.LogDebug("URL already exists in collection {Name}: {Url}", collection.Name, url);
            return null;
        }

        var item = collection.AddItem(url, title);
        await _repository.UpdateAsync(collection, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Saved {Url} to collection {Name}", url, collection.Name);
        return item;
    }

    private async Task<Collection> GetCollectionOrThrow(Guid collectionId, CancellationToken cancellationToken)
    {
        var collection = await _repository.GetByIdAsync(collectionId, cancellationToken).ConfigureAwait(false);
        if (collection == null)
        {
            throw new InvalidOperationException($"Collection {collectionId} not found");
        }

        return collection;
    }
}
