// Licensed under the MIT License. See LICENSE in the repository root.

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
    /// Moves a bookmark up in the sort order (swaps with the previous bookmark).
    /// </summary>
    Task MoveBookmarkUpAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a bookmark down in the sort order (swaps with the next bookmark).
    /// </summary>
    Task MoveBookmarkDownAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a bookmark.
    /// </summary>
    Task DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds default bookmarks if none exist.
    /// </summary>
    Task EnsureSeededAsync(CancellationToken cancellationToken = default);
}
