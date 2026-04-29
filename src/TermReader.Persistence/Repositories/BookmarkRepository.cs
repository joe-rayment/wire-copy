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

    public async Task SeedDefaultsAsync(CancellationToken cancellationToken = default)
    {
        // Additive seed: add any default that isn't already present by URL.
        // This way defaults added in later releases (e.g. Wired, The New Yorker)
        // land for existing users instead of being skipped because the table
        // wasn't empty at first launch.
        var defaults = new[]
        {
            ("Maclean's", "https://macleans.ca"),
            ("CBC News", "https://www.cbc.ca/news"),
            ("NYT Today's Paper", "https://www.nytimes.com/section/todayspaper"),
            ("The Verge", "https://www.theverge.com"),
            ("The Toronto Star", "https://www.thestar.com"),
            ("Techmeme", "https://www.techmeme.com"),
            ("Wall Street Journal", "https://www.wsj.com"),
            ("Wired", "https://www.wired.com"),
            ("The New Yorker", "https://www.newyorker.com"),
        };

        var existingUrls = await _context.Set<Bookmark>()
            .Select(b => b.Url)
            .ToListAsync(cancellationToken);
        var existingUrlSet = new HashSet<string>(existingUrls, StringComparer.OrdinalIgnoreCase);

        var nextSortOrder = await GetNextSortOrderAsync(cancellationToken);

        foreach (var (name, url) in defaults)
        {
            if (existingUrlSet.Contains(url))
            {
                continue;
            }

            _context.Set<Bookmark>().Add(Bookmark.Create(name, url, nextSortOrder));
            nextSortOrder++;
        }
    }
}
