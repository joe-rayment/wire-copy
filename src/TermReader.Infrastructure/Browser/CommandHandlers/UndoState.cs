// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Represents a pending undo operation for a destructive action.
/// The actual deletion is deferred until the undo window expires or
/// another action clears the undo state.
/// </summary>
internal enum UndoActionKind
{
    /// <summary>
    /// A collection item was removed from a collection.
    /// </summary>
    CollectionItemRemoved,

    /// <summary>
    /// A bookmark was removed from the launcher.
    /// </summary>
    BookmarkRemoved,

    /// <summary>
    /// An entire collection was deleted.
    /// </summary>
    CollectionDeleted,
}

/// <summary>
/// Holds the information needed to undo a destructive action or commit it
/// after the undo window expires.
/// </summary>
internal sealed class UndoState
{
    /// <summary>
    /// Duration of the undo window before the action is committed permanently.
    /// </summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What kind of destructive action was taken.
    /// </summary>
    public required UndoActionKind Kind { get; init; }

    /// <summary>
    /// UTC timestamp when the action was taken (start of the undo window).
    /// </summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>
    /// Display name/title of the removed item (used in status messages).
    /// </summary>
    public required string ItemTitle { get; init; }

    // --- Collection item removal ---

    /// <summary>
    /// The collection ID the item was removed from.
    /// </summary>
    public Guid CollectionId { get; init; }

    /// <summary>
    /// The removed item's ID (for collection items).
    /// </summary>
    public Guid ItemId { get; init; }

    /// <summary>
    /// The removed item's URL (needed to re-add on undo).
    /// </summary>
    public string? ItemUrl { get; init; }

    /// <summary>
    /// The removed item's original sort-order index (so it can be restored at the same position).
    /// </summary>
    public int OriginalIndex { get; init; }

    // --- Bookmark removal ---

    /// <summary>
    /// The removed bookmark's ID.
    /// </summary>
    public Guid BookmarkId { get; init; }

    /// <summary>
    /// The removed bookmark's URL (needed to re-add on undo).
    /// </summary>
    public string? BookmarkUrl { get; init; }

    /// <summary>
    /// The removed bookmark's name (needed to re-add on undo).
    /// </summary>
    public string? BookmarkName { get; init; }

    /// <summary>
    /// Whether the undo window has expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow - CreatedAtUtc > UndoWindow;
}
