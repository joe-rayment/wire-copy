// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Animations;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using static WireCopy.Infrastructure.Browser.UI.KeyRegistry;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles collection-related commands: save, delete, reorder, open collections.
/// </summary>
internal static class CollectionCommandHandler
{
    public static async Task HandleSaveToCollection(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        // (row, col) of the saved row's title — captured BEFORE the await so we can
        // flash the original screen position even if a re-render shifts things.
        // Null when not applicable (multi-save, reader view, off-screen).
        (int Row, int Col)? flashPosition = null;
        string? flashText = null;
        Guid? savedNodeId = null;

        if (viewMode == ViewMode.Hierarchical)
        {
            // Multi-select: save all toggled items
            if (tree != null && tree.SelectionCount > 0)
            {
                try
                {
                    var selectedNodes = tree.GetSelectedNodes();
                    using var scope = ctx.ScopeFactory.CreateScope();
                    var service = ctx.CreateCollectionService(scope);
                    var links = selectedNodes
                        .Where(n => !string.IsNullOrEmpty(n.Link.Url))
                        .Select(n => (n.Link.Url, n.Link.DisplayText))
                        .ToList();

                    await service.SaveAllToReadingListAsync(links, ct).ConfigureAwait(false);
                    tree.ClearSelection();
                    ctx.Logger.LogInformation("Saved {Count} items to Reading List", links.Count);

                    // workspace-wef6.4: success feedback is a transient with its
                    // follow-up key; toasts are reserved for modal-worthy results.
                    ctx.NavigationService.Announce(
                        "✓",
                        $"Saved ({links.Count})",
                        new[] { new StatusKeyHint(KeyFor(CommandType.OpenCollections), "list") },
                        shortText: $"✓ {links.Count}");
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Failed to save selected items");
                    ctx.NavigationService.SetStatusMessage("Failed to save selected items", StatusSeverity.Error);
                    ctx.NavigationService.ShowToast(
                        ToastType.Error,
                        "Couldn't save selected items",
                        ex.Message);
                }
            }
            else
            {
                // Single save: cursor item
                var saveNode = tree?.GetSelectedNode();
                if (saveNode != null && !saveNode.IsGroupHeader && !string.IsNullOrEmpty(saveNode.Link.Url))
                {
                    // Capture the saved row's screen position BEFORE awaiting save —
                    // a re-render or scroll between now and the flash should still
                    // target the row the user originally pressed `s` on.
                    flashPosition = ComputeSelectedRowFlashPosition(tree!, ctx, options);
                    flashText = saveNode.Link.DisplayText;
                    savedNodeId = saveNode.Id;

                    try
                    {
                        using var scope = ctx.ScopeFactory.CreateScope();
                        var service = ctx.CreateCollectionService(scope);
                        await service.SaveToReadingListAsync(
                            saveNode.Link.Url, saveNode.Link.DisplayText, ct).ConfigureAwait(false);
                        ctx.Logger.LogInformation("Saved to Reading List: {Title}", saveNode.Link.DisplayText);

                        // workspace-yejq.6: name the destination. `s` always
                        // targets the Reading List (SaveToReadingListAsync is
                        // hardcoded to it) — announce that truthfully rather
                        // than the starred "default" collection, which does
                        // not govern this save path.
                        ctx.NavigationService.Announce(
                            "✓",
                            $"Saved to Reading List: {saveNode.Link.DisplayText}",
                            new[] { new StatusKeyHint(KeyFor(CommandType.OpenCollections), "list") },
                            shortText: "✓ saved");
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogWarning(ex, "Failed to save to default collection");
                        ctx.NavigationService.SetStatusMessage("Failed to save", StatusSeverity.Error);
                        ctx.NavigationService.ShowToast(
                            ToastType.Error,
                            "Couldn't save",
                            ex.Message);

                        // Failure path: skip the success flash.
                        flashPosition = null;
                        flashText = null;
                    }
                }
            }
        }
        else if (viewMode == ViewMode.Readable)
        {
            var page = ctx.NavigationService.CurrentPage;
            var url = page?.Url;
            var title = page?.Metadata?.Title ?? "Untitled";
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    using var scope = ctx.ScopeFactory.CreateScope();
                    var service = ctx.CreateCollectionService(scope);
                    await service.SaveToReadingListAsync(url, title, ct).ConfigureAwait(false);
                    ctx.Logger.LogInformation("Saved to Reading List from reader: {Title}", title);

                    // workspace-yejq.6: same destination-naming as the link-view save.
                    ctx.NavigationService.Announce(
                        "✓",
                        $"Saved to Reading List: {title}",
                        new[] { new StatusKeyHint(KeyFor(CommandType.OpenCollections), "list") },
                        shortText: "✓ saved");
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Failed to save from reader view");
                    ctx.NavigationService.SetStatusMessage("Failed to save", StatusSeverity.Error);
                    ctx.NavigationService.ShowToast(
                        ToastType.Error,
                        "Couldn't save",
                        ex.Message);
                }
            }
        }
        else if (viewMode == ViewMode.CollectionList &&
                 ctx.Collections != null && ctx.NavigationService.CollectionSelectedIndex < ctx.Collections.Count)
        {
            var col = ctx.Collections[ctx.NavigationService.CollectionSelectedIndex];
            try
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var service = ctx.CreateCollectionService(scope);
                await service.SetDefaultCollectionAsync(col.Id, ct).ConfigureAwait(false);
                ctx.DefaultCollectionId = col.Id;
                ctx.Logger.LogInformation("Set default collection: {Name}", col.Name);

                // workspace-yejq.3: the `s` = set-default overload was a silent
                // action — announce the result so the dual behaviour of `s`
                // is discoverable in the view where it applies.
                ctx.NavigationService.Announce(
                    Indicators.Star.ToString(),
                    $"Default collection: {col.Name}",
                    shortText: $"{Indicators.Star} default");
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to set default collection");
                ctx.NavigationService.SetStatusMessage("Couldn't set default collection", StatusSeverity.Error);
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        // After the toast is staged and the page has been re-rendered, play the
        // row flash on the saved card.
        PlaySaveFlash(ctx, options, flashPosition, flashText, savedNodeId);
    }

    public static async Task HandleSaveToSpecific(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        // Flash target captured before the awaits (mirrors HandleSaveToCollection).
        (int Row, int Col)? flashPosition = null;
        string? flashText = null;
        Guid? savedNodeId = null;

        if (ctx.NavigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
        {
            var saveNode = tree?.GetSelectedNode();
            if (saveNode != null && !saveNode.IsGroupHeader && !string.IsNullOrEmpty(saveNode.Link.Url))
            {
                // workspace-yejq.1: show the existing collection names inside the
                // prompt so users reuse a collection instead of accidentally
                // creating a near-duplicate through a typo.
                var prompt = await BuildSaveToSpecificPromptAsync(ctx, options, ct).ConfigureAwait(false);

                var collectionName = await ctx.InputHandler.PromptForInputAsync(prompt, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(collectionName))
                {
                    // workspace-yejq.5: capture the flash target BEFORE the save
                    // awaits, mirroring HandleSaveToCollection — a re-render
                    // between now and the flash should still hit the row the
                    // user pressed Shift+S on.
                    flashPosition = ComputeSelectedRowFlashPosition(tree!, ctx, options);
                    flashText = saveNode.Link.DisplayText;
                    savedNodeId = saveNode.Id;

                    try
                    {
                        using var scope = ctx.ScopeFactory.CreateScope();
                        var service = ctx.CreateCollectionService(scope);
                        var savedSpecific = await service.SaveToCollectionByNameAsync(
                            collectionName, saveNode.Link.Url, saveNode.Link.DisplayText, ct).ConfigureAwait(false);
                        if (savedSpecific != null)
                        {
                            ctx.Logger.LogInformation(
                                "Saved to collection '{Collection}': {Title}",
                                collectionName,
                                saveNode.Link.DisplayText);

                            // workspace-yejq.2: same Announce vocabulary as the
                            // `s` quick save — glyph + follow-up key.
                            ctx.NavigationService.Announce(
                                "✓",
                                $"Saved to {collectionName}: {saveNode.Link.DisplayText}",
                                new[] { new StatusKeyHint(KeyFor(CommandType.OpenCollections), "list") },
                                shortText: "✓ saved");
                        }
                        else
                        {
                            ctx.Logger.LogWarning(
                                "Already in collection '{Collection}': {Title}",
                                collectionName,
                                saveNode.Link.DisplayText);

                            // workspace-yejq.4: duplicate gets an indicator glyph
                            // and no success flash.
                            ctx.NavigationService.Announce(
                                "ℹ",
                                $"Already in {collectionName}",
                                shortText: "ℹ already saved");
                            flashPosition = null;
                            flashText = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogWarning(ex, "Failed to save to collection");
                        ctx.NavigationService.SetStatusMessage("Failed to save", StatusSeverity.Error);
                        flashPosition = null;
                        flashText = null;
                    }
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        // workspace-yejq.5: the non-duplicate save path gets the same row
        // flash as the `s` quick save.
        PlaySaveFlash(ctx, options, flashPosition, flashText, savedNodeId);
    }

    public static async Task HandleSaveAllToReadingList(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (ctx.NavigationService.CurrentContext.ViewMode != ViewMode.Hierarchical)
        {
            ctx.NavigationService.SetStatusMessage("Switch to link view (t) to save all links");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var tree = ctx.NavigationService.CurrentPage?.LinkTree;
        if (tree != null)
        {
            var visibleNodes = tree.GetVisibleNodes()
                .Where(n => !n.IsGroupHeader && !string.IsNullOrEmpty(n.Link.Url))
                .Select(n => (n.Link.Url, n.Link.DisplayText))
                .ToList();

            if (visibleNodes.Count > 0)
            {
                try
                {
                    using var scope = ctx.ScopeFactory.CreateScope();
                    var service = ctx.CreateCollectionService(scope);
                    await service.SaveAllToReadingListAsync(visibleNodes, ct).ConfigureAwait(false);
                    ctx.Logger.LogInformation("Saved {Count} links to Reading List", visibleNodes.Count);
                    ctx.NavigationService.Announce(
                        "✓",
                        $"Saved ({visibleNodes.Count})",
                        new[] { new StatusKeyHint(KeyFor(CommandType.OpenCollections), "list") },
                        shortText: $"✓ {visibleNodes.Count}");
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Failed to save all to Reading List");
                    ctx.NavigationService.SetStatusMessage("Failed to save", StatusSeverity.Error);
                    ctx.NavigationService.ShowToast(
                        ToastType.Error,
                        "Couldn't save",
                        ex.Message);
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleDeleteItem(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        // Commit any previous pending undo before starting a new delete
        await UndoCommandHandler.ClearOnAction(ctx, ct).ConfigureAwait(false);

        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;

        if (viewMode == ViewMode.CollectionItems)
        {
            var col = ctx.NavigationService.ActiveCollection;
            var deleteIdx = ctx.NavigationService.CollectionItemSelectedIndex;
            if (col != null && deleteIdx >= 0 && deleteIdx < col.Items.Count)
            {
                var item = col.Items[deleteIdx];

                // Store undo state before modifying in-memory collection
                ctx.PendingUndo = new UndoState
                {
                    Kind = UndoActionKind.CollectionItemRemoved,
                    CreatedAtUtc = DateTime.UtcNow,
                    ItemTitle = item.Title,
                    CollectionId = col.Id,
                    ItemId = item.Id,
                    ItemUrl = item.Url,
                    OriginalIndex = deleteIdx,
                };

                // Remove from in-memory collection only (not persisted yet)
                col.RemoveItem(item.Id);
                ctx.NavigationService.SetStatusMessage($"Removed \u00b7 z:undo", UndoState.UndoWindow);

                // Adjust selection index
                var newCount = col.Items.Count;
                if (ctx.NavigationService.CollectionItemSelectedIndex >= newCount)
                {
                    ctx.NavigationService.CollectionItemSelectedIndex = Math.Max(0, newCount - 1);
                }
            }
        }
        else if (viewMode == ViewMode.CollectionList &&
                 ctx.Collections != null && ctx.NavigationService.CollectionSelectedIndex < ctx.Collections.Count)
        {
            var collection = ctx.Collections[ctx.NavigationService.CollectionSelectedIndex];
            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var confirmed = await ConfirmationDialog.ConfirmAsync(
                ctx.InputHandler,
                "Delete Collection",
                $"Delete \"{collection.Name}\" and all its items?",
                palette,
                ct,
                isDestructive: true).ConfigureAwait(false);
            if (!confirmed)
            {
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            try
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var service = ctx.CreateCollectionService(scope);
                await service.DeleteCollectionAsync(collection.Id, ct).ConfigureAwait(false);
                await ctx.RefreshCollectionsAsync(ct).ConfigureAwait(false);
                ctx.NavigationService.SetStatusMessage($"Deleted collection: {collection.Name}");
                if (ctx.NavigationService.CollectionSelectedIndex >= ctx.Collections.Count)
                {
                    ctx.NavigationService.CollectionSelectedIndex = Math.Max(0, ctx.Collections.Count - 1);
                }
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to delete collection");
                ctx.NavigationService.SetStatusMessage("Failed to delete", StatusSeverity.Error);
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleClearCollection(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (ctx.NavigationService.CurrentContext.ViewMode != ViewMode.CollectionItems)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var col = ctx.NavigationService.ActiveCollection;
        if (col == null || col.Items.Count == 0)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var itemTitles = col.Items.Select(i => i.Title).ToList();
        var confirmed = await ConfirmationDialog.ConfirmDestructiveAsync(
            ctx.InputHandler,
            "Clear Collection",
            $"\u26a0  This will permanently remove all {col.Items.Count} article(s) from \"{col.Name}\".",
            itemTitles,
            palette,
            options.TerminalHeight,
            ct).ConfigureAwait(false);
        if (!confirmed)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var service = ctx.CreateCollectionService(scope);
            await service.ClearCollectionAsync(col.Id, ct).ConfigureAwait(false);
            await ctx.RefreshCollectionsAsync(ct).ConfigureAwait(false);
            ctx.NavigationService.CollectionItemSelectedIndex = 0;
            ctx.Logger.LogInformation("Cleared collection: {Name}", col.Name);
            ctx.NavigationService.SetStatusMessage($"Cleared: {col.Name}");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to clear collection");
            ctx.NavigationService.SetStatusMessage("Failed to clear", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleReorderUp(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (ctx.NavigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
        {
            var col = ctx.NavigationService.ActiveCollection;
            var reorderUpIdx = ctx.NavigationService.CollectionItemSelectedIndex;
            if (col != null && reorderUpIdx >= 0 && reorderUpIdx < col.Items.Count)
            {
                var item = col.Items[reorderUpIdx];
                try
                {
                    using var scope = ctx.ScopeFactory.CreateScope();
                    var service = ctx.CreateCollectionService(scope);
                    await service.MoveItemUpAsync(col.Id, item.Id, ct).ConfigureAwait(false);
                    await ctx.RefreshCollectionsAsync(ct).ConfigureAwait(false);
                    var refreshedUp = ctx.NavigationService.ActiveCollection;
                    if (refreshedUp != null)
                    {
                        var newIdx = IndexOfItemById(refreshedUp.Items, item.Id);
                        ctx.NavigationService.CollectionItemSelectedIndex =
                            newIdx >= 0 ? newIdx : Math.Max(0, ctx.NavigationService.CollectionItemSelectedIndex - 1);
                    }
                }
                catch (Exception ex)
                {
                    // workspace-wyxx.1: reorder failures must not be silent.
                    ctx.Logger.LogWarning(ex, "Failed to move item up");
                    ctx.NavigationService.SetStatusMessage("Couldn't reorder", StatusSeverity.Error);
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleReorderDown(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (ctx.NavigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
        {
            var col = ctx.NavigationService.ActiveCollection;
            var reorderDownIdx = ctx.NavigationService.CollectionItemSelectedIndex;
            if (col != null && reorderDownIdx >= 0 && reorderDownIdx < col.Items.Count)
            {
                var item = col.Items[reorderDownIdx];
                try
                {
                    using var scope = ctx.ScopeFactory.CreateScope();
                    var service = ctx.CreateCollectionService(scope);
                    await service.MoveItemDownAsync(col.Id, item.Id, ct).ConfigureAwait(false);
                    await ctx.RefreshCollectionsAsync(ct).ConfigureAwait(false);

                    var refreshedDown = ctx.NavigationService.ActiveCollection;
                    if (refreshedDown != null)
                    {
                        var newIdx = IndexOfItemById(refreshedDown.Items, item.Id);
                        var maxIdx = Math.Max(0, refreshedDown.Items.Count - 1);
                        ctx.NavigationService.CollectionItemSelectedIndex =
                            newIdx >= 0 ? newIdx : Math.Min(maxIdx, ctx.NavigationService.CollectionItemSelectedIndex + 1);
                    }
                }
                catch (Exception ex)
                {
                    // workspace-wyxx.1: reorder failures must not be silent.
                    ctx.Logger.LogWarning(ex, "Failed to move item down");
                    ctx.NavigationService.SetStatusMessage("Couldn't reorder", StatusSeverity.Error);
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleOpenCollections(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            ctx.NavigationService.EnterCollections();
            await ctx.RefreshCollectionsAsync(ct).ConfigureAwait(false);

            // Auto-enter the sole collection to skip the list screen
            if (ctx.Collections is { Count: 1 })
            {
                ctx.NavigationService.EnterCollection(ctx.Collections[0]);
                ctx.PreloadService.EnableEagerMode();
            }

            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Failed to open collections");
            ctx.NavigationService.SetStatusMessage($"Failed to load collections: {ex.Message}", StatusSeverity.Error);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the Shift+S prompt, listing the existing collection names inline
    /// ("Save to collection [Reading List, Favorites]: ") so users reuse
    /// collections instead of accidentally creating new ones (workspace-yejq.1).
    /// Falls back to the bare prompt when there are no collections or the
    /// lookup fails.
    /// </summary>
    private static async Task<string> BuildSaveToSpecificPromptAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        const string barePrompt = "Save to collection: ";
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var service = ctx.CreateCollectionService(scope);
            var collections = await service.GetAllCollectionsAsync(ct).ConfigureAwait(false);
            if (collections is not { Count: > 0 })
            {
                return barePrompt;
            }

            // Leave room on the prompt line for the user's typing.
            var namesBudget = Math.Max(12, options.TerminalWidth - 40);
            var names = RenderHelpers.TruncateText(
                string.Join(", ", collections.Select(c => c.Name)), namesBudget);
            return $"Save to collection [{names}]: ";
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Couldn't list collection names for the save prompt");
            return barePrompt;
        }
    }

    /// <summary>
    /// Plays the save-success row flash on the captured screen position. The
    /// flash runs synchronously (~400ms), is skipped when animations are
    /// globally disabled, and is skipped if the selection moved during the
    /// save await (race protection). Shared by the `s` quick save and the
    /// Shift+S save-to-specific paths (workspace-yejq.5).
    /// </summary>
    private static void PlaySaveFlash(
        CommandContext ctx,
        RenderOptions options,
        (int Row, int Col)? flashPosition,
        string? flashText,
        Guid? savedNodeId)
    {
        if (flashPosition is not { } pos
            || string.IsNullOrEmpty(flashText)
            || ctx.DisableAnimations
            || !SelectionStillOnSavedNode(ctx, savedNodeId))
        {
            return;
        }

        try
        {
            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

            // Match the title-text width budget used by BuildSelectedCardLine —
            // truncate so the flash never spills past the card edge.
            var maxWidth = Math.Max(1, (options.TerminalWidth / 2) - 4);
            var displayText = RenderHelpers.TruncateText(flashText!, maxWidth);
            SaveFlashAnimation.PlayRow(displayText, pos.Row, pos.Col, palette);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Save flash animation failed");
        }
    }

    /// <summary>
    /// Computes the screen (row, col) of the currently-selected link tree row's
    /// title text, for use as a save-flash target. Returns null if not applicable.
    /// </summary>
    private static (int Row, int Col)? ComputeSelectedRowFlashPosition(
        NavigationTree tree,
        CommandContext ctx,
        RenderOptions options)
    {
        try
        {
            var visibleNodes = tree.GetVisibleNodes().ToList();
            var selectedNode = tree.GetSelectedNode();
            if (selectedNode == null)
            {
                return null;
            }

            var selectedIdx = -1;
            for (var i = 0; i < visibleNodes.Count; i++)
            {
                if (visibleNodes[i].Id == selectedNode.Id)
                {
                    selectedIdx = i;
                    break;
                }
            }

            if (selectedIdx < 0)
            {
                return null;
            }

            var layout = LinkTreeRenderer.ComputeLayout(options.TerminalWidth, options.TerminalHeight);
            var maxLines = ctx.GetHierarchicalViewportHeight(options) * Math.Max(1, layout.CellHeight);
            return LinkTreeRenderer.TryGetSelectedRowScreenPosition(
                visibleNodes,
                selectedIdx,
                ctx.NavigationService.CurrentContext.ScrollOffset,
                layout,
                maxLines);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Failed to compute save flash position");
            return null;
        }
    }

    /// <summary>
    /// Returns true when the currently-selected link tree node is still the
    /// same node that was saved — protects against the user pressing j/k
    /// while the save is in flight.
    /// </summary>
    private static bool SelectionStillOnSavedNode(CommandContext ctx, Guid? savedNodeId)
    {
        if (savedNodeId == null)
        {
            return false;
        }

        var current = ctx.NavigationService.CurrentPage?.LinkTree?.GetSelectedNode();
        return current != null && current.Id == savedNodeId.Value;
    }

    private static int IndexOfItemById(IReadOnlyList<Domain.Entities.Collections.CollectionItem> items, Guid id)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
