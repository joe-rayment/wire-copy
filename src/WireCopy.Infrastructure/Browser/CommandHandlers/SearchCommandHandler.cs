// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Storage;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

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
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        // workspace-8fkv: start the search prompt at the docked content origin so a
        // left-docked browser doesn't sit over it. Only override the default column when a
        // left dock actually shifts the origin, so the undocked / right-dock call is unchanged.
        var query = await ctx.InputHandler.PromptForInputAsync(
            "/", ct, col: options.ContentLeftOffset > 0 ? options.ContentLeftOffset : null).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(query))
        {
            ctx.NavigationService.SetSearchQuery(query);
            ctx.ScrollToSearchMatch(0, options);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
    }

    public static async Task<bool> HandleOpenCommandLine(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        // workspace-8fkv: start the command line at the docked content origin so a
        // left-docked browser doesn't sit over it. Only override the default column when a
        // left dock actually shifts the origin, so the undocked / right-dock call is unchanged.
        var input = await ctx.InputHandler.PromptForInputAsync(
            ":", ct, col: options.ContentLeftOffset > 0 ? options.ContentLeftOffset : null).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(input))
        {
            return await HandleCommandLineInput(ctx, input.Trim(), options, ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                }

                return true;

            case "forward" or "f":
                var fwdPage = ctx.NavigationService.GoForward();
                if (fwdPage != null)
                {
                    ctx.LineCacheManager.InvalidateLineCache();
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                }

                return true;

            case "help" or "h":
                await ViewCommandHandler.HandleShowHelp(ctx, options, ct).ConfigureAwait(false);
                return true;

            case "open" or "go" or "o":
                if (parts.Length > 1)
                {
                    var url = NormalizeUrl(parts[1]);
                    await ctx.NavigateToAsync(url, options, ct).ConfigureAwait(false);
                }
                else
                {
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                }

                return true;

            case "home":
                ctx.NavigationService.EnterLauncher();
                await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return true;

            case "add":
                // Delegate to launcher handler's add bookmark
                var name = await ctx.InputHandler.PromptForInputAsync("Bookmark name: ", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var bookmarkUrl = await ctx.InputHandler.PromptForInputAsync("URL: ", ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(bookmarkUrl))
                    {
                        bookmarkUrl = NormalizeUrl(bookmarkUrl);
                        try
                        {
                            using var scope = ctx.ScopeFactory.CreateScope();
                            var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
                            await bookmarkService.AddBookmarkAsync(name, bookmarkUrl, ct).ConfigureAwait(false);
                            await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);
                            ctx.Logger.LogInformation("Added bookmark: {Name} ({Url})", name, bookmarkUrl);
                        }
                        catch (Exception ex)
                        {
                            ctx.Logger.LogWarning(ex, "Failed to add bookmark");
                        }
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return true;

            case "collections" or "readlater":
                await CollectionCommandHandler.HandleOpenCollections(ctx, options, ct).ConfigureAwait(false);
                return true;

            case "new":
                if (parts.Length > 1)
                {
                    try
                    {
                        using var scope = ctx.ScopeFactory.CreateScope();
                        var service = ctx.CreateCollectionService(scope);
                        await service.CreateCollectionAsync(parts[1], ct).ConfigureAwait(false);
                        await ctx.RefreshCollectionsAsync(ct).ConfigureAwait(false);
                        ctx.Logger.LogInformation("Created collection: {Name}", parts[1]);
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogWarning(ex, "Failed to create collection");
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return true;

            case "rename":
                if (parts.Length > 1)
                {
                    var renameViewMode = ctx.NavigationService.CurrentContext.ViewMode;

                    if (renameViewMode == ViewMode.Launcher)
                    {
                        await HandleRenameLauncherBookmark(ctx, parts[1], ct).ConfigureAwait(false);
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
                                await service.RenameCollectionAsync(renameCol.Id, parts[1], ct).ConfigureAwait(false);
                                await ctx.RefreshCollectionsAsync(ct).ConfigureAwait(false);
                                ctx.Logger.LogInformation("Renamed collection to: {Name}", parts[1]);
                            }
                            catch (Exception ex)
                            {
                                ctx.Logger.LogWarning(ex, "Failed to rename collection");
                            }
                        }
                    }
                }

                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return true;

            case "clear":
                await SettingsCommandHandler.HandleClearCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct).ConfigureAwait(false);
                return true;

            case "export":
                await HandleExportCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct).ConfigureAwait(false);
                return true;

            case "cache":
                await HandleCacheCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct).ConfigureAwait(false);
                return true;

            case "podcast":
                // workspace-vkhr Phase D: when a background job is running,
                // :podcast restores the modal instead of starting a new run
                // (single-active-job invariant). Otherwise kick off a fresh
                // generation.
                using (var podcastScope = ctx.ScopeFactory.CreateScope())
                {
                    var jobManager = podcastScope.ServiceProvider.GetService<IPodcastBackgroundJobManager>();
                    if (jobManager?.HasActiveJob == true)
                    {
                        await PodcastCommandHandler.HandleRestorePodcastModal(ctx, options, ct).ConfigureAwait(false);
                        return true;
                    }
                }

                await PodcastCommandHandler.HandleGeneratePodcast(ctx, options, ct).ConfigureAwait(false);
                return true;

            case "settings":
                await PodcastCommandHandler.HandlePodcastSettings(ctx, options, ct).ConfigureAwait(false);
                return true;

            case "config":
                await SettingsCommandHandler.HandleConfigScreen(ctx, options, ct).ConfigureAwait(false);
                return true;

            case "set":
                await SettingsCommandHandler.HandleSetCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct).ConfigureAwait(false);
                return true;

            case "cred" or "credentials":
                await CredentialCommandHandler.HandleCredentialCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct).ConfigureAwait(false);
                return true;

            case "cookies":
                await CookiesCommandHandler.HandleCookiesCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct).ConfigureAwait(false);
                return true;

            case "dump" or "dump-html":
                await ViewCommandHandler.HandleDumpHtml(ctx, options, ct).ConfigureAwait(false);
                return true;

            case "reanalyze":
                await HandleReanalyze(ctx, options, ct).ConfigureAwait(false);
                return true;

            case "layout":
                // workspace-5oe9.10: ':layout' and Ctrl+L both open the single
                // strategy chooser (StrategyChooserHandler) — no second entry point.
                await StrategyChooserHandler.HandleOpenChooserAsync(ctx, options, ct).ConfigureAwait(false);
                return true;

            case "layout clear":
                await LayoutCommandHandler.HandleClearLayout(ctx, options, ct).ConfigureAwait(false);
                return true;

            case "layout reset":
                // workspace-8qyo: forget the tuned/AI ARTICLE selectors for the
                // current domain (link-list layout has ':layout clear' above).
                await ArticleLayoutResetHandler.HandleResetAsync(ctx, options, ct).ConfigureAwait(false);
                return true;

            case "schedules" or "schedule":
                // workspace-frpl.14 (B12a): the recurring-recipe → auto-podcast screen.
                await ScheduleCommandHandler.HandleSchedulesAsync(ctx, options, ct).ConfigureAwait(false);
                return true;

            default:
                var navigateUrl = NormalizeUrl(input);
                await ctx.NavigateToAsync(navigateUrl, options, ct).ConfigureAwait(false);
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
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            var outputDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "wirecopy-exports");
            Directory.CreateDirectory(outputDir);

            var safeName = FileNameSanitizer.Sanitize(collection.Name);
            var outputPath = Path.Combine(outputDir, $"{safeName}.{requestedFormat}");

            await exporter.ExportAsync(collection, new ExportOptions(outputPath), ct).ConfigureAwait(false);
            ctx.NavigationService.SetStatusMessage($"Exported to {outputPath}");
            ctx.Logger.LogInformation("Exported collection '{Name}' to {Path}", collection.Name, outputPath);
        }
        catch (Exception ex)
        {
            ctx.NavigationService.SetStatusMessage($"Export failed: {ex.Message}");
            ctx.Logger.LogWarning(ex, "Failed to export collection");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
                    ct).ConfigureAwait(false);
                if (confirmed)
                {
                    var cleared = ctx.PageCache.Clear();
                    ctx.NavigationService.SetStatusMessage(
                        $"Cache cleared ({cleared.EntryCount} pages, {cleared.FormattedSize} freed)");
                }
            }

            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
            var registry = scope.ServiceProvider.GetService<IScrapingStrategyRegistry>();
            var analyzer = scope.ServiceProvider.GetService<IHierarchyAnalyzer>();

            if (analyzer == null || !analyzer.IsConfigured)
            {
                ctx.NavigationService.SetStatusMessage("OpenAI API key not configured. Press S to open Setup.");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            var strategy = registry?.GetById(ScrapingStrategies.AiCuratedStrategy.StrategyId);
            if (strategy == null)
            {
                ctx.NavigationService.SetStatusMessage("AI Curated strategy unavailable");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            // workspace-5oe9.6: :reanalyze is the FAST one-shot path — run the
            // AiCurated strategy directly (SavedConfig=null forces a fresh
            // analysis, never the cache) so it produces a durable Version-3
            // pattern config (B5). Ctrl+L (B9) is the question-driven path.
            ctx.NavigationService.SetStatusMessage("Re-analyzing page hierarchy…", TimeSpan.FromMinutes(2));
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            var buildCache = ctx.PageCache.TryGetBuildCache(page.Url);
            var links = (IReadOnlyList<Domain.ValueObjects.Browser.LinkInfo>)(buildCache?.Links
                ?? new List<Domain.ValueObjects.Browser.LinkInfo>());
            byte[]? screenshot = null;
            if (scope.ServiceProvider.GetService<IBrowserSessionControl>() is IBrowserSession session)
            {
                try
                {
                    screenshot = await session.CaptureScreenshotAsync().ConfigureAwait(false);
                }
                catch
                {
                    // best-effort; AI curation works without a screenshot
                }
            }

            var stContext = new ScrapingStrategyContext
            {
                PageUrl = page.Url,
                Html = page.RawHtml ?? string.Empty,
                Links = links,
                Screenshot = screenshot,
                SavedConfig = null,
            };

            var result = await strategy.BuildTreeAsync(stContext, ct).ConfigureAwait(false);
            if (configStore != null)
            {
                await configStore.SaveConfigAsync(result.Config).ConfigureAwait(false);
            }

            page.SetLinkTree(result.Tree);

            // workspace-5oe9.13: a degenerate fresh analysis (even after retry)
            // must not read as success — nudge the user to the question-driven
            // setup instead of silently shipping document order.
            ctx.NavigationService.SetStatusMessage(result.NeedsClarification
                ? "AI couldn't find a clear structure — press Ctrl+l to set it up with questions"
                : $"✔ Re-analyzed · {result.Summary}");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to re-analyze page hierarchy");
            ctx.NavigationService.SetStatusMessage("Re-analysis failed");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
                await bookmarkService.RenameBookmarkAsync(bookmark.Id, newName, ct).ConfigureAwait(false);
                await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);
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
