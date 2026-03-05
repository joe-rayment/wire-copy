// Educational and personal use only.

using TermReader.Domain.Entities.Bookmarks;

namespace TermReader.Application.Interfaces;

/// <summary>
/// Application service for managing launcher bookmarks.
/// </summary>
public interface IBookmarkService
{
    /// <summary>
    /// Gets all bookmarks ordered by sort order.
    /// </summary>
    Task<IReadOnlyList<Bookmark>> GetAllBookmarksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new bookmark with auto-assigned sort order.
    /// </summary>
    Task<Bookmark> AddBookmarkAsync(string name, string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames an existing bookmark.
    /// </summary>
    Task RenameBookmarkAsync(Guid id, string newName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a bookmark.
    /// </summary>
    Task DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds default bookmarks if none exist.
    /// </summary>
    Task EnsureSeededAsync(CancellationToken cancellationToken = default);
}
