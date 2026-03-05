// Educational and personal use only.

using Microsoft.EntityFrameworkCore;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities.Bookmarks;

namespace TermReader.Infrastructure.Persistence.Repositories;

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

    public async Task AddAsync(Bookmark bookmark, CancellationToken cancellationToken = default)
    {
        await _context.Set<Bookmark>().AddAsync(bookmark, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Bookmark bookmark, CancellationToken cancellationToken = default)
    {
        // Rely on EF Core change tracking to detect modifications.
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Bookmark bookmark, CancellationToken cancellationToken = default)
    {
        _context.Set<Bookmark>().Remove(bookmark);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> GetNextSortOrderAsync(CancellationToken cancellationToken = default)
    {
        var maxOrder = await _context.Set<Bookmark>()
            .MaxAsync(b => (int?)b.SortOrder, cancellationToken);
        return (maxOrder ?? -1) + 1;
    }

    public async Task SeedDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var any = await _context.Set<Bookmark>().AnyAsync(cancellationToken);
        if (any)
        {
            return;
        }

#pragma warning disable S1075 // URIs are intentional default bookmark seeds, not configuration
        var defaults = new[]
        {
            Bookmark.Create("Maclean's", "https://macleans.ca", 0),
            Bookmark.Create("NYT Today's Paper", "https://www.nytimes.com/section/todayspaper", 1),
            Bookmark.Create("The Verge", "https://www.theverge.com", 2),
        };
#pragma warning restore S1075

        foreach (var bookmark in defaults)
        {
            await _context.Set<Bookmark>().AddAsync(bookmark, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
