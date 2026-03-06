// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles search and command-line input commands.
/// </summary>
internal static class SearchCommandHandler
{
    public static async Task HandleSearch(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var query = await ctx.InputHandler.PromptForInputAsync("/", ct);
        if (!string.IsNullOrWhiteSpace(query))
        {
            ctx.NavigationService.SetSearchQuery(query);
            ctx.ScrollToSearchMatch(0, options);
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleSearchNext(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(ctx.NavigationService.CurrentContext.SearchQuery))
        {
            var nextIndex = ctx.NavigationService.CurrentContext.SearchMatchIndex + 1;
            ctx.ScrollToSearchMatch(nextIndex, options);
            await ctx.RenderCurrentPageAsync(options, ct);
        }
    }

    public static async Task HandleSearchPrevious(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(ctx.NavigationService.CurrentContext.SearchQuery))
        {
            var prevIndex = ctx.NavigationService.CurrentContext.SearchMatchIndex - 1;
            ctx.ScrollToSearchMatch(prevIndex, options);
            await ctx.RenderCurrentPageAsync(options, ct);
        }
    }

    public static async Task HandleOpenCommandLine(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var input = await ctx.InputHandler.PromptForInputAsync(":", ct);
        if (!string.IsNullOrWhiteSpace(input))
        {
            await HandleCommandLineInput(ctx, input.Trim(), options, ct);
        }
        else
        {
            await ctx.RenderCurrentPageAsync(options, ct);
        }
    }

    public static async Task HandleCommandLineInput(CommandContext ctx, string input, RenderOptions options, CancellationToken ct)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "q" or "quit":
                return;

            case "back" or "b":
                var prevPage = ctx.NavigationService.GoBack();
                if (prevPage != null)
                {
                    ctx.InvalidateLineCache();
                    await ctx.RenderCurrentPageAsync(options, ct);
                }

                return;

            case "forward" or "f":
                var fwdPage = ctx.NavigationService.GoForward();
                if (fwdPage != null)
                {
                    ctx.InvalidateLineCache();
                    await ctx.RenderCurrentPageAsync(options, ct);
                }

                return;

            case "help" or "h":
                Console.Clear();
                Console.WriteLine(ctx.InputHandler.GetHelpText());
                Console.ReadKey(intercept: true);
                await ctx.RenderCurrentPageAsync(options, ct);
                return;

            case "open" or "go" or "o":
                if (parts.Length > 1)
                {
                    var url = NormalizeUrl(parts[1]);
                    await ctx.NavigateToAsync(url, options, ct);
                }
                else
                {
                    await ctx.RenderCurrentPageAsync(options, ct);
                }

                return;

            case "home":
                ctx.NavigationService.EnterLauncher();
                await ctx.RefreshBookmarksAsync(ct);
                await ctx.RenderCurrentPageAsync(options, ct);
                return;

            case "add":
                // Delegate to launcher handler's add bookmark
                var name = await ctx.InputHandler.PromptForInputAsync("Bookmark name: ", ct);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var bookmarkUrl = await ctx.InputHandler.PromptForInputAsync("URL: ", ct);
                    if (!string.IsNullOrWhiteSpace(bookmarkUrl))
                    {
                        bookmarkUrl = NormalizeUrl(bookmarkUrl);
                        try
                        {
                            using var scope = ctx.ScopeFactory.CreateScope();
                            var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
                            await bookmarkService.AddBookmarkAsync(name, bookmarkUrl, ct);
                            await ctx.RefreshBookmarksAsync(ct);
                            ctx.Logger.LogInformation("Added bookmark: {Name} ({Url})", name, bookmarkUrl);
                        }
                        catch (Exception ex)
                        {
                            ctx.Logger.LogWarning(ex, "Failed to add bookmark");
                        }
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct);
                return;

            case "collections" or "readlater":
                await CollectionCommandHandler.HandleOpenCollections(ctx, options, ct);
                return;

            case "new":
                if (parts.Length > 1)
                {
                    try
                    {
                        using var scope = ctx.ScopeFactory.CreateScope();
                        var service = ctx.CreateCollectionService(scope);
                        await service.CreateCollectionAsync(parts[1], ct);
                        await ctx.RefreshCollectionsAsync(ct);
                        ctx.Logger.LogInformation("Created collection: {Name}", parts[1]);
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogWarning(ex, "Failed to create collection");
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct);
                return;

            case "rename":
                if (parts.Length > 1 && ctx.NavigationService.CurrentContext.ViewMode == ViewMode.CollectionList &&
                    ctx.Collections != null && ctx.NavigationService.CollectionSelectedIndex < ctx.Collections.Count)
                {
                    var renameCol = ctx.Collections[ctx.NavigationService.CollectionSelectedIndex];
                    try
                    {
                        using var scope = ctx.ScopeFactory.CreateScope();
                        var service = ctx.CreateCollectionService(scope);
                        await service.RenameCollectionAsync(renameCol.Id, parts[1], ct);
                        await ctx.RefreshCollectionsAsync(ct);
                        ctx.Logger.LogInformation("Renamed collection to: {Name}", parts[1]);
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogWarning(ex, "Failed to rename collection");
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct);
                return;

            case "clear":
                if (ctx.NavigationService.CurrentContext.ViewMode == ViewMode.CollectionItems)
                {
                    var clearCol = ctx.NavigationService.ActiveCollection;
                    if (clearCol != null)
                    {
                        try
                        {
                            using var scope = ctx.ScopeFactory.CreateScope();
                            var service = ctx.CreateCollectionService(scope);
                            await service.ClearCollectionAsync(clearCol.Id, ct);
                            await ctx.RefreshCollectionsAsync(ct);
                            ctx.NavigationService.CollectionItemSelectedIndex = 0;
                            ctx.Logger.LogInformation("Cleared collection: {Name}", clearCol.Name);
                        }
                        catch (Exception ex)
                        {
                            ctx.Logger.LogWarning(ex, "Failed to clear collection");
                        }
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct);
                return;

            case "export":
                await HandleExportCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
                return;

            case "cache":
                await HandleCacheCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
                return;

            default:
                var navigateUrl = NormalizeUrl(input);
                await ctx.NavigateToAsync(navigateUrl, options, ct);
                return;
        }
    }

    private static async Task HandleExportCommand(CommandContext ctx, string? format, RenderOptions options, CancellationToken ct)
    {
        var collection = ctx.NavigationService.ActiveCollection;
        if (collection == null)
        {
            ctx.Logger.LogWarning("No active collection to export. Open a collection first.");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var exporters = scope.ServiceProvider.GetServices<ICollectionExporter>();

            var requestedFormat = format?.ToLowerInvariant() ?? "urls";
            var exporter = exporters.FirstOrDefault(e =>
                string.Equals(e.Format, requestedFormat, StringComparison.OrdinalIgnoreCase));

            if (exporter == null)
            {
                var available = string.Join(", ", exporters.Select(e => e.Format));
                ctx.Logger.LogWarning("Unknown export format '{Format}'. Available: {Available}", requestedFormat, available);
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            var outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "termreader-exports");
            Directory.CreateDirectory(outputDir);

            var safeName = string.Join("_", collection.Name.Split(Path.GetInvalidFileNameChars()));
            var outputPath = Path.Combine(outputDir, $"{safeName}.{requestedFormat}");

            await exporter.ExportAsync(collection, new ExportOptions(outputPath), ct);
            ctx.Logger.LogInformation("Exported collection '{Name}' to {Path}", collection.Name, outputPath);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to export collection");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleCacheCommand(CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subcommand))
        {
            var stats = ctx.PageCache.GetStats();
            ctx.NavigationService.SetStatusMessage(
                $"Cache: {stats.EntryCount} pages, {stats.FormattedSize} / {stats.FormattedMaxSize}, {stats.HitRatePercent}% hit rate");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        if (subcommand.StartsWith("clear", StringComparison.OrdinalIgnoreCase))
        {
            var clearArg = subcommand.Length > 5 ? subcommand[5..].Trim() : null;
            if (!string.IsNullOrEmpty(clearArg))
            {
                var removed = ctx.PageCache.Remove(clearArg);
                ctx.NavigationService.SetStatusMessage(
                    removed ? $"Removed {clearArg} from cache" : "URL not found in cache");
            }
            else
            {
                var confirm = await ctx.InputHandler.PromptForInputAsync("Clear all cached pages? (y/n): ", ct);
                if (string.Equals(confirm, "y", StringComparison.OrdinalIgnoreCase))
                {
                    var cleared = ctx.PageCache.Clear();
                    ctx.NavigationService.SetStatusMessage(
                        $"Cache cleared ({cleared.EntryCount} pages, {cleared.FormattedSize} freed)");
                }
            }

            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static string NormalizeUrl(string input)
    {
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + input;
        }

        return input;
    }
}
