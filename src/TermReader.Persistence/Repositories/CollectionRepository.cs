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
    private const string DefaultCollectionName = "Read Later";

    private readonly AppDbContext _context;

    // Simple in-memory tracking of last used collection ID.
    // Persists across the application lifetime (singleton scope).
    private Guid? _lastUsedCollectionId;

    public CollectionRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Set<Collection>()
            .Include(c => c.Items)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Collection?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Set<Collection>()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Collection?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        // SQLite default collation is case-insensitive for ASCII
        return await _context.Set<Collection>()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower(), cancellationToken);
    }

    public async Task<Collection> GetOrCreateDefaultAsync(CancellationToken cancellationToken = default)
    {
        var existing = await GetByNameAsync(DefaultCollectionName, cancellationToken);
        if (existing != null)
            return existing;

        var collection = Collection.Create(DefaultCollectionName, sortOrder: 0);
        await _context.Set<Collection>().AddAsync(collection, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return collection;
    }

    public async Task AddAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        await _context.Set<Collection>().AddAsync(collection, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        // Do not call _context.Update(collection) — it marks the entire entity graph
        // as Modified, which fails for newly added child entities. Instead, rely on
        // EF Core change tracking to detect modifications and new entities automatically.
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        _context.Set<Collection>().Remove(collection);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public Task<Guid?> GetLastUsedCollectionIdAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_lastUsedCollectionId);
    }

    public Task SetLastUsedCollectionIdAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        _lastUsedCollectionId = collectionId;
        return Task.CompletedTask;
    }
}
