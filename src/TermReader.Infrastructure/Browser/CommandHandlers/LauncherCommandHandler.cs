// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Infrastructure.Browser.UI.Renderers;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles launcher-mode commands: grid navigation, bookmark management, search.
/// </summary>
internal static class LauncherCommandHandler
{
    public static async Task<bool> Handle(CommandContext ctx, NavigationCommand command, RenderOptions options, CancellationToken ct)
    {
        var totalItems = (ctx.Bookmarks?.Count ?? 0) + 1; // +1 for Collections tile

        switch (command.Type)
        {
            case CommandType.Quit:
            case CommandType.GoBack:
                return false;

            case CommandType.MoveDown:
            {
                if (ctx.NavigationService.LauncherSelectedIndex == -1)
                {
                    // From URL bar → first bookmark
                    ctx.NavigationService.LauncherSelectedIndex = 0;
                }
                else
                {
                    var newIndex = LauncherNavigationState.MoveInGrid(ctx.NavigationService.LauncherSelectedIndex, totalItems, 1);
                    ctx.NavigationService.LauncherSelectedIndex = newIndex;
                }

                AdjustLauncherScroll(ctx, options);
                await ctx.RenderCurrentPageAsync(options, ct);
                break;
            }

            case CommandType.MoveUp:
            {
                var currentIdx = ctx.NavigationService.LauncherSelectedIndex;
                if (currentIdx <= 0 && currentIdx != -1)
                {
                    // From top row → URL bar
                    ctx.NavigationService.LauncherSelectedIndex = -1;
                }
                else if (currentIdx != -1)
                {
                    var newIndex = LauncherNavigationState.MoveInGrid(currentIdx, totalItems, 0);
                    ctx.NavigationService.LauncherSelectedIndex = newIndex;
                }

                AdjustLauncherScroll(ctx, options);
                await ctx.RenderCurrentPageAsync(options, ct);
                break;
            }

            case CommandType.CollapseNode: // h = left
            {
                var newIndex = LauncherNavigationState.MoveInGrid(ctx.NavigationService.LauncherSelectedIndex, totalItems, 2);
                ctx.NavigationService.LauncherSelectedIndex = newIndex;
                await ctx.RenderCurrentPageAsync(options, ct);
                break;
            }

            case CommandType.ExpandNode: // l = right
            {
                var newIndex = LauncherNavigationState.MoveInGrid(ctx.NavigationService.LauncherSelectedIndex, totalItems, 3);
                ctx.NavigationService.LauncherSelectedIndex = newIndex;
                await ctx.RenderCurrentPageAsync(options, ct);
                break;
            }

            case CommandType.ActivateLink:
            {
                var idx = ctx.NavigationService.LauncherSelectedIndex;
                if (idx == -1)
                {
                    // URL bar selected — activate URL input
                    await HandleGoToUrl(ctx, options, ct);
                }
                else if (idx == (ctx.Bookmarks?.Count ?? 0))
                {
                    await CollectionCommandHandler.HandleOpenCollections(ctx, options, ct);
                }
                else if (ctx.Bookmarks != null && idx < ctx.Bookmarks.Count)
                {
                    var bookmark = ctx.Bookmarks[idx];
                    await ctx.NavigateToAsync(bookmark.Url, options, ct);
                }

                break;
            }

            case CommandType.AddBookmark:
                await HandleAddBookmark(ctx, options, ct);
                break;

            case CommandType.DeleteItem:
            {
                var idx = ctx.NavigationService.LauncherSelectedIndex;
                if (ctx.Bookmarks != null && idx < ctx.Bookmarks.Count)
                {
                    var bookmark = ctx.Bookmarks[idx];
                    try
                    {
                        using var scope = ctx.ScopeFactory.CreateScope();
                        var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
                        await bookmarkService.DeleteBookmarkAsync(bookmark.Id, ct);
                        await ctx.RefreshBookmarksAsync(ct);

                        var newTotal = (ctx.Bookmarks?.Count ?? 0) + 1;
                        if (ctx.NavigationService.LauncherSelectedIndex >= newTotal)
                        {
                            ctx.NavigationService.LauncherSelectedIndex = Math.Max(0, newTotal - 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogWarning(ex, "Failed to delete bookmark");
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct);
                break;
            }

            case CommandType.ReorderUp:
                await HandleReorderUp(ctx, options, ct);
                break;

            case CommandType.ReorderDown:
                await HandleReorderDown(ctx, options, ct);
                break;

            case CommandType.OpenCollections:
                await CollectionCommandHandler.HandleOpenCollections(ctx, options, ct);
                break;

            case CommandType.GoToUrl:
            case CommandType.OpenInBrowser:
                // Select the URL bar, then activate it
                ctx.NavigationService.LauncherSelectedIndex = -1;
                await ctx.RenderCurrentPageAsync(options, ct);
                await HandleGoToUrl(ctx, options, ct);
                break;

            case CommandType.OpenCommandLine:
            {
                var input = await ctx.InputHandler.PromptForInputAsync(":", ct);
                if (!string.IsNullOrWhiteSpace(input))
                {
                    if (!await SearchCommandHandler.HandleCommandLineInput(ctx, input.Trim(), options, ct))
                    {
                        return false;
                    }
                }
                else
                {
                    await ctx.RenderCurrentPageAsync(options, ct);
                }

                break;
            }

            case CommandType.Search:
            {
                var query = await ctx.InputHandler.PromptForInputAsync("/", ct);
                if (!string.IsNullOrWhiteSpace(query) && ctx.Bookmarks != null)
                {
                    var matchIdx = ctx.Bookmarks
                        .Select((b, i) => (b, i))
                        .Where(x => x.b.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.i)
                        .FirstOrDefault(-1);
                    if (matchIdx >= 0)
                    {
                        ctx.NavigationService.LauncherSelectedIndex = matchIdx;
                    }
                    else
                    {
                        ctx.NavigationService.SetStatusMessage($"No bookmarks matching '{query}'");
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct);
                break;
            }

            case CommandType.ShowHelp:
                await ViewCommandHandler.HandleShowHelp(ctx, options, ct);
                break;

            case CommandType.PageDown:
            {
                var layout = LauncherRenderer.ComputeLayout(options.TerminalWidth, options.TerminalHeight);
                var halfRows = Math.Max(1, layout.VisibleRows / 2);
                var step = halfRows * layout.Columns;
                ctx.NavigationService.LauncherSelectedIndex =
                    Math.Min(ctx.NavigationService.LauncherSelectedIndex + step, totalItems - 1);
                AdjustLauncherScroll(ctx, options);
                await ctx.RenderCurrentPageAsync(options, ct);
                break;
            }

            case CommandType.PageUp:
            {
                var layout = LauncherRenderer.ComputeLayout(options.TerminalWidth, options.TerminalHeight);
                var halfRows = Math.Max(1, layout.VisibleRows / 2);
                var step = halfRows * layout.Columns;
                ctx.NavigationService.LauncherSelectedIndex =
                    Math.Max(ctx.NavigationService.LauncherSelectedIndex - step, 0);
                AdjustLauncherScroll(ctx, options);
                await ctx.RenderCurrentPageAsync(options, ct);
                break;
            }

            case CommandType.GoToTop:
                ctx.NavigationService.LauncherSelectedIndex = 0;
                ctx.NavigationService.LauncherScrollOffset = 0;
                await ctx.RenderCurrentPageAsync(options, ct);
                break;

            case CommandType.GoToBottom:
                ctx.NavigationService.LauncherSelectedIndex = Math.Max(0, totalItems - 1);
                AdjustLauncherScroll(ctx, options);
                await ctx.RenderCurrentPageAsync(options, ct);
                break;
        }

        return true;
    }

    private static async Task HandleGoToUrl(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var input = await ctx.InputHandler.PromptForInputAsync("Go to: ", ct);
        if (string.IsNullOrWhiteSpace(input))
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var url = input.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        await ctx.NavigateToAsync(url, options, ct);
    }

    private static async Task HandleAddBookmark(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var name = await ctx.InputHandler.PromptForInputAsync("Bookmark name: ", ct);
        if (string.IsNullOrWhiteSpace(name))
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var url = await ctx.InputHandler.PromptForInputAsync("URL: ", ct);
        if (string.IsNullOrWhiteSpace(url))
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
            await bookmarkService.AddBookmarkAsync(name, url, ct);
            await ctx.RefreshBookmarksAsync(ct);
            ctx.Logger.LogInformation("Added bookmark: {Name} ({Url})", name, url);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to add bookmark");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleReorderUp(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var idx = ctx.NavigationService.LauncherSelectedIndex;
        if (ctx.Bookmarks != null && idx >= 0 && idx < ctx.Bookmarks.Count)
        {
            var bookmark = ctx.Bookmarks[idx];
            try
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
                await bookmarkService.MoveBookmarkUpAsync(bookmark.Id, ct);
                await ctx.RefreshBookmarksAsync(ct);

                // Follow the bookmark to its new position
                if (ctx.Bookmarks != null)
                {
                    var newIdx = IndexOfBookmarkById(ctx.Bookmarks, bookmark.Id);
                    ctx.NavigationService.LauncherSelectedIndex =
                        newIdx >= 0 ? newIdx : Math.Max(0, idx - 1);
                }
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to move bookmark up");
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleReorderDown(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var idx = ctx.NavigationService.LauncherSelectedIndex;
        if (ctx.Bookmarks != null && idx >= 0 && idx < ctx.Bookmarks.Count)
        {
            var bookmark = ctx.Bookmarks[idx];
            try
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
                await bookmarkService.MoveBookmarkDownAsync(bookmark.Id, ct);
                await ctx.RefreshBookmarksAsync(ct);

                // Follow the bookmark to its new position
                if (ctx.Bookmarks != null)
                {
                    var newIdx = IndexOfBookmarkById(ctx.Bookmarks, bookmark.Id);
                    var maxIdx = Math.Max(0, ctx.Bookmarks.Count - 1);
                    ctx.NavigationService.LauncherSelectedIndex =
                        newIdx >= 0 ? newIdx : Math.Min(maxIdx, idx + 1);
                }
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to move bookmark down");
            }
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static int IndexOfBookmarkById(IReadOnlyList<Domain.Entities.Bookmarks.Bookmark> bookmarks, Guid id)
    {
        for (var i = 0; i < bookmarks.Count; i++)
        {
            if (bookmarks[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static void AdjustLauncherScroll(CommandContext ctx, RenderOptions options)
    {
        var layout = LauncherRenderer.ComputeLayout(options.TerminalWidth, options.TerminalHeight);
        var selectedRow = ctx.NavigationService.LauncherSelectedIndex / layout.Columns;
        var currentOffset = ctx.NavigationService.LauncherScrollOffset;

        if (selectedRow < currentOffset)
        {
            ctx.NavigationService.LauncherScrollOffset = selectedRow;
        }
        else if (selectedRow >= currentOffset + layout.VisibleRows)
        {
            ctx.NavigationService.LauncherScrollOffset = selectedRow - layout.VisibleRows + 1;
        }
    }
}
