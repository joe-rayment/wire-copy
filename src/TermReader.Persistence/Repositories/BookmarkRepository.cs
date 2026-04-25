// Educational and personal use only.

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

    public async Task SeedDefaultsAsync(CancellationToken cancellationToken = default)
    {
        var any = await _context.Set<Bookmark>().AnyAsync(cancellationToken);
        if (any)
        {
            return;
        }

        var defaults = new[]
        {
            Bookmark.Create("Maclean's", "https://macleans.ca", 0),
            Bookmark.Create("CBC News", "https://www.cbc.ca/news", 1),
            Bookmark.Create("NYT Today's Paper", "https://www.nytimes.com/section/todayspaper", 2),
            Bookmark.Create("The Verge", "https://www.theverge.com", 3),
            Bookmark.Create("The Toronto Star", "https://www.thestar.com", 4),
            Bookmark.Create("Techmeme", "https://www.techmeme.com", 5),
            Bookmark.Create("Wall Street Journal", "https://www.wsj.com", 6),
        };

        foreach (var bookmark in defaults)
        {
            _context.Set<Bookmark>().Add(bookmark);
        }
    }
}
