// Educational and personal use only.

using Microsoft.EntityFrameworkCore;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities.Collections;

namespace TermReader.Persistence.Repositories;

/// <summary>
/// Repository implementation for collection persistence using EF Core.
/// </summary>
public class CollectionRepository : ICollectionRepository
{
    private const string DefaultCollectionName = "Reading List";

    private readonly AppDbContext _context;
    private readonly ICollectionPreferences _preferences;

    public CollectionRepository(AppDbContext context, ICollectionPreferences preferences)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
    }

    public async Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Set<Collection>()
            .Include(c => c.Items.OrderBy(i => i.SortOrder))
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Collection?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Set<Collection>()
            .Include(c => c.Items.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Collection?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        // SQLite default collation is case-insensitive for ASCII
        return await _context.Set<Collection>()
            .Include(c => c.Items.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower(), cancellationToken);
    }

    public async Task<Collection> GetOrCreateDefaultAsync(CancellationToken cancellationToken = default)
    {
        var existing = await GetByNameAsync(DefaultCollectionName, cancellationToken);
        if (existing != null)
            return existing;

        var collection = Collection.Create(DefaultCollectionName, sortOrder: 0);
        await _context.Set<Collection>().AddAsync(collection, cancellationToken);
        return collection;
    }

    public Task AddAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        _context.Set<Collection>().Add(collection);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        // Rely on EF Core change tracking to detect modifications and new entities.
        // SaveChangesAsync is called by the service layer via IUnitOfWork.
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        _context.Set<Collection>().Remove(collection);
        return Task.CompletedTask;
    }

    public Task<Guid?> GetLastUsedCollectionIdAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_preferences.LastUsedCollectionId);
    }

    public Task SetLastUsedCollectionIdAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        _preferences.LastUsedCollectionId = collectionId;
        return Task.CompletedTask;
    }
}
