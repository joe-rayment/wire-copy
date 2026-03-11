// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Renderers;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles collection-related commands: save, delete, reorder, open collections.
/// </summary>
internal static class CollectionCommandHandler
{
    private const string Reset = "\x1b[0m";

    public static async Task HandleSaveToCollection(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        if (viewMode == ViewMode.Hierarchical)
        {
            var saveNode = tree?.GetSelectedNode();
            if (saveNode != null && !saveNode.IsGroupHeader && !string.IsNullOrEmpty(saveNode.Link.Url))
            {
                try
                {
                    using var scope = ctx.ScopeFactory.CreateScope();
                    var service = ctx.CreateCollectionService(scope);
                    await service.SaveToReadingListAsync(
                        saveNode.Link.Url, saveNode.Link.DisplayText, ct);
                    ctx.Logger.LogInformation("Saved to Reading List: {Title}", saveNode.Link.DisplayText);
                    ctx.NavigationService.SetStatusMessage($"Saved: {saveNode.Link.DisplayText}");
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Failed to save to default collection");
                    ctx.NavigationService.SetStatusMessage("Failed to save");
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
                await service.SetDefaultCollectionAsync(col.Id, ct);
                ctx.DefaultCollectionId = col.Id;
                ctx.Logger.LogInformation("Set default collection: {Name}", col.Name);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to set default collection");
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleSaveToSpecific(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        if (ctx.NavigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
        {
            var saveNode = tree?.GetSelectedNode();
            if (saveNode != null && !saveNode.IsGroupHeader && !string.IsNullOrEmpty(saveNode.Link.Url))
            {
                var collectionName = await ctx.InputHandler.PromptForInputAsync("Save to collection: ", ct);
                if (!string.IsNullOrWhiteSpace(collectionName))
                {
                    try
                    {
                        using var scope = ctx.ScopeFactory.CreateScope();
                        var service = ctx.CreateCollectionService(scope);
                        var savedSpecific = await service.SaveToCollectionByNameAsync(
                            collectionName, saveNode.Link.Url, saveNode.Link.DisplayText, ct);
                        if (savedSpecific != null)
                        {
                            ctx.Logger.LogInformation(
                                "Saved to collection '{Collection}': {Title}",
                                collectionName,
                                saveNode.Link.DisplayText);
                            ctx.NavigationService.SetStatusMessage(
                                $"Saved to {collectionName}: {saveNode.Link.DisplayText}");
                        }
                        else
                        {
                            ctx.Logger.LogWarning(
                                "Already in collection '{Collection}': {Title}",
                                collectionName,
                                saveNode.Link.DisplayText);
                            ctx.NavigationService.SetStatusMessage(
                                $"Already in {collectionName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogWarning(ex, "Failed to save to collection");
                        ctx.NavigationService.SetStatusMessage("Failed to save");
                    }
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleSaveAllToReadingList(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (ctx.NavigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
        {
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
                        await service.SaveAllToReadingListAsync(visibleNodes, ct);
                        ctx.Logger.LogInformation("Saved {Count} links to Reading List", visibleNodes.Count);
                        ctx.NavigationService.SetStatusMessage(
                            $"Saved {visibleNodes.Count} links to Reading List");
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogWarning(ex, "Failed to save all to Reading List");
                        ctx.NavigationService.SetStatusMessage("Failed to save");
                    }
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleDeleteItem(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;

        if (viewMode == ViewMode.CollectionItems)
        {
            var col = ctx.NavigationService.ActiveCollection;
            var deleteIdx = ctx.NavigationService.CollectionItemSelectedIndex;
            if (col != null && deleteIdx >= 0 && deleteIdx < col.Items.Count)
            {
                var item = col.Items[deleteIdx];
                var confirm = await ctx.InputHandler.PromptForInputAsync(
                    $"Remove \"{item.Title}\"? (y/n): ", ct);
                if (!string.Equals(confirm, "y", StringComparison.OrdinalIgnoreCase))
                {
                    await ctx.RenderCurrentPageAsync(options, ct);
                    return;
                }

                try
                {
                    using var scope = ctx.ScopeFactory.CreateScope();
                    var service = ctx.CreateCollectionService(scope);
                    await service.RemoveItemAsync(col.Id, item.Id, ct);
                    await ctx.RefreshCollectionsAsync(ct);
                    ctx.NavigationService.SetStatusMessage($"Removed: {item.Title}");

                    var refreshedCol = ctx.NavigationService.ActiveCollection;
                    var refreshedCount = refreshedCol?.Items.Count ?? 0;
                    if (ctx.NavigationService.CollectionItemSelectedIndex >= refreshedCount)
                    {
                        ctx.NavigationService.CollectionItemSelectedIndex = Math.Max(0, refreshedCount - 1);
                    }
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Failed to remove item");
                    ctx.NavigationService.SetStatusMessage("Failed to remove");
                }
            }
        }
        else if (viewMode == ViewMode.CollectionList &&
                 ctx.Collections != null && ctx.NavigationService.CollectionSelectedIndex < ctx.Collections.Count)
        {
            var collection = ctx.Collections[ctx.NavigationService.CollectionSelectedIndex];
            var confirm = await ctx.InputHandler.PromptForInputAsync(
                $"Delete collection \"{collection.Name}\"? (y/n): ", ct);
            if (!string.Equals(confirm, "y", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            try
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var service = ctx.CreateCollectionService(scope);
                await service.DeleteCollectionAsync(collection.Id, ct);
                await ctx.RefreshCollectionsAsync(ct);
                ctx.NavigationService.SetStatusMessage($"Deleted collection: {collection.Name}");
                if (ctx.NavigationService.CollectionSelectedIndex >= ctx.Collections.Count)
                {
                    ctx.NavigationService.CollectionSelectedIndex = Math.Max(0, ctx.Collections.Count - 1);
                }
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to delete collection");
                ctx.NavigationService.SetStatusMessage("Failed to delete");
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleClearCollection(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (ctx.NavigationService.CurrentContext.ViewMode != ViewMode.CollectionItems)
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var col = ctx.NavigationService.ActiveCollection;
        if (col == null || col.Items.Count == 0)
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var confirmed = await ShowClearConfirmationAsync(ctx, options, col, ct);
        if (!confirmed)
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var service = ctx.CreateCollectionService(scope);
            await service.ClearCollectionAsync(col.Id, ct);
            await ctx.RefreshCollectionsAsync(ct);
            ctx.NavigationService.CollectionItemSelectedIndex = 0;
            ctx.Logger.LogInformation("Cleared collection: {Name}", col.Name);
            ctx.NavigationService.SetStatusMessage($"Cleared: {col.Name}");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to clear collection");
            ctx.NavigationService.SetStatusMessage("Failed to clear");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
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
                    await service.MoveItemUpAsync(col.Id, item.Id, ct);
                    await ctx.RefreshCollectionsAsync(ct);
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
                    ctx.Logger.LogWarning(ex, "Failed to move item up");
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
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
                    await service.MoveItemDownAsync(col.Id, item.Id, ct);
                    await ctx.RefreshCollectionsAsync(ct);

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
                    ctx.Logger.LogWarning(ex, "Failed to move item down");
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleOpenCollections(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var service = ctx.CreateCollectionService(scope);

            // Purge expired Reading List items (16-hour auto-expiry) — non-critical
            try
            {
                var purged = await service.PurgeExpiredReadingListItemsAsync(TimeSpan.FromHours(16), ct);
                if (purged > 0)
                {
                    ctx.Logger.LogInformation("Purged {Count} expired Reading List items", purged);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to purge expired Reading List items; continuing");
            }

            // Go directly to Reading List items view (skip CollectionList)
            var readingList = await service.GetReadingListAsync(ct);
            ctx.NavigationService.EnterCollections();
            ctx.NavigationService.EnterCollection(readingList);
            await ctx.RefreshCollectionsAsync(ct);
            ctx.PreloadService.EnableEagerMode();
            await ctx.RenderCurrentPageAsync(options, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Failed to open Reading List");
            ctx.NavigationService.SetStatusMessage($"Failed to load collections: {ex.Message}");
            await ctx.RenderCurrentPageAsync(options, ct);
        }
    }

    private static async Task<bool> ShowClearConfirmationAsync(
        CommandContext ctx,
        RenderOptions options,
        Domain.Entities.Collections.Collection collection,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);

            // Title box (error-colored for destructive action)
            helpers.WriteLine();
            helpers.WriteLine($"{p.ErrorFg.AnsiFg}\u256d{new string('\u2500', width - 2)}\u256e{Reset}");
            var title = RenderHelpers.TruncateText("Clear Collection", width - 4);
            helpers.WriteLine(
                $"{p.ErrorFg.AnsiFg}\u2502 {title.PadRight(width - 4)} \u2502{Reset}");
            helpers.WriteLine($"{p.ErrorFg.AnsiFg}\u2570{new string('\u2500', width - 2)}\u256f{Reset}");
            helpers.WriteLine();

            // Warning
            helpers.WriteLine(
                $"  {p.ErrorFg.AnsiFg}\u26a0  This will permanently remove all {collection.Items.Count} " +
                $"article(s) from \"{collection.Name}\".{Reset}");
            helpers.WriteLine();

            // Article list
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Articles to remove:{Reset}");

            // Reserve lines for prompt area (instruction + prompt + padding)
            var maxArticleLines = Math.Max(1, options.TerminalHeight - helpers.LinesWritten - 5);
            var articleCount = Math.Min(collection.Items.Count, maxArticleLines);

            for (var i = 0; i < articleCount; i++)
            {
                var item = collection.Items[i];
                var displayTitle = RenderHelpers.TruncateText(item.Title, width - 8);
                helpers.WriteLine($"    {p.ErrorFg.AnsiFg}\u2022{Reset} {p.PrimaryText.AnsiFg}{displayTitle}{Reset}");
            }

            if (collection.Items.Count > articleCount)
            {
                helpers.WriteLine(
                    $"    {p.SecondaryText.AnsiFg}... and {collection.Items.Count - articleCount} more{Reset}");
            }

            helpers.WriteLine();
            helpers.WriteLine(
                $"  {p.SecondaryText.AnsiFg}Type {p.ErrorFg.AnsiFg}clear{p.SecondaryText.AnsiFg} to confirm, " +
                $"or press {p.PrimaryText.AnsiFg}Esc{p.SecondaryText.AnsiFg} to cancel{Reset}");
            helpers.ClearRemainingLines();

            // Text input at the bottom of the screen
            var response = await ctx.InputHandler.PromptForInputAsync("> ", ct);

            if (response == null)
            {
                // Escape pressed
                return false;
            }

            if (string.Equals(response.Trim(), "clear", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Wrong input — loop to re-render the modal
        }

        return false;
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
