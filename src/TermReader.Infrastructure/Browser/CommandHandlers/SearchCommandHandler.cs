// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Entities.Credentials;
using TermReader.Domain.Enums;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Credentials;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using TermReader.Infrastructure.Storage;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles search and command-line input commands.
/// </summary>
internal static class SearchCommandHandler
{
    private static readonly Dictionary<string, (string LoginUrl, List<LoginStep> Steps)> KnownSiteDefaults = new()
    {
        ["nytimes.com"] = (
            "https://myaccount.nytimes.com/auth/login",
            new List<LoginStep>
            {
                new("#email", StepValueType.Username, "button[data-testid=submit-email]"),
                new("#password", StepValueType.Password, "button[type=submit]"),
            }),
    };

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

            case "settings":
                await PodcastCommandHandler.HandlePodcastSettings(ctx, options, ct);
                return true;

            case "set":
                await HandleSetCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
                return true;

            case "cred" or "credentials":
                await HandleCredentialCommand(ctx, parts.Length > 1 ? parts[1] : null, options, ct);
                return true;

            case "dump" or "dump-html":
                await ViewCommandHandler.HandleDumpHtml(ctx, options, ct);
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

    /// <summary>
    /// Enriches page cache stats with article cache count from the preload service.
    /// </summary>
    private static CacheStats EnrichCacheStats(CommandContext ctx)
    {
        var stats = ctx.PageCache.GetStats();
        var articleCount = ctx.PreloadService.GetArticleCachedUrls().Count;
        return stats with { ArticleCacheCount = articleCount };
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

    private static async Task HandleCredentialCommand(
        CommandContext ctx, string? subcommand, RenderOptions options, CancellationToken ct)
    {
        var sub = subcommand?.Trim().ToLowerInvariant();

        // Parse subcommand and optional trailing argument (e.g. "rm nytimes.com")
        string? subArg = null;
        if (sub != null)
        {
            var subParts = sub.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            sub = subParts[0];
            subArg = subParts.Length > 1 ? subParts[1].Trim() : null;
        }

        switch (sub)
        {
            case "add":
                await HandleCredentialAdd(ctx, options, ct);
                break;

            case "remove" or "rm":
                await HandleCredentialRemove(ctx, subArg, options, ct);
                break;

            case "test":
                await HandleCredentialTest(ctx, subArg, options, ct);
                break;

            case "edit":
                await HandleCredentialEdit(ctx, subArg, options, ct);
                break;

            default:
                await HandleCredentialList(ctx, options, ct);
                break;
        }
    }

    private static async Task HandleCredentialList(
        CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
            var credentials = await repo.GetAllAsync(ct);

            if (credentials.Count == 0)
            {
                ctx.NavigationService.SetStatusMessage(
                    "No stored credentials. Use :cred add to add one.");
            }
            else
            {
                var list = string.Join(", ", credentials.Select(c =>
                    $"{c.Domain} ({c.CredentialType})"));
                ctx.NavigationService.SetStatusMessage($"Stored credentials: {list}");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to list credentials");
            ctx.NavigationService.SetStatusMessage("Failed to list credentials");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleCredentialAdd(
        CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        try
        {
            var domain = await ctx.InputHandler.PromptForInputAsync("Domain (e.g., nytimes.com): ", ct);
            if (string.IsNullOrWhiteSpace(domain))
            {
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            var username = await ctx.InputHandler.PromptForInputAsync("Username/email: ", ct);
            if (string.IsNullOrWhiteSpace(username))
            {
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            var password = await ctx.InputHandler.PromptForInputAsync("Password: ", ct, isSecret: true);
            if (string.IsNullOrWhiteSpace(password))
            {
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            // Check for known site defaults (e.g., NYT multi-step login)
            var normalizedDomain = domain.Trim().ToLowerInvariant();
            var hasDefaults = KnownSiteDefaults.TryGetValue(normalizedDomain, out var defaults);

            string? loginUrl = null;
            string? usernameSelector = null;
            string? passwordSelector = null;
            string? submitSelector = null;
            List<LoginStep>? loginSteps = null;

            if (hasDefaults)
            {
                loginUrl = defaults.LoginUrl;
                loginSteps = defaults.Steps;
                ctx.NavigationService.SetStatusMessage(
                    $"Using known {normalizedDomain} login flow ({loginSteps.Count}-step)");
                await ctx.RenderCurrentPageAsync(options, ct);
            }
            else
            {
                loginUrl = await ctx.InputHandler.PromptForInputAsync("Login URL (Enter to skip): ", ct);
                usernameSelector = await ctx.InputHandler.PromptForInputAsync("Username selector (Enter to skip): ", ct);
                passwordSelector = await ctx.InputHandler.PromptForInputAsync("Password selector (Enter to skip): ", ct);
                submitSelector = await ctx.InputHandler.PromptForInputAsync("Submit selector (Enter to skip): ", ct);
            }

            using var scope = ctx.ScopeFactory.CreateScope();
            var encryption = scope.ServiceProvider.GetRequiredService<ICookieEncryptionService>();
            var repo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var encryptedUsername = encryption.Encrypt(username.Trim());
            var encryptedPassword = encryption.Encrypt(password.Trim());

            var credential = SiteCredential.Create(
                domain.Trim(),
                CredentialType.FormLogin,
                encryptedUsername,
                encryptedPassword,
                usernameSelector?.Trim(),
                passwordSelector?.Trim(),
                submitSelector?.Trim(),
                loginUrl?.Trim(),
                loginSteps);

            await repo.AddAsync(credential, ct);
            await unitOfWork.SaveChangesAsync(ct);

            var msg = loginSteps != null
                ? $"Credential saved for {credential.Domain} (multi-step login)"
                : $"Credential saved for {credential.Domain}";
            ctx.NavigationService.SetStatusMessage(msg);
            ctx.Logger.LogInformation("Added credential for domain: {Domain}", credential.Domain);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to add credential");
            ctx.NavigationService.SetStatusMessage($"Failed to add credential: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleCredentialRemove(
        CommandContext ctx, string? domainArg, RenderOptions options, CancellationToken ct)
    {
        try
        {
            var domain = domainArg;
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = await ctx.InputHandler.PromptForInputAsync("Domain to remove: ", ct);
            }

            if (string.IsNullOrWhiteSpace(domain))
            {
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            domain = domain.Trim().ToLowerInvariant();

            using var scope = ctx.ScopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var credential = await repo.GetByDomainAsync(domain, ct);
            if (credential == null)
            {
                ctx.NavigationService.SetStatusMessage($"No credential found for {domain}");
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            await repo.DeleteAsync(credential.Id, ct);
            await unitOfWork.SaveChangesAsync(ct);

            ctx.NavigationService.SetStatusMessage($"Credential removed for {domain}");
            ctx.Logger.LogInformation("Removed credential for domain: {Domain}", domain);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to remove credential");
            ctx.NavigationService.SetStatusMessage($"Failed to remove credential: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleCredentialTest(
        CommandContext ctx, string? domainArg, RenderOptions options, CancellationToken ct)
    {
        try
        {
            var domain = domainArg;
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = await ctx.InputHandler.PromptForInputAsync("Domain to test: ", ct);
            }

            if (string.IsNullOrWhiteSpace(domain))
            {
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            domain = domain.Trim().ToLowerInvariant();

            using var scope = ctx.ScopeFactory.CreateScope();
            var autoLogin = scope.ServiceProvider.GetRequiredService<IAutoLoginService>();

            ctx.NavigationService.SetStatusMessage($"Testing login for {domain}...");
            await ctx.RenderCurrentPageAsync(options, ct);

            var result = await autoLogin.LoginAsync(domain, ct);

            if (result.Success)
            {
                ctx.NavigationService.SetStatusMessage($"Login succeeded for {domain}");
            }
            else if (result.ManualLoginRequired)
            {
                ctx.NavigationService.SetStatusMessage(
                    $"Manual login required for {domain}: {result.ErrorMessage}");
            }
            else
            {
                ctx.NavigationService.SetStatusMessage(
                    $"Login failed for {domain}: {result.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to test credential");
            ctx.NavigationService.SetStatusMessage($"Login test failed: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task HandleCredentialEdit(
        CommandContext ctx, string? domainArg, RenderOptions options, CancellationToken ct)
    {
        try
        {
            var domain = domainArg;
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = await ctx.InputHandler.PromptForInputAsync("Domain to edit: ", ct);
            }

            if (string.IsNullOrWhiteSpace(domain))
            {
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            domain = domain.Trim().ToLowerInvariant();

            using var scope = ctx.ScopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISiteCredentialRepository>();
            var encryption = scope.ServiceProvider.GetRequiredService<ICookieEncryptionService>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var credential = await repo.GetByDomainAsync(domain, ct);
            if (credential == null)
            {
                ctx.NavigationService.SetStatusMessage($"No credential found for {domain}");
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            // Decrypt current values to show as context
            var currentUsername = encryption.Decrypt(credential.EncryptedUsername);

            var newUsername = await ctx.InputHandler.PromptForInputAsync(
                $"Username [{currentUsername}]: ", ct);
            var newPassword = await ctx.InputHandler.PromptForInputAsync(
                "New password (Enter to keep): ", ct, isSecret: true);
            var newLoginUrl = await ctx.InputHandler.PromptForInputAsync(
                $"Login URL [{credential.LoginUrl ?? "none"}]: ", ct);
            var newUsernameSelector = await ctx.InputHandler.PromptForInputAsync(
                $"Username selector [{credential.UsernameSelector ?? "none"}]: ", ct);
            var newPasswordSelector = await ctx.InputHandler.PromptForInputAsync(
                $"Password selector [{credential.PasswordSelector ?? "none"}]: ", ct);
            var newSubmitSelector = await ctx.InputHandler.PromptForInputAsync(
                $"Submit selector [{credential.SubmitSelector ?? "none"}]: ", ct);

            var usernameToEncrypt = string.IsNullOrWhiteSpace(newUsername)
                ? currentUsername : newUsername.Trim();
            var encryptedUsername = encryption.Encrypt(usernameToEncrypt);

            byte[] encryptedPassword;
            if (string.IsNullOrWhiteSpace(newPassword))
            {
                // Keep existing password
                encryptedPassword = credential.EncryptedPassword;
            }
            else
            {
                encryptedPassword = encryption.Encrypt(newPassword.Trim());
            }

            credential.Update(
                encryptedUsername,
                encryptedPassword,
                string.IsNullOrWhiteSpace(newUsernameSelector) ? credential.UsernameSelector : newUsernameSelector.Trim(),
                string.IsNullOrWhiteSpace(newPasswordSelector) ? credential.PasswordSelector : newPasswordSelector.Trim(),
                string.IsNullOrWhiteSpace(newSubmitSelector) ? credential.SubmitSelector : newSubmitSelector.Trim(),
                string.IsNullOrWhiteSpace(newLoginUrl) ? credential.LoginUrl : newLoginUrl.Trim());

            await repo.UpdateAsync(credential, ct);
            await unitOfWork.SaveChangesAsync(ct);

            ctx.NavigationService.SetStatusMessage($"Credential updated for {domain}");
            ctx.Logger.LogInformation("Updated credential for domain: {Domain}", domain);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to edit credential");
            ctx.NavigationService.SetStatusMessage($"Failed to edit credential: {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
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
