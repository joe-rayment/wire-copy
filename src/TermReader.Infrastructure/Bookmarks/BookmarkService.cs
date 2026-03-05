// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities.Bookmarks;

namespace TermReader.Infrastructure.Bookmarks;

/// <summary>
/// Application service for managing launcher bookmarks.
/// </summary>
public class BookmarkService : IBookmarkService
{
    private readonly IBookmarkRepository _repository;
    private readonly ILogger<BookmarkService> _logger;

    public BookmarkService(IBookmarkRepository repository, ILogger<BookmarkService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<Bookmark>> GetAllBookmarksAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.GetAllAsync(cancellationToken);
    }

    public async Task<Bookmark> AddBookmarkAsync(string name, string url, CancellationToken cancellationToken = default)
    {
        var nextOrder = await _repository.GetNextSortOrderAsync(cancellationToken);
        var bookmark = Bookmark.Create(name, url, nextOrder);
        await _repository.AddAsync(bookmark, cancellationToken);
        _logger.LogInformation("Added bookmark: {Name} ({Url})", name, url);
        return bookmark;
    }

    public async Task RenameBookmarkAsync(Guid id, string newName, CancellationToken cancellationToken = default)
    {
        var bookmark = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Bookmark {id} not found");

        bookmark.Rename(newName);
        await _repository.UpdateAsync(bookmark, cancellationToken);
        _logger.LogInformation("Renamed bookmark to: {Name}", newName);
    }

    public async Task DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var bookmark = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Bookmark {id} not found");

        await _repository.DeleteAsync(bookmark, cancellationToken);
        _logger.LogInformation("Deleted bookmark: {Name}", bookmark.Name);
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        await _repository.SeedDefaultsAsync(cancellationToken);
    }
}
