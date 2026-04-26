// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.Entities.Bookmarks;

namespace TermReader.Application.Interfaces;

/// <summary>
/// Repository interface for bookmark persistence operations.
/// </summary>
public interface IBookmarkRepository
{
    /// <summary>
    /// Gets all bookmarks ordered by SortOrder then Name.
    /// </summary>
    Task<IReadOnlyList<Bookmark>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a bookmark by its ID.
    /// </summary>
    Task<Bookmark?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new bookmark.
    /// </summary>
    Task AddAsync(Bookmark bookmark, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing bookmark (relies on change tracking).
    /// </summary>
    Task UpdateAsync(Bookmark bookmark, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a bookmark.
    /// </summary>
    Task DeleteAsync(Bookmark bookmark, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the next available sort order value.
    /// </summary>
    Task<int> GetNextSortOrderAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds default bookmarks if the table is empty.
    /// </summary>
    Task SeedDefaultsAsync(CancellationToken cancellationToken = default);
}
