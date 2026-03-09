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

            await ShowErrorScreenAsync(ctx, options, error, ct);
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
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Credentials{Reset}");

            var ttsIndicator = isTtsConfigured
                ? $"    {p.PromptFg.AnsiFg}\u25cf{Reset} OpenAI TTS API key     {p.PromptFg.AnsiFg}configured{Reset}"
                : $"    {p.ErrorFg.AnsiFg}\u25cb{Reset} OpenAI TTS API key     {p.ErrorFg.AnsiFg}not found{Reset}";
            helpers.WriteLine(ttsIndicator);

            if (!isTtsConfigured)
            {
                helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Required for text-to-speech audio generation{Reset}");
            }

            var gcsIndicator = isGcsConfigured
                ? $"    {p.PromptFg.AnsiFg}\u25cf{Reset} GCS bucket             {p.PromptFg.AnsiFg}{gcsConfig.BucketName}{Reset}"
                : $"    {p.SecondaryText.AnsiFg}\u25cb{Reset} GCS bucket             {p.SecondaryText.AnsiFg}not set (local-only){Reset}";
            helpers.WriteLine(gcsIndicator);

            if (!isGcsConfigured)
            {
                helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Optional \u2014 enables RSS feed publishing{Reset}");
            }

            if (cacheAnalysis != null)
            {
                helpers.WriteLine();
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Cache{Reset}");
                helpers.WriteLine(
                    $"    {p.PromptFg.AnsiFg}{cacheAnalysis.CachedArticles}{Reset}" +
                    $"{p.SecondaryText.AnsiFg} of {cacheAnalysis.TotalArticles} articles cached{Reset}");

                if (cacheAnalysis.UncachedArticles > 0)
                {
                    helpers.WriteLine(
                        $"    {p.SecondaryText.AnsiFg}Estimated cost for remaining:{Reset} " +
                        $"{p.PrimaryText.AnsiFg}${cacheAnalysis.EstimatedCost:F4}{Reset}");

                    // Estimate time: ~150 WPM * 5 chars/word = 750 chars/min for reading,
                    // but generation is roughly real-time, so use character count
                    var uncachedChars = cacheAnalysis.ArticleStatuses
                        .Where(s => !s.IsCached)
                        .Sum(s => s.EstimatedCost * 1_000_000m / 15.0m); // reverse cost to char count
                    var estimatedMinutes = (int)Math.Ceiling((double)uncachedChars / (150.0 * 5.0));
                    if (estimatedMinutes > 0)
                    {
                        helpers.WriteLine(
                            $"    {p.SecondaryText.AnsiFg}Estimated time:{Reset} " +
                            $"{p.PrimaryText.AnsiFg}~{estimatedMinutes} min{Reset}");
                    }
                }
                else
                {
                    helpers.WriteLine(
                        $"    {p.PromptFg.AnsiFg}All articles cached \u2014 no API cost{Reset}");
                }
            }

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
                        ttsService.SetApiKeyOverride(apiKey.Trim());
                        isTtsConfigured = ttsService.IsConfigured;
                        if (isTtsConfigured)
                        {
                            try { settingsStore.Set("OpenAiApiKey", apiKey.Trim(), encrypt: true); }
                            catch (Exception ex) { ctx.Logger.LogWarning(ex, "Failed to persist API key"); }
                        }
                    }

                    continue;
                }

                return true;
            }

            if (command.Type == CommandType.OpenCommandLine && !isGcsConfigured)
            {
                var bucketName = await ctx.InputHandler.PromptForInputAsync(
                    "GCS bucket name: ", ct);
                if (!string.IsNullOrWhiteSpace(bucketName))
                {
                    var trimmed = bucketName.Trim();
                    if (GcsConfiguration.IsValidBucketName(trimmed))
                    {
                        gcsConfig.BucketName = trimmed;
                        isGcsConfigured = true;
                        try { settingsStore.Set("GcsBucketName", trimmed); }
                        catch (Exception ex) { ctx.Logger.LogWarning(ex, "Failed to persist bucket name"); }
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

        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);
            RenderBox(helpers, p, "Podcast Ready!", width);
            helpers.WriteLine();

            var duration = FormatDuration(result.TotalDuration);
            var fileSize = FormatFileSize(result.FileSizeBytes);
            helpers.WriteLine(
                $"  {p.HeaderTitleFg.AnsiFg}\u266b{Reset} {p.PrimaryText.AnsiFg}" +
                $"{result.ArticlesProcessed} articles \u00bb\u00bb\u00bb {duration} \u00bb\u00bb\u00bb {fileSize}{Reset}");
            helpers.WriteLine();

            if (result.ArticlesFailed > 0)
            {
                helpers.WriteLine($"  {p.ErrorFg.AnsiFg}{result.ArticlesFailed} article(s) failed{Reset}");
                helpers.WriteLine();
            }

            if (!string.IsNullOrEmpty(result.LocalFilePath))
            {
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}File:{Reset}  {p.PrimaryText.AnsiFg}{result.LocalFilePath}{Reset}");
            }

            if (!string.IsNullOrEmpty(result.FeedUrl))
            {
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Feed:{Reset}  {p.PromptFg.AnsiFg}{result.FeedUrl}{Reset}");
                helpers.WriteLine();
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Feed URL copied to clipboard{Reset}");
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

            if (command.Type is CommandType.ActivateLink or CommandType.GoBack or CommandType.Quit)
            {
                break;
            }
        }
    }

    private static async Task ShowErrorScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        string errorMessage,
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

            var wrappedLines = RenderHelpers.WrapText(errorMessage, width - 4);
            foreach (var line in wrappedLines)
            {
                helpers.WriteLine($"  {p.ErrorFg.AnsiFg}{line}{Reset}");
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
