// Licensed under the MIT License. See LICENSE in the repository root.

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
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<BookmarkService> _logger;

    public BookmarkService(IBookmarkRepository repository, IUnitOfWork unitOfWork, ILogger<BookmarkService> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<Bookmark>> GetAllBookmarksAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Bookmark> AddBookmarkAsync(string name, string url, CancellationToken cancellationToken = default)
    {
        var nextOrder = await _repository.GetNextSortOrderAsync(cancellationToken).ConfigureAwait(false);
        var bookmark = Bookmark.Create(name, url, nextOrder);
        await _repository.AddAsync(bookmark, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Added bookmark: {Name} ({Url})", name, url);
        return bookmark;
    }

    public async Task RenameBookmarkAsync(Guid id, string newName, CancellationToken cancellationToken = default)
    {
        var bookmark = await _repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Bookmark {id} not found");

        bookmark.Rename(newName);
        await _repository.UpdateAsync(bookmark, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Renamed bookmark to: {Name}", newName);
    }

    public async Task MoveBookmarkUpAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var all = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var index = IndexOfBookmarkById(all, id);
        if (index < 0)
        {
            throw new InvalidOperationException($"Bookmark {id} not found");
        }

        if (index == 0)
        {
            return; // Already at the top
        }

        var current = all[index];
        var previous = all[index - 1];
        var tempOrder = current.SortOrder;
        current.SetSortOrder(previous.SortOrder);
        previous.SetSortOrder(tempOrder);
        await _repository.UpdateAsync(current, cancellationToken).ConfigureAwait(false);
        await _repository.UpdateAsync(previous, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveBookmarkDownAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var all = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var index = IndexOfBookmarkById(all, id);
        if (index < 0)
        {
            throw new InvalidOperationException($"Bookmark {id} not found");
        }

        if (index >= all.Count - 1)
        {
            return; // Already at the bottom
        }

        var current = all[index];
        var next = all[index + 1];
        var tempOrder = current.SortOrder;
        current.SetSortOrder(next.SortOrder);
        next.SetSortOrder(tempOrder);
        await _repository.UpdateAsync(current, cancellationToken).ConfigureAwait(false);
        await _repository.UpdateAsync(next, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var bookmark = await _repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Bookmark {id} not found");

        await _repository.DeleteAsync(bookmark, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Deleted bookmark: {Name}", bookmark.Name);
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        await _repository.SeedDefaultsAsync(cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int IndexOfBookmarkById(IReadOnlyList<Bookmark> bookmarks, Guid id)
    {
        for (var i = 0; i < bookmarks.Count; i++)
        {
            if (bookmarks[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
