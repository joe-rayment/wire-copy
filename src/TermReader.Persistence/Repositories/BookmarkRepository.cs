// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities.Bookmarks;

namespace TermReader.Persistence.Repositories;

/// <summary>
/// Repository implementation for bookmark persistence using EF Core.
/// </summary>
public class BookmarkRepository : IBookmarkRepository
{
    private readonly AppDbContext _context;

    public BookmarkRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IReadOnlyList<Bookmark>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Set<Bookmark>()
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Bookmark?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Set<Bookmark>()
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public Task AddAsync(Bookmark bookmark, CancellationToken cancellationToken = default)
    {
        _context.Set<Bookmark>().Add(bookmark);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Bookmark bookmark, CancellationToken cancellationToken = default)
    {
        // Rely on EF Core change tracking to detect modifications.
        // SaveChangesAsync is called by the service layer via IUnitOfWork.
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Bookmark bookmark, CancellationToken cancellationToken = default)
    {
        _context.Set<Bookmark>().Remove(bookmark);
        return Task.CompletedTask;
    }

    public async Task<int> GetNextSortOrderAsync(CancellationToken cancellationToken = default)
    {
        var maxOrder = await _context.Set<Bookmark>()
            .MaxAsync(b => (int?)b.SortOrder, cancellationToken);
        return (maxOrder ?? -1) + 1;
    }
}
