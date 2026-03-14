// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.UI.Renderers;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles navigation commands: move up/down, page up/down, go to top/bottom,
/// go back/forward, expand/collapse nodes, activate links.
/// </summary>
internal static class NavigationCommandHandler
{
    public static async Task HandleMoveDown(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        if (viewMode == ViewMode.CollectionList)
        {
            var maxIdx = (ctx.Collections?.Count ?? 0) - 1;
            if (maxIdx >= 0)
            {
                ctx.NavigationService.CollectionSelectedIndex =
                    Math.Min(ctx.NavigationService.CollectionSelectedIndex + 1, maxIdx);
                AdjustCollectionListScroll(ctx, options);
            }
        }
        else if (viewMode == ViewMode.CollectionItems)
        {
            var activeCol = ctx.NavigationService.ActiveCollection;
            if (activeCol != null)
            {
                var maxItemIdx = activeCol.Items.Count - 1;
                if (maxItemIdx >= 0)
                {
                    ctx.NavigationService.CollectionItemSelectedIndex =
                        Math.Min(ctx.NavigationService.CollectionItemSelectedIndex + 1, maxItemIdx);
                    AdjustCollectionItemScroll(ctx, options);
                }
            }
        }
        else if (viewMode == ViewMode.Hierarchical)
        {
            MoveVerticalInGrid(tree, options, down: true);
            ctx.AdjustScrollForSelection(tree, options);
        }
        else
        {
            ctx.LineCacheManager.EnsureLineCache(options);
            var vpHeight = ctx.GetReaderViewportHeight(options);
            var maxOffset = Math.Max(0, (ctx.LineCacheManager.CachedLines?.Count ?? 0) - vpHeight);
            ctx.NavigationService.SetScrollOffset(
                Math.Min(ctx.NavigationService.CurrentContext.ScrollOffset + 1, maxOffset));
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleMoveUp(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        if (viewMode == ViewMode.CollectionList)
        {
            ctx.NavigationService.CollectionSelectedIndex =
                Math.Max(ctx.NavigationService.CollectionSelectedIndex - 1, 0);
            AdjustCollectionListScroll(ctx, options);
        }
        else if (viewMode == ViewMode.CollectionItems)
        {
            var activeCol = ctx.NavigationService.ActiveCollection;
            var minIdx = activeCol != null && activeCol.Items.Count > 0 ? -1 : 0;
            ctx.NavigationService.CollectionItemSelectedIndex =
                Math.Max(ctx.NavigationService.CollectionItemSelectedIndex - 1, minIdx);
            AdjustCollectionItemScroll(ctx, options);
        }
        else if (viewMode == ViewMode.Hierarchical)
        {
            MoveVerticalInGrid(tree, options, down: false);
            ctx.AdjustScrollForSelection(tree, options);
        }
        else
        {
            ctx.NavigationService.SetScrollOffset(
                Math.Max(0, ctx.NavigationService.CurrentContext.ScrollOffset - 1));
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandlePageDown(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        if (viewMode == ViewMode.CollectionList)
        {
            var maxVisible = CollectionRenderer.GetCollectionListVisibleCount(options.TerminalHeight);
            var halfPage = Math.Max(1, maxVisible / 2);
            var maxIdx = Math.Max(0, (ctx.Collections?.Count ?? 0) - 1);
            ctx.NavigationService.CollectionSelectedIndex =
                Math.Min(ctx.NavigationService.CollectionSelectedIndex + halfPage, maxIdx);
            AdjustCollectionListScroll(ctx, options);
        }
        else if (viewMode == ViewMode.CollectionItems)
        {
            var activeCol = ctx.NavigationService.ActiveCollection;
            if (activeCol != null)
            {
                var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(options.TerminalHeight);
                var halfPage = Math.Max(1, maxVisible / 2);
                var maxIdx = Math.Max(0, activeCol.Items.Count - 1);
                ctx.NavigationService.CollectionItemSelectedIndex =
                    Math.Min(ctx.NavigationService.CollectionItemSelectedIndex + halfPage, maxIdx);
                AdjustCollectionItemScroll(ctx, options);
            }
        }
        else if (viewMode == ViewMode.Readable)
        {
            ctx.LineCacheManager.EnsureLineCache(options);
            var vpHeight = ctx.GetReaderViewportHeight(options);
            var halfPage = Math.Max(1, vpHeight / 2);
            var maxOff = Math.Max(0, (ctx.LineCacheManager.CachedLines?.Count ?? 0) - vpHeight);
            ctx.NavigationService.SetScrollOffset(
                Math.Min(ctx.NavigationService.CurrentContext.ScrollOffset + halfPage, maxOff));
        }
        else if (viewMode == ViewMode.Hierarchical && tree != null)
        {
            var halfVp = Math.Max(1, ctx.GetHierarchicalViewportHeight(options) / 2);
            for (var i = 0; i < halfVp; i++)
            {
                tree.SelectNext();
            }

            ctx.AdjustScrollForSelection(tree, options);
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandlePageUp(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        if (viewMode == ViewMode.CollectionList)
        {
            var maxVisible = CollectionRenderer.GetCollectionListVisibleCount(options.TerminalHeight);
            var halfPage = Math.Max(1, maxVisible / 2);
            ctx.NavigationService.CollectionSelectedIndex =
                Math.Max(ctx.NavigationService.CollectionSelectedIndex - halfPage, 0);
            AdjustCollectionListScroll(ctx, options);
        }
        else if (viewMode == ViewMode.CollectionItems)
        {
            var activeCol = ctx.NavigationService.ActiveCollection;
            if (activeCol != null)
            {
                var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(options.TerminalHeight);
                var halfPage = Math.Max(1, maxVisible / 2);
                var minIdx = activeCol.Items.Count > 0 ? -1 : 0;
                ctx.NavigationService.CollectionItemSelectedIndex =
                    Math.Max(ctx.NavigationService.CollectionItemSelectedIndex - halfPage, minIdx);
                AdjustCollectionItemScroll(ctx, options);
            }
        }
        else if (viewMode == ViewMode.Readable)
        {
            ctx.LineCacheManager.EnsureLineCache(options);
            var vpHeight = ctx.GetReaderViewportHeight(options);
            var halfPage = Math.Max(1, vpHeight / 2);
            ctx.NavigationService.SetScrollOffset(
                Math.Max(0, ctx.NavigationService.CurrentContext.ScrollOffset - halfPage));
        }
        else if (viewMode == ViewMode.Hierarchical && tree != null)
        {
            var halfVp = Math.Max(1, ctx.GetHierarchicalViewportHeight(options) / 2);
            for (var i = 0; i < halfVp; i++)
            {
                tree.SelectPrevious();
            }

            ctx.AdjustScrollForSelection(tree, options);
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleGoToTop(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        if (viewMode == ViewMode.CollectionList)
        {
            ctx.NavigationService.CollectionSelectedIndex = 0;
            ctx.NavigationService.CollectionListScrollOffset = 0;
        }
        else if (viewMode == ViewMode.CollectionItems)
        {
            ctx.NavigationService.CollectionItemSelectedIndex = 0;
            ctx.NavigationService.CollectionItemScrollOffset = 0;
        }
        else
        {
            ctx.NavigationService.SetScrollOffset(0);
            if (viewMode == ViewMode.Hierarchical && tree != null)
            {
                var firstNode = tree.GetVisibleNodes().FirstOrDefault();
                if (firstNode != null)
                {
                    tree.SelectNodeById(firstNode.Id);
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleGoToBottom(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        var page = ctx.NavigationService.CurrentPage;
        var tree = page?.LinkTree;

        if (viewMode == ViewMode.CollectionList)
        {
            var maxIdx = Math.Max(0, (ctx.Collections?.Count ?? 0) - 1);
            ctx.NavigationService.CollectionSelectedIndex = maxIdx;
            AdjustCollectionListScroll(ctx, options);
        }
        else if (viewMode == ViewMode.CollectionItems)
        {
            var activeCol = ctx.NavigationService.ActiveCollection;
            if (activeCol != null)
            {
                ctx.NavigationService.CollectionItemSelectedIndex = Math.Max(0, activeCol.Items.Count - 1);
                AdjustCollectionItemScroll(ctx, options);
            }
        }
        else if (viewMode == ViewMode.Readable && page?.ReadableContent != null)
        {
            ctx.LineCacheManager.EnsureLineCache(options);
            var vpHeight = ctx.GetReaderViewportHeight(options);
            ctx.NavigationService.SetScrollOffset(
                Math.Max(0, (ctx.LineCacheManager.CachedLines?.Count ?? 0) - vpHeight));
        }
        else if (tree != null)
        {
            var lastNode = tree.GetVisibleNodes().LastOrDefault();
            if (lastNode != null)
            {
                tree.SelectNodeById(lastNode.Id);
                ctx.AdjustScrollForSelection(tree, options);
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleGoBack(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;

        if (viewMode == ViewMode.CollectionItems)
        {
            ctx.NavigationService.ExitToCollectionList();
            ctx.LineCacheManager.InvalidateLineCache();
            await ctx.RenderCurrentPageAsync(options, ct);
        }
        else if (viewMode == ViewMode.CollectionList)
        {
            ctx.NavigationService.ExitCollections();
            ctx.LineCacheManager.InvalidateLineCache();
            await ctx.RenderCurrentPageAsync(options, ct);
        }
        else if (ctx.NavigationService.TryRestoreCollectionReturnPoint())
        {
            await ctx.RefreshCollectionsAsync(ct);
            await ctx.RenderCurrentPageAsync(options, ct);
        }
        else
        {
            var previousPage = ctx.NavigationService.GoBack();
            if (previousPage != null)
            {
                ctx.LineCacheManager.InvalidateLineCache();
                await ctx.RenderCurrentPageAsync(options, ct);
            }
            else
            {
                ctx.NavigationService.EnterLauncher();
                await ctx.RefreshBookmarksAsync(ct);
                await ctx.RenderCurrentPageAsync(options, ct);
            }
        }
    }

    public static async Task HandleGoForward(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var nextPage = ctx.NavigationService.GoForward();
        if (nextPage != null)
        {
            ctx.LineCacheManager.InvalidateLineCache();
            await ctx.RenderCurrentPageAsync(options, ct);
        }
    }

    public static async Task HandleExpandNode(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;
        var selected = tree?.CurrentSelection;

        if (selected != null && !selected.IsGroupHeader && ctx.NavigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
        {
            MoveHorizontalInGrid(tree!, options, right: true);
        }
        else
        {
            selected?.Expand();
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleCollapseNode(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;
        var selected = tree?.CurrentSelection;

        if (selected != null && !selected.IsGroupHeader && ctx.NavigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
        {
            MoveHorizontalInGrid(tree!, options, right: false);
        }
        else
        {
            selected?.Collapse();
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleToggleNode(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        ctx.NavigationService.CurrentPage?.LinkTree?.ToggleCollapse();
        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleActivateLink(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        if (viewMode == ViewMode.CollectionList)
        {
            if (ctx.Collections != null && ctx.NavigationService.CollectionSelectedIndex < ctx.Collections.Count)
            {
                var selectedCollection = ctx.Collections[ctx.NavigationService.CollectionSelectedIndex];
                ctx.NavigationService.EnterCollection(selectedCollection);
                await ctx.RenderCurrentPageAsync(options, ct);
            }
        }
        else if (viewMode == ViewMode.CollectionItems)
        {
            // CTA button focused — trigger podcast generation
            if (ctx.NavigationService.CollectionItemSelectedIndex == -1)
            {
                await PodcastCommandHandler.HandleGeneratePodcast(ctx, options, ct);
                return;
            }

            var activeCol = ctx.NavigationService.ActiveCollection;
            var activateIdx = ctx.NavigationService.CollectionItemSelectedIndex;
            if (activeCol != null && activateIdx >= 0 && activateIdx < activeCol.Items.Count)
            {
                var selectedItem = activeCol.Items[activateIdx];
                ctx.NavigationService.SaveCollectionReturnPoint();
                await ctx.NavigateToAsync(selectedItem.Url, options, ct);

                // Mark item as read
                try
                {
                    using var markScope = ctx.ScopeFactory.CreateScope();
                    var markService = ctx.CreateCollectionService(markScope);
                    await markService.MarkItemAsReadAsync(activeCol.Id, selectedItem.Id, ct);
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Failed to mark item as read");
                }

                // Default to reader view if the page has readable content
                if (ctx.NavigationService.CurrentPage?.HasReadableContent() == true)
                {
                    ctx.NavigationService.SetViewMode(ViewMode.Readable);
                    ctx.LineCacheManager.InvalidateLineCache();
                    await ctx.RenderCurrentPageAsync(options, ct);
                }
            }
        }
        else
        {
            var selectedNode = tree?.GetSelectedNode();
            if (selectedNode != null)
            {
                if (selectedNode.IsGroupHeader)
                {
                    selectedNode.ToggleCollapse();
                    await ctx.RenderCurrentPageAsync(options, ct);
                }
                else if (!string.IsNullOrEmpty(selectedNode.Link.Url))
                {
                    await ctx.NavigateToAsync(selectedNode.Link.Url, options, ct);

                    if (ctx.NavigationService.CurrentPage?.HasReadableContent() == true)
                    {
                        ctx.NavigationService.SetViewMode(ViewMode.Readable);
                        ctx.LineCacheManager.InvalidateLineCache();
                        await ctx.RenderCurrentPageAsync(options, ct);
                    }
                }
            }
        }
    }

    public static async Task HandleRefresh(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var page = ctx.NavigationService.CurrentPage;
        if (page != null)
        {
            var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
            await ctx.NavigateToAsync(page.Url, options, ct);
            ctx.NavigationService.SetViewMode(viewMode);
        }
    }

    public static async Task HandleForceRefresh(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var page = ctx.NavigationService.CurrentPage;
        if (page != null)
        {
            await ctx.ForceRefreshAsync(page.Url, options, ct);
        }
    }

    public static async Task HandleInteractiveRefresh(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var page = ctx.NavigationService.CurrentPage;
        if (page != null)
        {
            await ctx.InteractiveRefreshAsync(page.Url, options, ct);
        }
    }

    public static async Task HandleNavigate(CommandContext ctx, NavigationCommand command, RenderOptions options, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(command.TargetUrl))
        {
            await ctx.NavigateToAsync(command.TargetUrl, options, ct);
        }
    }

    private static void MoveVerticalInGrid(Domain.Entities.Browser.NavigationTree? tree, RenderOptions options, bool down)
    {
        if (tree == null)
        {
            return;
        }

        var selected = tree.CurrentSelection;
        if (selected == null)
        {
            return;
        }

        // Group headers: use sequential movement (no column concept)
        if (selected.IsGroupHeader)
        {
            if (down)
            {
                tree.SelectNext();
            }
            else
            {
                tree.SelectPrevious();
            }

            return;
        }

        var visibleNodes = tree.GetVisibleNodes().ToList();
        var selectedIndex = visibleNodes.IndexOf(selected);
        if (selectedIndex < 0)
        {
            return;
        }

        var layout = LinkTreeRenderer.ComputeLayout(options.TerminalWidth, options.TerminalHeight);
        var gridRows = LinkTreeGridMapper.MapToGrid(visibleNodes, layout.Columns);
        var (row, col) = LinkTreeGridMapper.NodeIndexToGridPosition(gridRows, selectedIndex);

        var newNodeIndex = down
            ? LinkTreeGridMapper.MoveDown(gridRows, row, col)
            : LinkTreeGridMapper.MoveUp(gridRows, row, col);

        if (newNodeIndex >= 0 && newNodeIndex < visibleNodes.Count)
        {
            tree.SelectNodeById(visibleNodes[newNodeIndex].Id);
        }
    }

    private static void MoveHorizontalInGrid(Domain.Entities.Browser.NavigationTree tree, RenderOptions options, bool right)
    {
        var selected = tree.CurrentSelection;
        if (selected == null)
        {
            return;
        }

        var visibleNodes = tree.GetVisibleNodes().ToList();
        var selectedIndex = visibleNodes.IndexOf(selected);
        if (selectedIndex < 0)
        {
            return;
        }

        var layout = LinkTreeRenderer.ComputeLayout(options.TerminalWidth, options.TerminalHeight);
        if (layout.Columns < 2)
        {
            return;
        }

        var gridRows = LinkTreeGridMapper.MapToGrid(visibleNodes, layout.Columns);
        var (row, col) = LinkTreeGridMapper.NodeIndexToGridPosition(gridRows, selectedIndex);

        int targetCol;
        if (right)
        {
            targetCol = 1;
            if (col == 1 || row >= gridRows.Count || gridRows[row].Right == null)
            {
                return;
            }
        }
        else
        {
            targetCol = 0;
            if (col == 0)
            {
                return;
            }
        }

        var newNodeIndex = LinkTreeGridMapper.GridPositionToNodeIndex(gridRows, row, targetCol);
        if (newNodeIndex >= 0 && newNodeIndex < visibleNodes.Count)
        {
            tree.SelectNodeById(visibleNodes[newNodeIndex].Id);
        }
    }

    private static void AdjustCollectionListScroll(CommandContext ctx, RenderOptions options)
    {
        var maxVisible = CollectionRenderer.GetCollectionListVisibleCount(options.TerminalHeight);
        var selectedIndex = ctx.NavigationService.CollectionSelectedIndex;
        var currentOffset = ctx.NavigationService.CollectionListScrollOffset;

        if (selectedIndex < currentOffset)
        {
            ctx.NavigationService.CollectionListScrollOffset = selectedIndex;
        }
        else if (selectedIndex >= currentOffset + maxVisible)
        {
            ctx.NavigationService.CollectionListScrollOffset = selectedIndex - maxVisible + 1;
        }
    }

    private static void AdjustCollectionItemScroll(CommandContext ctx, RenderOptions options)
    {
        var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(options.TerminalHeight);
        var selectedIndex = ctx.NavigationService.CollectionItemSelectedIndex;
        var currentOffset = ctx.NavigationService.CollectionItemScrollOffset;

        if (selectedIndex < currentOffset)
        {
            ctx.NavigationService.CollectionItemScrollOffset = selectedIndex;
        }
        else if (selectedIndex >= currentOffset + maxVisible)
        {
            ctx.NavigationService.CollectionItemScrollOffset = selectedIndex - maxVisible + 1;
        }
    }
}
