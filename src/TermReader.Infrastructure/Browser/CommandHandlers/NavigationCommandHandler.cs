// Licensed under the MIT License. See LICENSE in the repository root.

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
    public static async Task HandleMoveDown(CommandContext ctx, NavigationCommand command, RenderOptions options, CancellationToken ct)
    {
        var count = Math.Max(1, command.Count);
        for (var i = 0; i < count; i++)
        {
            MoveDownOnce(ctx, options);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleMoveUp(CommandContext ctx, NavigationCommand command, RenderOptions options, CancellationToken ct)
    {
        var count = Math.Max(1, command.Count);
        for (var i = 0; i < count; i++)
        {
            MoveUpOnce(ctx, options);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
                var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(options.TerminalHeight, options.TerminalWidth, string.Equals(options.LayoutVariant, "Compact", StringComparison.Ordinal));
                var halfPage = Math.Max(1, maxVisible / 2);
                var maxIdx = Math.Max(0, activeCol.Items.Count - 1);
                ctx.NavigationService.CollectionItemSelectedIndex =
                    Math.Min(ctx.NavigationService.CollectionItemSelectedIndex + halfPage, maxIdx);
                AdjustCollectionItemScroll(ctx, options);
            }
        }
        else if (viewMode == ViewMode.Readable)
        {
            var vpHeight = ctx.GetReaderViewportHeight(options);
            MoveReaderCursor(ctx, options, Math.Max(1, vpHeight / 2));
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

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
                var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(options.TerminalHeight, options.TerminalWidth, string.Equals(options.LayoutVariant, "Compact", StringComparison.Ordinal));
                var halfPage = Math.Max(1, maxVisible / 2);
                var minIdx = activeCol.Items.Count > 0 ? -1 : 0;
                ctx.NavigationService.CollectionItemSelectedIndex =
                    Math.Max(ctx.NavigationService.CollectionItemSelectedIndex - halfPage, minIdx);
                AdjustCollectionItemScroll(ctx, options);
            }
        }
        else if (viewMode == ViewMode.Readable)
        {
            var vpHeight = ctx.GetReaderViewportHeight(options);
            MoveReaderCursor(ctx, options, -Math.Max(1, vpHeight / 2));
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

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleGoToTop(CommandContext ctx, NavigationCommand command, RenderOptions options, CancellationToken ct)
    {
        // <number>gg jumps to that index (1-based)
        if (command.Count > 0)
        {
            GoToIndex(ctx, command.Count - 1, options);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

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
        else if (viewMode == ViewMode.Readable)
        {
            ctx.NavigationService.SetReaderCursorLine(0);
            ctx.NavigationService.SetScrollOffset(0);
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

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleGoToBottom(CommandContext ctx, NavigationCommand command, RenderOptions options, CancellationToken ct)
    {
        // <number>G jumps to that index (1-based)
        if (command.Count > 0)
        {
            GoToIndex(ctx, command.Count - 1, options);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

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
            var totalLines = ctx.LineCacheManager.CachedLines?.Count ?? 0;
            var vpHeight = ctx.GetReaderViewportHeight(options);
            var lastLine = Math.Max(0, totalLines - 1);

            // Find the last non-blank content line
            var lines = ctx.LineCacheManager.CachedLines;
            while (lastLine > 0 && lines != null && string.IsNullOrEmpty(lines[lastLine]))
            {
                lastLine--;
            }

            ctx.NavigationService.SetReaderCursorLine(lastLine);
            ctx.NavigationService.SetScrollOffset(Math.Max(0, totalLines - vpHeight));
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

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleGoBack(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;

        // Esc with active selections: clear selection instead of navigating back (Vim/Helix pattern)
        if (viewMode == ViewMode.Hierarchical)
        {
            var tree = ctx.NavigationService.CurrentPage?.LinkTree;
            if (tree != null && tree.SelectionCount > 0)
            {
                tree.ClearSelection();
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }
        }

        // If there's no current page (e.g., after a failed navigation that rendered an error page),
        // go straight to the launcher to recover gracefully.
        if (ctx.NavigationService.CurrentPage == null && viewMode != ViewMode.Launcher
            && viewMode != ViewMode.CollectionList && viewMode != ViewMode.CollectionItems)
        {
            ctx.NavigationService.EnterLauncher();
            await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        if (viewMode == ViewMode.CollectionItems)
        {
            // Skip the collection list when only 1 collection exists — go straight back
            if (ctx.Collections is not { Count: > 1 })
            {
                ctx.NavigationService.ExitCollections();
            }
            else
            {
                ctx.NavigationService.ExitToCollectionList();
            }

            ctx.LineCacheManager.InvalidateLineCache();
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
        else if (viewMode == ViewMode.CollectionList)
        {
            ctx.NavigationService.ExitCollections();
            ctx.LineCacheManager.InvalidateLineCache();
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
        else if (ctx.NavigationService.TryRestoreCollectionReturnPoint())
        {
            // Navigated from a collection (any view mode) — restore collection state
            await ctx.RefreshCollectionsAsync(ct).ConfigureAwait(false);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
        else if (ctx.NavigationService.CanGoBack)
        {
            // Has history — go back to previous page (works for both Readable and Hierarchical)
            ctx.NavigationService.GoBack();
            ctx.LineCacheManager.InvalidateLineCache();
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
        else if (viewMode == ViewMode.Readable)
        {
            // Readable view with no history — fall back to link view of same page
            ctx.NavigationService.SetViewMode(ViewMode.Hierarchical);
            ctx.LineCacheManager.InvalidateLineCache();
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
        else
        {
            // No history, not in readable — go to launcher
            ctx.NavigationService.EnterLauncher();
            await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
    }

    public static async Task HandleGoForward(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var nextPage = ctx.NavigationService.GoForward();
        if (nextPage != null)
        {
            ctx.LineCacheManager.InvalidateLineCache();
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
    }

    public static async Task HandleExpandNode(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        // In reader view, l/→ widens the reading column (documented in help)
        if (ctx.NavigationService.CurrentContext.ViewMode == ViewMode.Readable)
        {
            await ViewCommandHandler.HandleIncreaseWidth(ctx, options, ct).ConfigureAwait(false);
            return;
        }

        var tree = ctx.NavigationService.CurrentPage?.LinkTree;
        var selected = tree?.CurrentSelection;

        if (selected != null && !selected.IsGroupHeader && ctx.NavigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
        {
            MoveHorizontalInGrid(tree!, options, right: true);
        }
        else
        {
            tree?.Expand();
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleCollapseNode(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        // In reader view, h/← narrows the reading column (documented in help)
        if (ctx.NavigationService.CurrentContext.ViewMode == ViewMode.Readable)
        {
            await ViewCommandHandler.HandleDecreaseWidth(ctx, options, ct).ConfigureAwait(false);
            return;
        }

        var tree = ctx.NavigationService.CurrentPage?.LinkTree;
        var selected = tree?.CurrentSelection;

        if (selected != null && !selected.IsGroupHeader && ctx.NavigationService.CurrentContext.ViewMode == ViewMode.Hierarchical)
        {
            MoveHorizontalInGrid(tree!, options, right: false);
        }
        else
        {
            tree?.Collapse();
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleToggleNode(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        ctx.NavigationService.CurrentPage?.LinkTree?.ToggleCollapse();
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleToggleSelection(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;
        if (tree == null)
        {
            return;
        }

        tree.ToggleCurrentNodeSelection();
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
                ctx.PreloadService.EnableEagerMode();

                // Trigger preloading of collection items immediately (don't wait for cursor move)
                var urls = selectedCollection.Items.Select(item => item.Url).ToList();
                if (urls.Count > 0)
                {
                    ctx.PreloadService.NotifyCollectionChanged(0, urls);
                }

                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            }
        }
        else if (viewMode == ViewMode.CollectionItems)
        {
            // CTA button focused — trigger podcast generation
            if (ctx.NavigationService.CollectionItemSelectedIndex == -1)
            {
                await PodcastCommandHandler.HandleGeneratePodcast(ctx, options, ct).ConfigureAwait(false);
                return;
            }

            var activeCol = ctx.NavigationService.ActiveCollection;
            var activateIdx = ctx.NavigationService.CollectionItemSelectedIndex;
            if (activeCol != null && activateIdx >= 0 && activateIdx < activeCol.Items.Count)
            {
                var selectedItem = activeCol.Items[activateIdx];
                ctx.NavigationService.SaveCollectionReturnPoint();
                await ctx.NavigateToAsync(selectedItem.Url, options, ct).ConfigureAwait(false);

                // Mark item as read
                try
                {
                    using var markScope = ctx.ScopeFactory.CreateScope();
                    var markService = ctx.CreateCollectionService(markScope);
                    await markService.MarkItemAsReadAsync(activeCol.Id, selectedItem.Id, ct).ConfigureAwait(false);
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
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
                    tree!.ToggleCollapse();
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(selectedNode.Link.Url))
                {
                    await ctx.NavigateToAsync(selectedNode.Link.Url, options, ct).ConfigureAwait(false);

                    if (ctx.NavigationService.CurrentPage?.HasReadableContent() == true)
                    {
                        ctx.NavigationService.SetViewMode(ViewMode.Readable);
                        ctx.LineCacheManager.InvalidateLineCache();
                        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    public static async Task HandleRefresh(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        // F5 refresh always fetches from network (same as Shift+R).
        // Previously this called NavigateToAsync which served from cache.
        var page = ctx.NavigationService.CurrentPage;
        if (page != null)
        {
            await ctx.ForceRefreshAsync(page.Url, options, ct).ConfigureAwait(false);
        }
    }

    public static Task HandleForceRefresh(CommandContext ctx, RenderOptions options, CancellationToken ct)
        => HandleRefresh(ctx, options, ct);

    public static async Task HandleInteractiveRefresh(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var page = ctx.NavigationService.CurrentPage;
        if (page != null)
        {
            await ctx.InteractiveRefreshAsync(page.Url, options, ct).ConfigureAwait(false);
        }
    }

    public static async Task HandleNavigate(CommandContext ctx, NavigationCommand command, RenderOptions options, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(command.TargetUrl))
        {
            await ctx.NavigateToAsync(command.TargetUrl, options, ct).ConfigureAwait(false);
        }
    }

    public static async Task HandleParagraphDown(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        if (viewMode == ViewMode.Readable)
        {
            ctx.LineCacheManager.EnsureLineCache(options);
            var spans = ctx.LineCacheManager.ParagraphSpans;
            if (spans != null && spans.Count > 0)
            {
                var cursor = ctx.NavigationService.ReaderCursorLine;

                // Find the next paragraph after the current cursor position
                var nextStart = spans.Select(span => span.StartLine)
                    .FirstOrDefault(startLine => startLine > cursor);
                if (nextStart > cursor)
                {
                    ctx.NavigationService.SetReaderCursorLine(nextStart);
                    AdjustScrollForCursor(ctx, options);
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleParagraphUp(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        if (viewMode == ViewMode.Readable)
        {
            ctx.LineCacheManager.EnsureLineCache(options);
            var spans = ctx.LineCacheManager.ParagraphSpans;
            if (spans != null && spans.Count > 0)
            {
                var cursor = ctx.NavigationService.ReaderCursorLine;

                // Find the previous paragraph before the current cursor position
                for (var i = spans.Count - 1; i >= 0; i--)
                {
                    if (spans[i].StartLine < cursor)
                    {
                        ctx.NavigationService.SetReaderCursorLine(spans[i].StartLine);
                        AdjustScrollForCursor(ctx, options);
                        break;
                    }
                }
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static void MoveDownOnce(CommandContext ctx, RenderOptions options)
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
            MoveReaderCursor(ctx, options, 1);
        }
    }

    private static void MoveUpOnce(CommandContext ctx, RenderOptions options)
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
            MoveReaderCursor(ctx, options, -1);
        }
    }

    private static void GoToIndex(CommandContext ctx, int index, RenderOptions options)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;

        if (viewMode == ViewMode.Hierarchical && tree != null)
        {
            var visibleNodes = tree.GetVisibleNodes().ToList();
            var clampedIndex = Math.Clamp(index, 0, Math.Max(0, visibleNodes.Count - 1));
            if (clampedIndex < visibleNodes.Count)
            {
                tree.SelectNodeById(visibleNodes[clampedIndex].Id);
                ctx.AdjustScrollForSelection(tree, options);
            }
        }
        else if (viewMode == ViewMode.CollectionList)
        {
            var maxIdx = Math.Max(0, (ctx.Collections?.Count ?? 0) - 1);
            ctx.NavigationService.CollectionSelectedIndex = Math.Clamp(index, 0, maxIdx);
            AdjustCollectionListScroll(ctx, options);
        }
        else if (viewMode == ViewMode.CollectionItems)
        {
            var activeCol = ctx.NavigationService.ActiveCollection;
            if (activeCol != null)
            {
                var maxItemIdx = Math.Max(0, activeCol.Items.Count - 1);
                ctx.NavigationService.CollectionItemSelectedIndex = Math.Clamp(index, 0, maxItemIdx);
                AdjustCollectionItemScroll(ctx, options);
            }
        }
        else if (viewMode == ViewMode.Readable)
        {
            ctx.LineCacheManager.EnsureLineCache(options);
            var totalLines = ctx.LineCacheManager.CachedLines?.Count ?? 0;
            var clampedLine = Math.Clamp(index, 0, Math.Max(0, totalLines - 1));
            ctx.NavigationService.SetReaderCursorLine(clampedLine);

            var vpHeight = ctx.GetReaderViewportHeight(options);
            var maxScroll = Math.Max(0, totalLines - vpHeight);
            ctx.NavigationService.SetScrollOffset(Math.Clamp(clampedLine - (vpHeight / 2), 0, maxScroll));
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
        var maxVisible = CollectionRenderer.GetCollectionItemsVisibleCount(options.TerminalHeight, options.TerminalWidth, string.Equals(options.LayoutVariant, "Compact", StringComparison.Ordinal));
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

    /// <summary>
    /// Moves the reader cursor by delta lines, skipping blank lines (paragraph separators).
    /// Adjusts scroll to keep cursor visible in the viewport.
    /// </summary>
    private static void MoveReaderCursor(CommandContext ctx, RenderOptions options, int delta)
    {
        ctx.LineCacheManager.EnsureLineCache(options);
        var lines = ctx.LineCacheManager.CachedLines;
        if (lines == null || lines.Count == 0)
        {
            return;
        }

        var cursor = ctx.NavigationService.ReaderCursorLine;
        var totalLines = lines.Count;

        if (delta > 0)
        {
            // Moving down
            var newCursor = Math.Min(cursor + delta, totalLines - 1);

            // Skip blank lines
            while (newCursor < totalLines - 1 && string.IsNullOrEmpty(lines[newCursor]))
            {
                newCursor++;
            }

            ctx.NavigationService.SetReaderCursorLine(newCursor);
        }
        else
        {
            // Moving up
            var newCursor = Math.Max(0, cursor + delta);

            // Skip blank lines
            while (newCursor > 0 && string.IsNullOrEmpty(lines[newCursor]))
            {
                newCursor--;
            }

            ctx.NavigationService.SetReaderCursorLine(newCursor);
        }

        AdjustScrollForCursor(ctx, options);
    }

    private static void AdjustScrollForCursor(CommandContext ctx, RenderOptions options)
    {
        var cursor = ctx.NavigationService.ReaderCursorLine;
        var scroll = ctx.NavigationService.CurrentContext.ScrollOffset;
        var vpHeight = ctx.GetReaderViewportHeight(options);
        var totalLines = ctx.LineCacheManager.CachedLines?.Count ?? 0;
        var maxScroll = Math.Max(0, totalLines - vpHeight);

        const int topMargin = 2;
        var bottomMargin = 2;

        if (cursor < scroll + topMargin)
        {
            scroll = Math.Max(0, cursor - topMargin);
        }
        else if (cursor > scroll + vpHeight - 1 - bottomMargin)
        {
            scroll = Math.Min(maxScroll, cursor - vpHeight + 1 + bottomMargin);
        }

        ctx.NavigationService.SetScrollOffset(Math.Clamp(scroll, 0, maxScroll));
    }
}
