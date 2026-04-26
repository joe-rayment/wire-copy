// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.Entities.Collections;

namespace TermReader.Application.Interfaces;

/// <summary>
/// Repository interface for collection persistence operations.
/// </summary>
public interface ICollectionRepository
{
    /// <summary>
    /// Gets all collections with their items.
    /// </summary>
    Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a collection by its ID, including items.
    /// </summary>
    Task<Collection?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a collection by name (case-insensitive), including items.
    /// </summary>
    Task<Collection?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets or creates the default "Reading List" collection.
    /// </summary>
    Task<Collection> GetOrCreateDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new collection.
    /// </summary>
    Task AddAsync(Collection collection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing collection.
    /// </summary>
    Task UpdateAsync(Collection collection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a collection and all its items.
    /// </summary>
    Task DeleteAsync(Collection collection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the ID of the last used collection.
    /// </summary>
    Task<Guid?> GetLastUsedCollectionIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the ID of the last used collection.
    /// </summary>
    Task SetLastUsedCollectionIdAsync(Guid collectionId, CancellationToken cancellationToken = default);
}
