// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Components;
using TermReader.Infrastructure.Storage;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles search and command-line input commands.
/// </summary>
internal static class SearchCommandHandler
{
    public static async Task HandleSearch(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (IsCollectionView(ctx))
        {
            ctx.NavigationService.SetStatusMessage("Search not available in collections");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

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
        if (IsCollectionView(ctx))
        {
            return;
        }

        if (!string.IsNullOrEmpty(ctx.NavigationService.CurrentContext.SearchQuery))
        {
            var nextIndex = ctx.NavigationService.CurrentContext.SearchMatchIndex + 1;
            ctx.ScrollToSearchMatch(nextIndex, options);
            await ctx.RenderCurrentPageAsync(options, ct);
        }
    }

    public static async Task HandleSearchPrevious(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        if (IsCollectionView(ctx))
        {
            return;
        }

        if (!string.IsNullOrEmpty(ctx.NavigationService.CurrentContext.SearchQuery))
        {
            var prevIndex = ctx.NavigationService.CurrentContext.SearchMatchIndex - 1;
            ctx.ScrollToSearchMatch(prevIndex, options);
            await ctx.RenderCurrentPageAsync(options, ct);
        }
    }

    public static async Task<bool> HandleOpenCommandLine(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var input = await ctx.InputHandler.PromptForInputAsync(":", ct);
        if (!string.IsNullOrWhiteSpace(input))
        {
            return await HandleCommandLineInput(ctx, input.Trim(), options, ct);
        }
        else
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return true;
        }
    }

    internal static async Task<bool> HandleCommandLineInput(CommandContext ctx, string input, RenderOptions options, CancellationToken ct)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "q" or "quit":
                return false;

            case "back" or "b":
                var prevPage = ctx.NavigationService.GoBack();
                if (prevPage != null)
                {
                    ctx.LineCacheManager.InvalidateLineCache();
                    await ctx.RenderCurrentPageAsync(options, ct);
                }

                return true;

            case "forward" or "f":
                var fwdPage = ctx.NavigationService.GoForward();
                if (fwdPage != null)
                {
                    ctx.LineCacheManager.InvalidateLineCache();
                    await ctx.RenderCurrentPageAsync(options, ct);
                }

                return true;

            case "help" or "h":
                await ViewCommandHandler.HandleShowHelp(ctx, options, ct);
                return true;

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

                return true;

            case "home":
                ctx.NavigationService.EnterLauncher();
                await ctx.RefreshBookmarksAsync(ct);
                await ctx.RenderCurrentPageAsync(options, ct);
                return true;

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
                return true;

            case "collections" or "readlater":
                await CollectionCommandHandler.HandleOpenCollections(ctx, options, ct);
                return true;

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
                return true;

            case "rename":
                if (parts.Length > 1)
                {
                    var renameViewMode = ctx.NavigationService.CurrentContext.ViewMode;

                    if (renameViewMode == ViewMode.Launcher)
                    {
                        await HandleRenameLauncherBookmark(ctx, parts[1], ct);
                    }
                    else
                    {
                        Collection? renameCol = null;

                        if (renameViewMode == ViewMode.CollectionList &&
                            ctx.Collections != null && ctx.NavigationService.CollectionSelectedIndex < ctx.Collections.Count)
                        {
                            renameCol = ctx.Collections[ctx.NavigationService.CollectionSelectedIndex];
                        }
                        else if (renameViewMode == ViewMode.CollectionItems)
                        {
                            renameCol = ctx.NavigationService.ActiveCollection;
                        }

                        if (renameCol != null)
                        {
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
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct);
                return true;

            case "clear":
                await SettingsCommandHandler.HandleClearCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
                return true;

            case "export":
                await HandleExportCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
                return true;

            case "cache":
                await HandleCacheCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
                return true;

            case "podcast":
                await PodcastCommandHandler.HandleGeneratePodcast(ctx, options, ct);
                return true;

            case "settings":
                await PodcastCommandHandler.HandlePodcastSettings(ctx, options, ct);
                return true;

            case "config":
                await SettingsCommandHandler.HandleConfigScreen(ctx, options, ct);
                return true;

            case "set":
                await SettingsCommandHandler.HandleSetCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
                return true;

            case "cred" or "credentials":
                await CredentialCommandHandler.HandleCredentialCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
                return true;

            case "dump" or "dump-html":
                await ViewCommandHandler.HandleDumpHtml(ctx, options, ct);
                return true;

            case "reanalyze":
                await HandleReanalyze(ctx, options, ct);
                return true;

            case "layout":
                await LayoutCommandHandler.HandleChooseLayout(ctx, options, ct);
                return true;

            case "layout clear":
                await LayoutCommandHandler.HandleClearLayout(ctx, options, ct);
                return true;

            default:
                var navigateUrl = NormalizeUrl(input);
                await ctx.NavigateToAsync(navigateUrl, options, ct);
                return true;
        }
    }

    private static async Task HandleExportCommand(CommandContext ctx, string? format, RenderOptions options, CancellationToken ct)
    {
        var collection = ctx.NavigationService.ActiveCollection;
        if (collection == null)
        {
            ctx.NavigationService.SetStatusMessage("No active collection to export. Open a collection first.");
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
                ctx.NavigationService.SetStatusMessage($"Unknown format '{requestedFormat}'. Available: {available}");
                ctx.Logger.LogWarning("Unknown export format '{Format}'. Available: {Available}", requestedFormat, available);
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            var outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "termreader-exports");
            Directory.CreateDirectory(outputDir);

            var safeName = FileNameSanitizer.Sanitize(collection.Name);
            var outputPath = Path.Combine(outputDir, $"{safeName}.{requestedFormat}");

            await exporter.ExportAsync(collection, new ExportOptions(outputPath), ct);
            ctx.NavigationService.SetStatusMessage($"Exported to {outputPath}");
            ctx.Logger.LogInformation("Exported collection '{Name}' to {Path}", collection.Name, outputPath);
        }
        catch (Exception ex)
        {
            ctx.NavigationService.SetStatusMessage($"Export failed: {ex.Message}");
            ctx.Logger.LogWarning(ex, "Failed to export collection");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleCacheCommand(CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subcommand))
        {
            var stats = EnrichCacheStats(ctx);
            var diskPart = stats.DiskCacheFileCount > 0
                ? $" | Disk: {stats.DiskCacheFileCount} files, {stats.FormattedDiskSize}/{stats.FormattedMaxDiskSize}"
                : string.Empty;
            var articlePart = stats.ArticleCacheCount > 0
                ? $" | Articles: {stats.ArticleCacheCount}"
                : string.Empty;
            ctx.NavigationService.SetStatusMessage(
                $"Cache: {stats.EntryCount} pages ({stats.UsagePercent}%) | {stats.HitRatePercent}% hit rate{diskPart}{articlePart}");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        if (string.Equals(subcommand, "info", StringComparison.OrdinalIgnoreCase))
        {
            var stats = EnrichCacheStats(ctx);
            var lines = new List<string>
            {
                $"Memory: {stats.EntryCount} pages, {stats.FormattedSize}/{stats.FormattedMaxSize} ({stats.UsagePercent}%)",
                $"Disk: {stats.DiskCacheFileCount} files, {stats.FormattedDiskSize}/{stats.FormattedMaxDiskSize} ({stats.DiskUsagePercent}%)",
                $"Articles: {stats.ArticleCacheCount} cached",
                $"Hit rate: {stats.HitRatePercent}% ({stats.HitCount} hits, {stats.MissCount} misses)"
            };
            ctx.NavigationService.SetStatusMessage(string.Join(" | ", lines));
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
                var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
                var confirmed = await ConfirmationDialog.ConfirmAsync(
                    ctx.InputHandler,
                    "Clear Cache",
                    "Clear all cached pages?",
                    palette,
                    ct);
                if (confirmed)
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

    /// <summary>
    /// Enriches page cache stats with article cache count from the preload service.
    /// </summary>
    private static CacheStats EnrichCacheStats(CommandContext ctx)
    {
        var stats = ctx.PageCache.GetStats();
        var articleCount = ctx.PreloadService.GetArticleCachedUrls().Count;
        return stats with { ArticleCacheCount = articleCount };
    }

    private static async Task HandleReanalyze(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var page = ctx.NavigationService.CurrentPage;
        if (page == null)
        {
            ctx.NavigationService.SetStatusMessage("No page loaded");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
            var analyzer = scope.ServiceProvider.GetService<IHierarchyAnalyzer>();

            if (analyzer == null || !analyzer.IsConfigured)
            {
                ctx.NavigationService.SetStatusMessage("Anthropic API key not configured. Use :set anthropic-key first.");
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            // Delete existing config for this URL
            if (configStore != null)
            {
                await configStore.DeleteConfigAsync(page.Url);
            }

            // Force refresh will re-trigger AI analysis since config is now deleted
            ctx.NavigationService.SetStatusMessage("Re-analyzing page hierarchy...");
            await ctx.RenderCurrentPageAsync(options, ct);
            await ctx.ForceRefreshAsync(page.Url, options, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to re-analyze page hierarchy");
            ctx.NavigationService.SetStatusMessage("Re-analysis failed");
            await ctx.RenderCurrentPageAsync(options, ct);
        }
    }

    private static async Task HandleRenameLauncherBookmark(
        CommandContext ctx, string newName, CancellationToken ct)
    {
        var idx = ctx.NavigationService.LauncherSelectedIndex;
        if (ctx.Bookmarks != null && idx >= 0 && idx < ctx.Bookmarks.Count)
        {
            var bookmark = ctx.Bookmarks[idx];
            try
            {
                using var scope = ctx.ScopeFactory.CreateScope();
                var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
                await bookmarkService.RenameBookmarkAsync(bookmark.Id, newName, ct);
                await ctx.RefreshBookmarksAsync(ct);
                ctx.NavigationService.SetStatusMessage($"Renamed to: {newName}");
                ctx.Logger.LogInformation("Renamed bookmark to: {Name}", newName);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to rename bookmark");
                ctx.NavigationService.SetStatusMessage("Failed to rename bookmark");
            }
        }
    }

    private static bool IsCollectionView(CommandContext ctx)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        return viewMode == ViewMode.CollectionList || viewMode == ViewMode.CollectionItems;
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
