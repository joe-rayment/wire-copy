// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles collection-related commands: save, delete, reorder, open collections.
/// </summary>
internal static class CollectionCommandHandler
{
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
                    var savedItem = await service.SaveToDefaultCollectionAsync(
                        saveNode.Link.Url, saveNode.Link.DisplayText, ct);
                    if (savedItem != null)
                    {
                        ctx.Logger.LogInformation("Saved to default collection: {Title}", saveNode.Link.DisplayText);
                    }
                    else
                    {
                        ctx.Logger.LogWarning("Already in default collection: {Title}", saveNode.Link.DisplayText);
                    }
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Failed to save to default collection");
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
                        }
                        else
                        {
                            ctx.Logger.LogWarning(
                                "Already in collection '{Collection}': {Title}",
                                collectionName,
                                saveNode.Link.DisplayText);
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogWarning(ex, "Failed to save to collection");
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
            if (col != null && ctx.NavigationService.CollectionItemSelectedIndex < col.Items.Count)
            {
                var item = col.Items[ctx.NavigationService.CollectionItemSelectedIndex];
                try
                {
                    using var scope = ctx.ScopeFactory.CreateScope();
                    var service = ctx.CreateCollectionService(scope);
                    await service.RemoveItemAsync(col.Id, item.Id, ct);
                    await ctx.RefreshCollectionsAsync(ct);

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
                }
            }
        }
        else if (viewMode == ViewMode.CollectionList &&
                 ctx.Collections != null && ctx.NavigationService.CollectionSelectedIndex < ctx.Collections.Count)
        {
            var collection = ctx.Collections[ctx.NavigationService.CollectionSelectedIndex];
            try
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var service = ctx.CreateCollectionService(scope);
                await service.DeleteCollectionAsync(collection.Id, ct);
                await ctx.RefreshCollectionsAsync(ct);
                if (ctx.NavigationService.CollectionSelectedIndex >= ctx.Collections.Count)
                {
                    ctx.NavigationService.CollectionSelectedIndex = Math.Max(0, ctx.Collections.Count - 1);
                }
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to delete collection");
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleReorderUp(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (ctx.NavigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
        {
            var col = ctx.NavigationService.ActiveCollection;
            if (col != null && ctx.NavigationService.CollectionItemSelectedIndex < col.Items.Count)
            {
                var item = col.Items[ctx.NavigationService.CollectionItemSelectedIndex];
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
            if (col != null && ctx.NavigationService.CollectionItemSelectedIndex < col.Items.Count)
            {
                var item = col.Items[ctx.NavigationService.CollectionItemSelectedIndex];
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
            ctx.NavigationService.EnterCollections();
            await ctx.RefreshCollectionsAsync(ct);
            await ctx.RenderCurrentPageAsync(options, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Failed to enter collections mode");
            ctx.NavigationService.ExitCollections();
            await ctx.RenderCurrentPageAsync(options, ct);
        }
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
