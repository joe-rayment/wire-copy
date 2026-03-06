// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities.Collections;

namespace TermReader.Infrastructure.Collections;

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
        var collection = await _repository.GetOrCreateDefaultAsync(cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return await SaveItemToCollection(collection, url, title, cancellationToken);
    }

    public async Task<CollectionItem?> SaveToCollectionByNameAsync(string collectionName, string url, string title, CancellationToken cancellationToken = default)
    {
        var collection = await _repository.GetByNameAsync(collectionName, cancellationToken);
        if (collection == null)
        {
            collection = Collection.Create(collectionName);
            await _repository.AddAsync(collection, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Created new collection: {Name}", collectionName);
        }

        return await SaveItemToCollection(collection, url, title, cancellationToken);
    }

    public async Task<IReadOnlyList<Collection>> GetAllCollectionsAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.GetAllAsync(cancellationToken);
    }

    public async Task<Collection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetByNameAsync(name, cancellationToken);
        if (existing != null)
        {
            throw new InvalidOperationException($"A collection named '{name}' already exists");
        }

        var collection = Collection.Create(name);
        await _repository.AddAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Created collection: {Name}", name);
        return collection;
    }

    public async Task RenameCollectionAsync(Guid collectionId, string newName, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken);
        collection.Rename(newName);
        await _repository.UpdateAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Renamed collection {Id} to {Name}", collectionId, newName);
    }

    public async Task DeleteCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken);
        await _repository.DeleteAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Deleted collection: {Name}", collection.Name);
    }

    public async Task ClearCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken);
        collection.Clear();
        await _repository.UpdateAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Cleared collection: {Name}", collection.Name);
    }

    public async Task RemoveItemAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken);
        collection.RemoveItem(itemId);
        await _repository.UpdateAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveItemUpAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken);
        collection.MoveItemUp(itemId);
        await _repository.UpdateAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveItemDownAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken);
        collection.MoveItemDown(itemId);
        await _repository.UpdateAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkItemAsReadAsync(Guid collectionId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var collection = await GetCollectionOrThrow(collectionId, cancellationToken);
        var item = collection.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            throw new InvalidOperationException($"Item {itemId} not found in collection {collectionId}");
        }

        item.MarkAsRead();
        await _repository.UpdateAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task SetDefaultCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        // Verify the collection exists
        await GetCollectionOrThrow(collectionId, cancellationToken);
        await _repository.SetLastUsedCollectionIdAsync(collectionId, cancellationToken);
        _logger.LogInformation("Set default collection to {Id}", collectionId);
    }

    public async Task<Collection> GetDefaultCollectionAsync(CancellationToken cancellationToken = default)
    {
        var lastUsedId = await _repository.GetLastUsedCollectionIdAsync(cancellationToken);
        if (lastUsedId.HasValue)
        {
            var collection = await _repository.GetByIdAsync(lastUsedId.Value, cancellationToken);
            if (collection != null)
            {
                return collection;
            }
        }

        var defaultCollection = await _repository.GetOrCreateDefaultAsync(cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return defaultCollection;
    }

    public async Task<CollectionItem> SaveToReadingListAsync(string url, string title, CancellationToken cancellationToken = default)
    {
        var collection = await GetOrCreateReadingListAsync(cancellationToken);
        var item = collection.AddOrMoveToTop(url, title);
        await _repository.UpdateAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Saved {Url} to Reading List (move-to-top)", url);
        return item;
    }

    public async Task SaveAllToReadingListAsync(IEnumerable<(string Url, string Title)> items, CancellationToken cancellationToken = default)
    {
        var collection = await GetOrCreateReadingListAsync(cancellationToken);
        collection.AddItemsAtEnd(items);
        await _repository.UpdateAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Saved multiple items to Reading List");
    }

    public async Task<int> PurgeExpiredReadingListItemsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var collection = await _repository.GetByNameAsync("Reading List", cancellationToken);
        if (collection == null)
        {
            return 0;
        }

        var removed = collection.RemoveExpiredItems(maxAge);
        if (removed > 0)
        {
            await _repository.UpdateAsync(collection, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Purged {Count} expired items from Reading List", removed);
        }

        return removed;
    }

    private async Task<Collection> GetOrCreateReadingListAsync(CancellationToken cancellationToken)
    {
        // Try "Reading List" first
        var collection = await _repository.GetByNameAsync("Reading List", cancellationToken);
        if (collection != null)
        {
            return collection;
        }

        // Rename legacy "Read Later" to "Reading List" if found
        var legacy = await _repository.GetByNameAsync("Read Later", cancellationToken);
        if (legacy != null)
        {
            legacy.Rename("Reading List");
            await _repository.UpdateAsync(legacy, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return legacy;
        }

        // Create new Reading List
        collection = Collection.Create("Reading List");
        await _repository.AddAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
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
        await _repository.UpdateAsync(collection, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Saved {Url} to collection {Name}", url, collection.Name);
        return item;
    }

    private async Task<Collection> GetCollectionOrThrow(Guid collectionId, CancellationToken cancellationToken)
    {
        var collection = await _repository.GetByIdAsync(collectionId, cancellationToken);
        if (collection == null)
        {
            throw new InvalidOperationException($"Collection {collectionId} not found");
        }

        return collection;
    }
}
