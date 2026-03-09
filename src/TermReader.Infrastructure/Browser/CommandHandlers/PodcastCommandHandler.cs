// Educational and personal use only.

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Renderers;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles podcast generation: confirmation, progress, and completion screens.
/// </summary>
internal static class PodcastCommandHandler
{
    private const string Reset = "\x1b[0m";
    private const int AnimationIntervalMs = 500;
    private static readonly string[] AnimationFrames = [".", "..", "..."];

    private enum ArticleState
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Cached,
    }

    public static async Task HandleGeneratePodcast(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        if (ctx.NavigationService.CurrentContext.ViewMode != ViewMode.CollectionItems)
        {
            ctx.NavigationService.SetStatusMessage("Open a collection first, then press p");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var collection = ctx.NavigationService.ActiveCollection;
        if (collection == null || collection.Items.Count == 0)
        {
            ctx.NavigationService.SetStatusMessage("No articles to generate podcast from");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        // Press feedback: render one frame with inverted button colors, then continue
        var pressedOptions = options with { PodcastButtonState = 1 }; // 1 = Pressed
        await ctx.RenderCurrentPageAsync(pressedOptions, ct);
        await Task.Delay(100, ct);

        using var scope = ctx.ScopeFactory.CreateScope();
        var ttsService = scope.ServiceProvider.GetRequiredService<ITtsService>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IPodcastOrchestrator>();
        var gcsConfig = scope.ServiceProvider
            .GetRequiredService<IOptions<GcsConfiguration>>().Value;
        var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();

        // Hydrate persisted settings if not already configured
        if (!ttsService.IsConfigured)
        {
            var savedKey = settingsStore.Get("OpenAiApiKey");
            if (!string.IsNullOrWhiteSpace(savedKey))
            {
                ttsService.SetApiKeyOverride(savedKey);

                // Validate the saved key still works (5s timeout — don't block on network issues)
                try
                {
                    using var validationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    validationCts.CancelAfter(TimeSpan.FromSeconds(5));
                    var validation = await ttsService.ValidateApiKeyAsync(validationCts.Token);

                    if (!validation.IsValid && validation.ErrorCode is "invalid_key" or "insufficient_credits")
                    {
                        ctx.Logger.LogWarning(
                            "Saved API key is no longer valid ({ErrorCode}), clearing", validation.ErrorCode);
                        settingsStore.Remove("OpenAiApiKey");
                        ttsService.SetApiKeyOverride(string.Empty);
                        ctx.NavigationService.SetStatusMessage("Saved API key is no longer valid");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Validation timed out (network unreachable) — proceed with saved key
                    ctx.Logger.LogDebug("API key validation timed out, proceeding with saved key");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(gcsConfig.BucketName))
        {
            var savedBucket = settingsStore.Get("GcsBucketName");
            if (!string.IsNullOrWhiteSpace(savedBucket) && GcsConfiguration.IsValidBucketName(savedBucket))
            {
                gcsConfig.BucketName = savedBucket;
            }
        }

        // Pre-flight cache analysis for cost estimation
        CacheAnalysis? cacheAnalysis = null;
        if (ttsService.IsConfigured)
        {
            try
            {
                cacheAnalysis = await orchestrator.AnalyzeCacheStatusAsync(collection, ct);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Cache analysis failed, continuing without it");
            }
        }

        var confirmed = await ShowConfirmationScreenAsync(
            ctx,
            options,
            collection,
            ttsService,
            gcsConfig,
            settingsStore,
            cacheAnalysis,
            ct);

        if (!confirmed)
        {
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        var result = await ShowProgressScreenAsync(ctx, options, collection, orchestrator, ct);

        if (result == null)
        {
            ctx.NavigationService.SetStatusMessage("Podcast generation cancelled");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        if (!result.Success)
        {
            // If TTS failed due to auth error, clear the persisted key so user is re-prompted
            var error = result.ErrorMessage ?? "Unknown error";
            if (error.Contains("401", StringComparison.Ordinal) ||
                error.Contains("403", StringComparison.Ordinal) ||
                error.Contains("not configured", StringComparison.OrdinalIgnoreCase))
            {
                settingsStore.Remove("OpenAiApiKey");
                ttsService.SetApiKeyOverride(string.Empty);
            }

            await ShowErrorScreenAsync(ctx, options, error, result.FailedArticleDetails, ct);
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        await ShowCompletionScreenAsync(ctx, options, result, ct);
        await ctx.RenderCurrentPageAsync(options, ct);
    }

    private static async Task<bool> ShowConfirmationScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        Domain.Entities.Collections.Collection collection,
        ITtsService ttsService,
        GcsConfiguration gcsConfig,
        IUserSettingsStore settingsStore,
        CacheAnalysis? cacheAnalysis,
        CancellationToken ct)
    {
        var isTtsConfigured = ttsService.IsConfigured;
        var isGcsConfigured = !string.IsNullOrWhiteSpace(gcsConfig.BucketName);
        string? bucketError = null;

        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);
            RenderBox(helpers, p, "Generate Podcast", width);
            helpers.WriteLine();
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Collection:{Reset}   {p.PrimaryText.AnsiFg}{collection.Name}{Reset}");
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Articles:{Reset}     {p.PrimaryText.AnsiFg}{collection.Items.Count}{Reset}");
            helpers.WriteLine();

            // --- Credentials ---
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Credentials{Reset}");

            var ttsIndicator = isTtsConfigured
                ? $"    {p.PromptFg.AnsiFg}\u25cf{Reset} OpenAI TTS API key     {p.PromptFg.AnsiFg}configured{Reset}"
                : $"    {p.ErrorFg.AnsiFg}\u25cb{Reset} OpenAI TTS API key     {p.ErrorFg.AnsiFg}not set{Reset}";
            helpers.WriteLine(ttsIndicator);

            if (!isTtsConfigured)
            {
                helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Required \u2014 get a key at platform.openai.com/api-keys{Reset}");
            }

            var gcsIndicator = isGcsConfigured
                ? $"    {p.PromptFg.AnsiFg}\u25cf{Reset} GCS bucket             {p.PromptFg.AnsiFg}{gcsConfig.BucketName}{Reset}"
                : $"    {p.SecondaryText.AnsiFg}\u25cb{Reset} GCS bucket             {p.SecondaryText.AnsiFg}not set (local-only){Reset}";
            helpers.WriteLine(gcsIndicator);

            if (!isGcsConfigured)
            {
                if (bucketError != null)
                {
                    helpers.WriteLine($"      {p.ErrorFg.AnsiFg}{bucketError}{Reset}");
                }
                else
                {
                    helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Optional \u2014 enables RSS feed for podcast apps{Reset}");
                    helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Format: my-bucket-name (3\u201363 chars, lowercase, a\u2013z/0\u20139/hyphens/dots){Reset}");
                }
            }

            // --- Output ---
            helpers.WriteLine();
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Output{Reset}");
            helpers.WriteLine($"    {p.PrimaryText.AnsiFg}M4B audiobook with chapter markers per article{Reset}");

            if (isGcsConfigured)
            {
                helpers.WriteLine(
                    $"    {p.SecondaryText.AnsiFg}Feed:{Reset} " +
                    $"{p.PromptFg.AnsiFg}storage.googleapis.com/{gcsConfig.BucketName}/podcasts/\u2026/feed.xml{Reset}");
            }
            else
            {
                helpers.WriteLine($"    {p.SecondaryText.AnsiFg}Saved locally \u2014 configure GCS bucket for RSS feed{Reset}");
            }

            // --- Cache / Cost ---
            if (cacheAnalysis != null)
            {
                helpers.WriteLine();
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Estimate{Reset}");
                helpers.WriteLine(
                    $"    {p.PromptFg.AnsiFg}{cacheAnalysis.CachedArticles}{Reset}" +
                    $"{p.SecondaryText.AnsiFg} of {cacheAnalysis.TotalArticles} articles cached{Reset}");

                if (cacheAnalysis.UncachedArticles > 0)
                {
                    helpers.WriteLine(
                        $"    {p.SecondaryText.AnsiFg}Cost for remaining:{Reset} " +
                        $"{p.PrimaryText.AnsiFg}${cacheAnalysis.EstimatedCost:F4}{Reset}");

                    // Estimate time: reverse cost to char count, then use ~750 chars/min
                    var uncachedChars = cacheAnalysis.ArticleStatuses
                        .Where(s => !s.IsCached)
                        .Sum(s => s.EstimatedCost * 1_000_000m / 15.0m);
                    var estimatedMinutes = (int)Math.Ceiling((double)uncachedChars / (150.0 * 5.0));
                    if (estimatedMinutes > 0)
                    {
                        helpers.WriteLine(
                            $"    {p.SecondaryText.AnsiFg}Time:{Reset} " +
                            $"{p.PrimaryText.AnsiFg}~{estimatedMinutes} min{Reset}");
                    }
                }
                else
                {
                    helpers.WriteLine(
                        $"    {p.PromptFg.AnsiFg}All articles cached \u2014 no API cost{Reset}");
                }
            }

            // --- Key hints ---
            helpers.WriteLine();

            var hints = new StringBuilder();
            if (!isTtsConfigured)
            {
                hints.Append($"  {p.PrimaryText.AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:set API key   {Reset}");
            }
            else
            {
                hints.Append($"  {p.PrimaryText.AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:generate   {Reset}");
            }

            if (!isGcsConfigured)
            {
                hints.Append($"{p.PrimaryText.AnsiFg}:{Reset}{p.SecondaryText.AnsiFg}set bucket   {Reset}");
            }

            hints.Append($"{p.PrimaryText.AnsiFg}Esc{Reset}{p.SecondaryText.AnsiFg}:cancel{Reset}");
            helpers.WriteLine(hints.ToString());

            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                return false;
            }

            if (command.Type == CommandType.ActivateLink)
            {
                if (!isTtsConfigured)
                {
                    var apiKey = await ctx.InputHandler.PromptForInputAsync(
                        "OpenAI API key (platform.openai.com/api-keys): ", ct, isSecret: true);
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        var trimmedKey = apiKey.Trim();
                        ttsService.SetApiKeyOverride(trimmedKey);

                        // Validate with a minimal API call before persisting
                        Console.Write($"\r  {p.SecondaryText.AnsiFg}Verifying API key...{Reset}");
                        var validation = await ttsService.ValidateApiKeyAsync(ct);

                        if (validation.IsValid)
                        {
                            Console.Write($"\r  {p.PromptFg.AnsiFg}API key verified     {Reset}");
                            isTtsConfigured = true;

                            try
                            {
                                settingsStore.Set("OpenAiApiKey", trimmedKey, encrypt: true);
                            }
                            catch (Exception ex)
                            {
                                ctx.Logger.LogWarning(ex, "Failed to persist API key");
                            }

                            await Task.Delay(800, ct);
                        }
                        else
                        {
                            Console.Write(
                                $"\r  {p.ErrorFg.AnsiFg}{validation.ErrorMessage ?? "Invalid API key"}{Reset}" +
                                new string(' ', 20));
                            await Task.Delay(2000, ct);

                            // Revert — do not persist invalid keys
                            ttsService.SetApiKeyOverride(string.Empty);
                            isTtsConfigured = false;
                        }
                    }

                    continue;
                }

                return true;
            }

            if (command.Type == CommandType.OpenCommandLine && !isGcsConfigured)
            {
                bucketError = null;
                var bucketName = await ctx.InputHandler.PromptForInputAsync(
                    "GCS bucket name (e.g. my-podcast-feed): ", ct);
                if (!string.IsNullOrWhiteSpace(bucketName))
                {
                    var trimmed = bucketName.Trim();
                    if (GcsConfiguration.IsValidBucketName(trimmed))
                    {
                        gcsConfig.BucketName = trimmed;
                        isGcsConfigured = true;
                        bucketError = null;
                        try { settingsStore.Set("GcsBucketName", trimmed); }
                        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Failed to persist bucket name"); }
                    }
                    else
                    {
                        bucketError = $"Invalid: \"{trimmed}\" \u2014 must be 3\u201363 chars, lowercase a\u2013z/0\u20139/hyphens/dots";
                    }
                }

                continue;
            }
        }

        return false;
    }

    private static async Task<PodcastResult?> ShowProgressScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        Domain.Entities.Collections.Collection collection,
        IPodcastOrchestrator orchestrator,
        CancellationToken ct)
    {
        var articleCount = collection.Items.Count;
        var statuses = new ArticleStatus[articleCount];
        for (var i = 0; i < articleCount; i++)
        {
            statuses[i] = new ArticleStatus
            {
                Title = collection.Items[i].Title,
                State = ArticleState.Pending,
            };
        }

        PodcastProgress? latestProgress = null;
        var animFrame = 0;

        var lastProcessingIndex = -1;
        var progress = new Progress<PodcastProgress>(p =>
        {
            Volatile.Write(ref latestProgress, p);

            if (p.Phase == PodcastPhase.GeneratingAudio && p.CurrentArticle > 0 && p.CurrentArticle <= articleCount)
            {
                var idx = p.CurrentArticle - 1;

                if (lastProcessingIndex >= 0 && lastProcessingIndex < idx &&
                    statuses[lastProcessingIndex].State == ArticleState.Processing)
                {
                    statuses[lastProcessingIndex].State = ArticleState.Completed;
                }

                statuses[idx].State = p.IsFromCache ? ArticleState.Cached : ArticleState.Processing;
                lastProcessingIndex = idx;
            }
        });

        using var genCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var generationTask = orchestrator.GeneratePodcastAsync(collection, progress, genCts.Token);

        Task<NavigationCommand>? pendingKeyTask = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
                var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
                helpers.Clear();

                var currentProgress = Volatile.Read(ref latestProgress);
                RenderProgressContent(
                    helpers,
                    palette,
                    currentProgress,
                    animFrame,
                    statuses,
                    options.TerminalWidth,
                    options.TerminalHeight);
                helpers.ClearRemainingLines();

                pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(ct);
                var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task completed;
                try
                {
                    var tickTask = Task.Delay(AnimationIntervalMs, tickCts.Token);
                    completed = await Task.WhenAny(pendingKeyTask, generationTask, tickTask);
                    await tickCts.CancelAsync();
                }
                finally
                {
                    tickCts.Dispose();
                }

                if (completed == pendingKeyTask)
                {
                    var command = await pendingKeyTask;
                    pendingKeyTask = null;

                    if (command.Type == CommandType.TerminalResized)
                    {
                        options = ctx.GetCurrentRenderOptions();
                        continue;
                    }

                    if (command.Type == CommandType.GoBack)
                    {
                        // If generation already completed, fall through to collect result
                        if (generationTask.IsCompleted)
                        {
                            break;
                        }

                        var shouldCancel = await ShowCancellationConfirmAsync(ctx, ct);
                        if (shouldCancel)
                        {
                            await genCts.CancelAsync();
                            try
                            {
                                await generationTask;
                            }
                            catch (OperationCanceledException)
                            {
                                // Expected
                            }

                            return null;
                        }
                    }
                }
                else if (completed == generationTask)
                {
                    break;
                }
                else
                {
                    animFrame = (animFrame + 1) % AnimationFrames.Length;
                }
            }

            // Collect the result from the completed generation task
            if (generationTask.IsCompleted)
            {
                try
                {
                    var result = await generationTask;

                    for (var i = 0; i < statuses.Length; i++)
                    {
                        if (statuses[i].State == ArticleState.Processing)
                        {
                            statuses[i].State = ArticleState.Completed;
                        }
                    }

                    return result;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogError(ex, "Podcast generation failed");
                    return PodcastResult.Failure(ex.Message);
                }
            }
        }
        finally
        {
            if (!generationTask.IsCompleted)
            {
                await genCts.CancelAsync();
                try
                {
                    await generationTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }
        }

        return null;
    }

    private static async Task<bool> ShowCancellationConfirmAsync(
        CommandContext ctx,
        CancellationToken ct)
    {
        var response = await ctx.InputHandler.PromptForInputAsync(
            "Cancel podcast generation? (y/n): ", ct);
        return string.Equals(response, "y", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ShowCompletionScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        PodcastResult result,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(result.FeedUrl))
        {
            CopyToClipboardOsc52(result.FeedUrl);
        }

        var scrollOffset = 0;

        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);

            // Build all content lines, then render a scrollable window
            var lines = BuildCompletionLines(p, result, width);

            // Reserve 2 lines for key hints at bottom
            var viewportHeight = options.TerminalHeight - 2;
            var maxScroll = Math.Max(0, lines.Count - viewportHeight);
            scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);

            var visibleCount = Math.Min(lines.Count - scrollOffset, viewportHeight);
            for (var i = 0; i < visibleCount; i++)
            {
                helpers.WriteLine(lines[scrollOffset + i]);
            }

            // Pad remaining viewport
            for (var i = visibleCount; i < viewportHeight; i++)
            {
                helpers.WriteLine();
            }

            // Key hints
            var hints = $"  {p.PrimaryText.AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:back{Reset}";
            if (maxScroll > 0)
            {
                hints += $"   {p.PrimaryText.AnsiFg}j/k{Reset}{p.SecondaryText.AnsiFg}:scroll{Reset}";
            }

            helpers.WriteLine(hints);
            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type is CommandType.ActivateLink or CommandType.GoBack or CommandType.Quit)
            {
                break;
            }

            if (command.Type == CommandType.MoveDown)
            {
                scrollOffset = Math.Min(scrollOffset + 1, maxScroll);
            }
            else if (command.Type == CommandType.MoveUp)
            {
                scrollOffset = Math.Max(scrollOffset - 1, 0);
            }
            else if (command.Type == CommandType.PageDown)
            {
                scrollOffset = Math.Min(scrollOffset + (viewportHeight / 2), maxScroll);
            }
            else if (command.Type == CommandType.PageUp)
            {
                scrollOffset = Math.Max(scrollOffset - (viewportHeight / 2), 0);
            }
        }
    }

    private static List<string> BuildCompletionLines(
        ThemePalette p,
        PodcastResult result,
        int width)
    {
        var lines = new List<string>();

        // Box header
        lines.Add(string.Empty);
        lines.Add($"{p.HeaderBorderFg.AnsiFg}\u256d{new string('\u2500', width - 2)}\u256e{Reset}");
        var boxTitle = RenderHelpers.TruncateText("Podcast Ready!", width - 4);
        lines.Add(
            $"{p.HeaderBorderFg.AnsiFg}\u2502 {p.HeaderTitleFg.AnsiFg}" +
            $"{boxTitle.PadRight(width - 4)}{p.HeaderBorderFg.AnsiFg} \u2502{Reset}");
        lines.Add($"{p.HeaderBorderFg.AnsiFg}\u2570{new string('\u2500', width - 2)}\u256f{Reset}");
        lines.Add(string.Empty);

        // --- Summary ---
        var duration = FormatDuration(result.TotalDuration);
        var fileSize = FormatFileSize(result.FileSizeBytes);
        lines.Add(
            $"  {p.HeaderTitleFg.AnsiFg}\u266b{Reset} {p.PrimaryText.AnsiFg}" +
            $"{result.ArticlesProcessed} articles \u00bb\u00bb\u00bb {duration} \u00bb\u00bb\u00bb {fileSize}{Reset}");

        if (result.ArticlesCached > 0)
        {
            lines.Add(
                $"    {p.SecondaryText.AnsiFg}{result.ArticlesCached} from cache{Reset}");
        }

        if (result.TotalCost > 0)
        {
            lines.Add(
                $"    {p.SecondaryText.AnsiFg}API cost: ${result.TotalCost:F4}{Reset}");
        }

        if (result.ArticlesFailed > 0)
        {
            lines.Add(
                $"    {p.ErrorFg.AnsiFg}{result.ArticlesFailed} article(s) failed{Reset}");
        }

        lines.Add(string.Empty);

        // --- File ---
        if (!string.IsNullOrEmpty(result.LocalFilePath))
        {
            lines.Add($"  {p.SecondaryText.AnsiFg}File{Reset}");
            lines.Add($"    {p.PrimaryText.AnsiFg}{result.LocalFilePath}{Reset}");
            lines.Add(string.Empty);
        }

        // --- Feed + subscription instructions ---
        if (!string.IsNullOrEmpty(result.FeedUrl))
        {
            lines.Add($"  {p.SecondaryText.AnsiFg}Feed{Reset}");
            lines.Add($"    {p.PromptFg.AnsiFg}{result.FeedUrl}{Reset}");
            lines.Add($"    {p.SecondaryText.AnsiFg}Feed URL copied to clipboard{Reset}");
            lines.Add(string.Empty);
            lines.Add($"  {p.SecondaryText.AnsiFg}What\u2019s next{Reset}");
            lines.Add($"    {p.PrimaryText.AnsiFg}1.{Reset} {p.PrimaryText.AnsiFg}Subscribe in your podcast app{Reset}");
            lines.Add($"       {p.SecondaryText.AnsiFg}Apple Podcasts / Overcast \u2192 Add show by URL \u2192 paste{Reset}");
            lines.Add($"       {p.SecondaryText.AnsiFg}Pocket Casts \u2192 Search \u2192 \u201cAdd by URL\u201d \u2192 paste{Reset}");
            lines.Add($"       {p.SecondaryText.AnsiFg}Any RSS reader \u2192 Add feed \u2192 paste{Reset}");
            lines.Add($"    {p.PrimaryText.AnsiFg}2.{Reset} {p.PrimaryText.AnsiFg}Take a walk{Reset}");
            lines.Add($"       {p.SecondaryText.AnsiFg}Next time you generate, subscribe first and go do{Reset}");
            lines.Add($"       {p.SecondaryText.AnsiFg}something else. The episode will be waiting for you.{Reset}");
        }
        else
        {
            // Local-only instructions
            lines.Add($"  {p.SecondaryText.AnsiFg}Listen{Reset}");
            lines.Add($"    {p.PrimaryText.AnsiFg}VLC{Reset}{p.SecondaryText.AnsiFg} \u2014 File \u2192 Open, supports chapters{Reset}");
            lines.Add($"    {p.PrimaryText.AnsiFg}Apple Books{Reset}{p.SecondaryText.AnsiFg} \u2014 drag M4B file into library{Reset}");
            lines.Add(string.Empty);
            lines.Add($"    {p.SecondaryText.AnsiFg}Configure a GCS bucket to publish as RSS feed{Reset}");
        }

        lines.Add(string.Empty);

        return lines;
    }

    private static async Task ShowErrorScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        string errorMessage,
        IReadOnlyList<ArticleFailure> failedArticles,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);
            RenderBox(helpers, p, "Podcast Error", width);
            helpers.WriteLine();

            // Error summary
            var wrappedLines = RenderHelpers.WrapText(errorMessage, width - 4);
            foreach (var line in wrappedLines)
            {
                helpers.WriteLine($"  {p.ErrorFg.AnsiFg}{line}{Reset}");
            }

            helpers.WriteLine();

            // Per-article failure details
            if (failedArticles.Count > 0)
            {
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Failed articles:{Reset}");

                var maxArticleLines = Math.Max(1,
                    options.TerminalHeight - helpers.LinesWritten - 10);
                var displayCount = Math.Min(failedArticles.Count, maxArticleLines);

                for (var i = 0; i < displayCount; i++)
                {
                    var failure = failedArticles[i];
                    var displayTitle = RenderHelpers.TruncateText(failure.Title, width - 8);
                    helpers.WriteLine(
                        $"    {p.ErrorFg.AnsiFg}\u2717{Reset} {p.PrimaryText.AnsiFg}{displayTitle}{Reset}");
                    var reason = RenderHelpers.TruncateText(failure.Reason, width - 10);
                    helpers.WriteLine(
                        $"      {p.SecondaryText.AnsiFg}{reason}{Reset}");
                }

                if (failedArticles.Count > displayCount)
                {
                    helpers.WriteLine(
                        $"    {p.SecondaryText.AnsiFg}... and {failedArticles.Count - displayCount} more{Reset}");
                }

                helpers.WriteLine();
            }

            // Actionable suggestions based on error type
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}What to try:{Reset}");
            foreach (var suggestion in GetSuggestionsForError(errorMessage, failedArticles))
            {
                helpers.WriteLine($"    {p.SecondaryText.AnsiFg}\u2022 {suggestion}{Reset}");
            }

            helpers.WriteLine();
            helpers.WriteLine($"  {p.PrimaryText.AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:back{Reset}");
            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type != CommandType.NoOp)
            {
                break;
            }
        }
    }

    private static List<string> GetSuggestionsForError(
        string errorMessage,
        IReadOnlyList<ArticleFailure> failedArticles)
    {
        var suggestions = new List<string>();

        if (errorMessage.Contains("No readable articles", StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add("Open articles in the browser first to populate the page cache");
            suggestions.Add("Some sites block automated content extraction");
            if (failedArticles.Any(f => f.Reason.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
                                        f.Reason.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
            {
                suggestions.Add("Try browsing the blocked articles manually to pass bot checks");
            }
        }
        else if (errorMessage.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add("Install FFmpeg: brew install ffmpeg (macOS) or apt install ffmpeg (Linux)");
            suggestions.Add("Ensure ffmpeg is available in your PATH");
        }
        else if (errorMessage.Contains("budget", StringComparison.OrdinalIgnoreCase) ||
                 errorMessage.Contains("cost", StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add("Remove some articles from the collection to reduce cost");
            suggestions.Add("Increase MaxBudgetUsd in configuration");
        }
        else if (errorMessage.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
                 errorMessage.Contains("not configured", StringComparison.OrdinalIgnoreCase) ||
                 errorMessage.Contains("401", StringComparison.Ordinal) ||
                 errorMessage.Contains("403", StringComparison.Ordinal))
        {
            suggestions.Add("Check your OpenAI API key is valid at platform.openai.com/api-keys");
            suggestions.Add("Ensure your account has sufficient credits");
        }
        else if (errorMessage.Contains("TTS", StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add("Check your OpenAI API key and account status");
            suggestions.Add("Try again — transient API errors are common");
        }

        if (suggestions.Count == 0)
        {
            suggestions.Add("Try again — the error may be transient");
            suggestions.Add("Check the application logs for more details");
        }

        return suggestions;
    }

    private static void RenderBox(RenderHelpers helpers, ThemePalette p, string title, int width)
    {
        helpers.WriteLine();
        helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u256d{new string('\u2500', width - 2)}\u256e{Reset}");
        var displayTitle = RenderHelpers.TruncateText(title, width - 4);
        helpers.WriteLine(
            $"{p.HeaderBorderFg.AnsiFg}\u2502 {p.HeaderTitleFg.AnsiFg}" +
            $"{displayTitle.PadRight(width - 4)}{p.HeaderBorderFg.AnsiFg} \u2502{Reset}");
        helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u2570{new string('\u2500', width - 2)}\u256f{Reset}");
    }

    private static void RenderProgressContent(
        RenderHelpers helpers,
        ThemePalette p,
        PodcastProgress? progress,
        int animFrame,
        ArticleStatus[] statuses,
        int terminalWidth,
        int terminalHeight)
    {
        var width = Math.Max(20, terminalWidth - 2);
        RenderBox(helpers, p, "Generating Podcast", width);
        helpers.WriteLine();

        var phaseName = progress?.Phase switch
        {
            PodcastPhase.CachingContent => "Loading Articles",
            PodcastPhase.GeneratingAudio => "Generating Audio",
            PodcastPhase.AssemblingAudio => "Assembling M4B",
            PodcastPhase.Publishing => "Publishing Feed",
            _ => "Preparing",
        };

        helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Phase:{Reset} {p.PrimaryText.AnsiFg}{phaseName}{Reset}");
        helpers.WriteLine();

        var percent = progress?.PercentComplete ?? 0;
        var barWidth = Math.Max(10, width - 12);
        var filled = (int)(barWidth * percent / 100.0);
        var empty = barWidth - filled;
        var filledBar = new string('\u2588', filled);
        var emptyBar = new string('.', empty);
        helpers.WriteLine(
            $"  {p.PromptFg.AnsiFg}{filledBar}{p.SecondaryText.AnsiFg}{emptyBar}{Reset} {percent}%");
        helpers.WriteLine();

        var maxArticleLines = Math.Max(1, terminalHeight - helpers.LinesWritten - 4);
        var articleCount = Math.Min(statuses.Length, maxArticleLines);

        for (var i = 0; i < articleCount; i++)
        {
            var status = statuses[i];
            var displayTitle = RenderHelpers.TruncateText(status.Title, width - 10);

            var line = status.State switch
            {
                ArticleState.Cached =>
                    $"  {p.PromptFg.AnsiFg}\u2713{Reset} {displayTitle} {p.SecondaryText.AnsiFg}(cached){Reset}",
                ArticleState.Completed =>
                    $"  {p.PromptFg.AnsiFg}\u2713{Reset} {displayTitle}",
                ArticleState.Processing =>
                    $"  {p.HeaderTitleFg.AnsiFg}\u266b{Reset} {displayTitle}" +
                    $"{p.SecondaryText.AnsiFg}{AnimationFrames[animFrame]}{Reset}",
                ArticleState.Failed =>
                    $"  {p.ErrorFg.AnsiFg}\u2717{Reset} {displayTitle}",
                _ =>
                    $"  {p.SecondaryText.AnsiFg}  {displayTitle}{Reset}",
            };

            helpers.WriteLine(line);
        }

        if (statuses.Length > articleCount)
        {
            helpers.WriteLine(
                $"  {p.SecondaryText.AnsiFg}... and {statuses.Length - articleCount} more{Reset}");
        }

        helpers.WriteLine();
        helpers.WriteLine($"  {p.PrimaryText.AnsiFg}Esc{Reset}{p.SecondaryText.AnsiFg}:cancel{Reset}");
    }

    private static void CopyToClipboardOsc52(string text)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Console.Write($"\x1b]52;c;{base64}\x07");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B",
        };
    }

    private sealed class ArticleStatus
    {
        public required string Title { get; init; }

        public ArticleState State { get; set; }
    }
}
