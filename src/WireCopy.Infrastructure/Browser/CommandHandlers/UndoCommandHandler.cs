// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles undo operations and manages the undo-state lifecycle.
/// Call <see cref="CommitIfExpired"/> before processing any command
/// to auto-commit expired undo states. Call <see cref="ClearOnAction"/>
/// when a non-undo command is executed to commit immediately.
/// </summary>
internal static class UndoCommandHandler
{
    /// <summary>
    /// Commits the pending undo (performs the actual delete) if the undo window has expired.
    /// Returns true if an expired undo was committed.
    /// </summary>
    public static async Task<bool> CommitIfExpired(CommandContext ctx, CancellationToken ct)
    {
        var undo = ctx.PendingUndo;
        if (undo == null || !undo.IsExpired)
        {
            return false;
        }

        await CommitDeletion(ctx, undo, ct).ConfigureAwait(false);
        ctx.PendingUndo = null;
        return true;
    }

    /// <summary>
    /// Called when any non-undo action occurs. If there is a pending undo,
    /// it is committed immediately (the user chose to proceed without undoing).
    /// </summary>
    public static async Task ClearOnAction(CommandContext ctx, CancellationToken ct)
    {
        var undo = ctx.PendingUndo;
        if (undo == null)
        {
            return;
        }

        await CommitDeletion(ctx, undo, ct).ConfigureAwait(false);
        ctx.PendingUndo = null;
    }

    /// <summary>
    /// Handles the undo command (z key). Restores the removed item and clears undo state.
    /// </summary>
    public static async Task HandleUndo(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var undo = ctx.PendingUndo;
        if (undo == null)
        {
            ctx.NavigationService.SetStatusMessage("Nothing to undo");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        if (undo.IsExpired)
        {
            // Window passed — commit the deletion and inform the user
            await CommitDeletion(ctx, undo, ct).ConfigureAwait(false);
            ctx.PendingUndo = null;
            ctx.NavigationService.SetStatusMessage("Undo window expired");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        // Restore the item
        try
        {
            switch (undo.Kind)
            {
                case UndoActionKind.CollectionItemRemoved:
                    await RestoreCollectionItem(ctx, undo, ct).ConfigureAwait(false);
                    break;

                case UndoActionKind.BookmarkRemoved:
                    await RestoreBookmark(ctx, undo, ct).ConfigureAwait(false);
                    break;

                case UndoActionKind.CollectionDeleted:
                    // Collection deletion still uses the confirmation dialog (too complex to undo).
                    // This branch is a safeguard; it should not be reached.
                    ctx.NavigationService.SetStatusMessage("Cannot undo collection deletion");
                    break;
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to undo {Kind}", undo.Kind);
            ctx.NavigationService.SetStatusMessage("Undo failed");
        }

        ctx.PendingUndo = null;
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the actual permanent deletion for a pending undo state.
    /// </summary>
    private static async Task CommitDeletion(CommandContext ctx, UndoState undo, CancellationToken ct)
    {
        try
        {
            switch (undo.Kind)
            {
                case UndoActionKind.CollectionItemRemoved:
                {
                    using var scope = ctx.ScopeFactory.CreateScope();
                    var service = ctx.CreateCollectionService(scope);
                    await service.RemoveItemAsync(undo.CollectionId, undo.ItemId, ct).ConfigureAwait(false);
                    await ctx.RefreshCollectionsAsync(ct).ConfigureAwait(false);
                    ctx.Logger.LogInformation("Committed removal of collection item: {Title}", undo.ItemTitle);
                    break;
                }

                case UndoActionKind.BookmarkRemoved:
                {
                    using var scope = ctx.ScopeFactory.CreateScope();
                    var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
                    await bookmarkService.DeleteBookmarkAsync(undo.BookmarkId, ct).ConfigureAwait(false);
                    await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);
                    ctx.Logger.LogInformation("Committed removal of bookmark: {Name}", undo.BookmarkName);
                    break;
                }

                case UndoActionKind.CollectionDeleted:
                    // Already handled via confirmation dialog — no deferred deletion
                    break;
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to commit deletion for {Kind}: {Title}", undo.Kind, undo.ItemTitle);
        }
    }

    /// <summary>
    /// Restores a collection item that was removed from the in-memory model
    /// but not yet persisted.
    /// </summary>
    private static async Task RestoreCollectionItem(CommandContext ctx, UndoState undo, CancellationToken ct)
    {
        // The item was only removed from the in-memory collection list (not yet persisted).
        // Re-add by calling SaveToReadingListAsync / SaveToCollectionByNameAsync is not ideal
        // because it doesn't preserve sort order. Instead, we reload from the DB (which still
        // has the item since we deferred the actual delete).
        await ctx.RefreshCollectionsAsync(ct).ConfigureAwait(false);

        // Restore the selection index to where the item was
        var refreshedCol = ctx.NavigationService.ActiveCollection;
        if (refreshedCol != null)
        {
            var idx = undo.OriginalIndex;
            if (idx >= refreshedCol.Items.Count)
            {
                idx = Math.Max(0, refreshedCol.Items.Count - 1);
            }

            ctx.NavigationService.CollectionItemSelectedIndex = idx;
        }

        ctx.NavigationService.SetStatusMessage($"Restored: {undo.ItemTitle}");
        ctx.Logger.LogInformation("Undid removal of collection item: {Title}", undo.ItemTitle);
    }

    /// <summary>
    /// Restores a bookmark that was removed from the in-memory list
    /// but not yet persisted.
    /// </summary>
    private static async Task RestoreBookmark(CommandContext ctx, UndoState undo, CancellationToken ct)
    {
        // Like collection items, the bookmark was only removed from the in-memory list.
        // Refreshing from the DB restores it since the actual delete was deferred.
        await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);

        // Restore selection
        if (ctx.Bookmarks != null)
        {
            var idx = undo.OriginalIndex;
            var totalItems = ctx.Bookmarks.Count + 1; // +1 for Collections tile
            if (ctx.NavigationService.LauncherSelectedIndex >= totalItems)
            {
                ctx.NavigationService.LauncherSelectedIndex = Math.Max(0, idx);
            }
            else
            {
                ctx.NavigationService.LauncherSelectedIndex = Math.Min(idx, Math.Max(0, totalItems - 1));
            }
        }

        ctx.NavigationService.SetStatusMessage($"Restored: {undo.BookmarkName}");
        ctx.Logger.LogInformation("Undid removal of bookmark: {Name}", undo.BookmarkName);
    }
}
