// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.Entities.Bookmarks;

namespace TermReader.Application.Interfaces;

/// <summary>
/// A bookmark entry as it appears in the user's bookmarks.json config file.
/// File-shape DTO — kept separate from the <see cref="Bookmark"/> domain entity
/// because the file has only the fields the user sees (name, url) plus order
/// is implied by array position.
/// </summary>
public sealed record BookmarkConfigEntry(string Name, string Url);

/// <summary>
/// Parsed contents of a bookmarks.json config file (user-visible or shipped).
/// </summary>
public sealed record BookmarkConfigFile(int Version, IReadOnlyList<BookmarkConfigEntry> Bookmarks);

/// <summary>
/// Read/write access to the user's bookmarks.json config file (the
/// source-of-truth for the bookmark list) and to the shipped defaults
/// embedded in the binary.
/// </summary>
public interface IBookmarkConfigStore
{
    /// <summary>
    /// Absolute path to the user's bookmarks.json config file.
    /// </summary>
    string UserConfigPath { get; }

    /// <summary>
    /// Returns true when the user's config file exists on disk.
    /// </summary>
    bool UserConfigExists();

    /// <summary>
    /// Loads the user's config file. Returns null if the file does not exist.
    /// Throws on parse errors so callers can decide whether to surface a fatal
    /// error or proceed with defaults.
    /// </summary>
    Task<BookmarkConfigFile?> LoadUserConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the shipped defaults that are embedded in the binary.
    /// </summary>
    Task<BookmarkConfigFile> LoadShippedDefaultsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically writes the given bookmarks (in array order) to the user's
    /// config file. Writes to a temp file then renames on success so an
    /// interruption never leaves a half-written file.
    /// </summary>
    Task SaveUserConfigAsync(IReadOnlyList<Bookmark> bookmarks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically writes the given config (raw entries) to the user's
    /// config file. Used by the reconciler when adding shipped defaults.
    /// </summary>
    Task SaveUserConfigAsync(BookmarkConfigFile config, CancellationToken cancellationToken = default);
}
