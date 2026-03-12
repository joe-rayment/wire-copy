// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using TermReader.Infrastructure.Storage;

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

    public static async Task<bool> HandleCommandLineInput(CommandContext ctx, string input, RenderOptions options, CancellationToken ct)
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
                Console.Write("\x1b[H\x1b[2J");
                Console.WriteLine(ctx.InputHandler.GetHelpText());
                Console.ReadKey(intercept: true);
                await ctx.RenderCurrentPageAsync(options, ct);
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
                return true;

            case "clear":
                await HandleClearCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
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

            case "set":
                await HandleSetCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
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

    private static async Task HandleSetCommand(CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        var setting = subcommand?.Trim().ToLowerInvariant();

        switch (setting)
        {
            case "apikey":
                await HandleSetApiKey(ctx, options, ct);
                break;

            case "bucket":
                await HandleSetBucket(ctx, options, ct);
                break;

            case "key":
                await HandleSetKey(ctx, options, ct);
                break;

            default:
                ctx.NavigationService.SetStatusMessage("Usage: :set apikey | :set bucket | :set key");
                await ctx.RenderCurrentPageAsync(options, ct);
                break;
        }
    }

    private static async Task HandleSetApiKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var apiKey = await ctx.InputHandler.PromptForInputAsync(
            "OpenAI API key: ", ct, isSecret: true);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var trimmedKey = apiKey.Trim();

        using var scope = ctx.ScopeFactory.CreateScope();
        var ttsService = scope.ServiceProvider.GetRequiredService<ITtsService>();
        ttsService.SetApiKeyOverride(trimmedKey);

        ctx.NavigationService.SetStatusMessage("Verifying API key...");
        await ctx.RenderCurrentPageAsync(options, ct);

        var validation = await ttsService.ValidateApiKeyAsync(ct);

        if (validation.IsValid)
        {
            try
            {
                var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                settingsStore.Set("OpenAiApiKey", trimmedKey, encrypt: true);
                ctx.NavigationService.SetStatusMessage("API key saved");
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to persist API key");
                ctx.NavigationService.SetStatusMessage("API key verified but failed to save");
            }
        }
        else
        {
            ttsService.SetApiKeyOverride(string.Empty);
            ctx.NavigationService.SetStatusMessage(validation.ErrorMessage ?? "Invalid API key");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleSetBucket(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var bucketName = await ctx.InputHandler.PromptForInputAsync(
            "GCS bucket name: ", ct);

        if (string.IsNullOrWhiteSpace(bucketName))
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var trimmed = bucketName.Trim();

        if (!GcsConfiguration.IsValidBucketName(trimmed))
        {
            ctx.NavigationService.SetStatusMessage(
                $"Invalid: \"{trimmed}\" \u2014 must be 3\u201363 chars, lowercase a\u2013z/0\u20139/hyphens/dots");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Set("GcsBucketName", trimmed);

            var gcsConfig = scope.ServiceProvider.GetRequiredService<IOptions<GcsConfiguration>>().Value;
            gcsConfig.BucketName = trimmed;

            ctx.NavigationService.SetStatusMessage($"Bucket set to \"{trimmed}\"");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist bucket name");
            ctx.NavigationService.SetStatusMessage("Failed to save bucket name");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleClearCommand(CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        var setting = subcommand?.Trim().ToLowerInvariant();

        switch (setting)
        {
            case "apikey":
                await HandleClearApiKey(ctx, options, ct);
                break;

            case "bucket":
                await HandleClearBucket(ctx, options, ct);
                break;

            case "key":
                await HandleClearKey(ctx, options, ct);
                break;

            default:
                // No recognized subcommand — delegate to existing collection clear behavior
                await CollectionCommandHandler.HandleClearCollection(ctx, options, ct);
                break;
        }
    }

    private static async Task HandleClearApiKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Remove("OpenAiApiKey");

            var ttsService = scope.ServiceProvider.GetRequiredService<ITtsService>();
            ttsService.SetApiKeyOverride(string.Empty);

            ctx.NavigationService.SetStatusMessage("API key cleared");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove API key");
            ctx.NavigationService.SetStatusMessage("Failed to clear API key");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleClearBucket(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
            settingsStore.Remove("GcsBucketName");

            var gcsConfig = scope.ServiceProvider.GetRequiredService<IOptions<GcsConfiguration>>().Value;
            gcsConfig.BucketName = null;

            ctx.NavigationService.SetStatusMessage("Bucket name cleared");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove bucket name");
            ctx.NavigationService.SetStatusMessage("Failed to clear bucket name");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleSetKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var keyPath = await ctx.InputHandler.PromptForInputAsync(
            "GCS service account key file path: ", ct);

        if (string.IsNullOrWhiteSpace(keyPath))
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var trimmed = keyPath.Trim();

        // Expand ~ to home directory
        if (trimmed.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            trimmed = Path.Combine(home, trimmed[1..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        trimmed = Path.GetFullPath(trimmed);

        var validation = GcsStorageClient.ValidateKeyFile(trimmed);
        if (!validation.IsValid)
        {
            ctx.NavigationService.SetStatusMessage(validation.ErrorMessage ?? "Invalid key file");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudStorageClient>();

            if (cloudStorage is GcsStorageClient gcsClient)
            {
                var result = gcsClient.SetServiceAccountKeyPath(trimmed);
                if (result.IsValid)
                {
                    ctx.NavigationService.SetStatusMessage("Service account key saved");
                }
                else
                {
                    ctx.NavigationService.SetStatusMessage(result.ErrorMessage ?? "Failed to set key");
                }
            }
            else
            {
                // Fallback: store directly in settings
                var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                settingsStore.Set("GcsServiceAccountKeyPath", trimmed, encrypt: true);
                ctx.NavigationService.SetStatusMessage("Service account key path saved");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist service account key path");
            ctx.NavigationService.SetStatusMessage("Failed to save key path");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleClearKey(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudStorageClient>();

            if (cloudStorage is GcsStorageClient gcsClient)
            {
                gcsClient.ClearServiceAccountKey();
            }
            else
            {
                var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();
                settingsStore.Remove("GcsServiceAccountKeyPath");
            }

            ctx.NavigationService.SetStatusMessage("Service account key cleared");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove service account key");
            ctx.NavigationService.SetStatusMessage("Failed to clear service account key");
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
