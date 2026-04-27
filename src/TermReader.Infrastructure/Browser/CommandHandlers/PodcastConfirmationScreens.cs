// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Audio;
using TermReader.Application.Interfaces.Browser;
using TermReader.Application.Interfaces.Podcast;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Renderers;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Cache-analysis, cache-wait, and confirmation screens for podcast generation,
/// extracted from PodcastCommandHandler.
/// </summary>
internal static class PodcastConfirmationScreens
{
    private const string Reset = PodcastCommandHandler.Reset;

    /// <summary>
    /// Shows a progress screen while AnalyzeCacheStatusAsync extracts article content
    /// for cache/cost analysis. Returns null if the user cancels or the analysis fails.
    /// </summary>
    internal static async Task<CacheAnalysis?> ShowCacheAnalysisScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        Domain.Entities.Collections.Collection collection,
        IPodcastOrchestrator orchestrator,
        CancellationToken ct)
    {
        var articleCount = collection.Items.Count;
        var statuses = new PodcastCommandHandler.ArticleStatus[articleCount];
        for (var i = 0; i < articleCount; i++)
        {
            statuses[i] = new PodcastCommandHandler.ArticleStatus
            {
                Title = collection.Items[i].Title,
                State = PodcastCommandHandler.ArticleState.Pending,
            };
        }

        var animFrame = 0;

        var progress = new Progress<ContentExtractionProgress>(p =>
        {
            if (p.Current < 1 || p.Current > articleCount)
            {
                return;
            }

            var idx = p.Current - 1;
            if (p.IsCompleted)
            {
                statuses[idx].State = p.IsSuccess ? PodcastCommandHandler.ArticleState.Completed : PodcastCommandHandler.ArticleState.Failed;
                statuses[idx].Method = null;
            }
            else
            {
                statuses[idx].State = PodcastCommandHandler.ArticleState.Processing;
                statuses[idx].Method = p.ExtractionMethod;
            }
        });

        // Pass the parent ct — NOT a separate CTS — so the analysis keeps running
        // even if the user skips the UI. The orchestrator stores the running task
        // and GeneratePodcastAsync will await it to reuse extracted articles.
        var analysisTask = orchestrator.AnalyzeCacheStatusAsync(collection, progress, ct);
        Task<NavigationCommand>? pendingKeyTask = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
                var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
                helpers.Clear();

                var width = Math.Max(20, options.TerminalWidth - 2);
                PodcastCommandHandler.RenderBox(helpers, palette, "Analyzing Articles", width);
                helpers.WriteLine();

                var completedCount = statuses.Count(s => s.State is PodcastCommandHandler.ArticleState.Completed or PodcastCommandHandler.ArticleState.Failed);
                var dots = PodcastCommandHandler.AnimationFrames[animFrame % PodcastCommandHandler.AnimationFrames.Length];
                helpers.WriteLine(
                    $"  {palette.PrimaryText.AnsiFg}{completedCount}{Reset}" +
                    $"{palette.SecondaryText.AnsiFg} of {articleCount} analyzed{dots}{Reset}");
                helpers.WriteLine();

                var maxArticleLines = Math.Max(1, options.TerminalHeight - helpers.LinesWritten - 4);
                var displayCount = Math.Min(articleCount, maxArticleLines);
                var maxTitleLen = Math.Max(10, width - 10);

                for (var i = 0; i < displayCount; i++)
                {
                    var status = statuses[i];
                    var displayTitle = RenderHelpers.TruncateText(status.Title, maxTitleLen);
                    var methodSuffix = status.Method != null
                        ? $" {palette.SecondaryText.AnsiFg}({status.Method}){Reset}"
                        : string.Empty;

                    var line = status.State switch
                    {
                        PodcastCommandHandler.ArticleState.Completed =>
                            $"  {palette.GetSuccessFg().AnsiFg}✓{Reset} {displayTitle}",
                        PodcastCommandHandler.ArticleState.Processing =>
                            $"  {palette.HeaderTitleFg.AnsiFg}↻{Reset} {displayTitle}" +
                            $"{methodSuffix}" +
                            $"{palette.SecondaryText.AnsiFg}{PodcastCommandHandler.AnimationFrames[animFrame]}{Reset}",
                        PodcastCommandHandler.ArticleState.Failed =>
                            $"  {palette.ErrorFg.AnsiFg}✗{Reset} {displayTitle}",
                        _ =>
                            $"  {palette.SecondaryText.AnsiFg}  {displayTitle}{Reset}",
                    };

                    helpers.WriteLine(line);
                }

                if (articleCount > displayCount)
                {
                    helpers.WriteLine(
                        $"  {palette.SecondaryText.AnsiFg}... and {articleCount - displayCount} more{Reset}");
                }

                helpers.WriteLine();
                helpers.WriteLine(
                    $"  {palette.GetAccentFg().AnsiFg}Esc{Reset}{palette.SecondaryText.AnsiFg}:skip{Reset}");
                helpers.ClearRemainingLines();

                pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(ct);
                var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task completed;
                try
                {
                    var tickTask = Task.Delay(PodcastCommandHandler.AnimationIntervalMs, tickCts.Token);
                    completed = await Task.WhenAny(pendingKeyTask, analysisTask, tickTask).ConfigureAwait(false);
                    await tickCts.CancelAsync().ConfigureAwait(false);
                }
                finally
                {
                    tickCts.Dispose();
                }

                if (completed == pendingKeyTask)
                {
                    var command = await pendingKeyTask.ConfigureAwait(false);
                    pendingKeyTask = null;

                    if (command.Type == CommandType.TerminalResized)
                    {
                        options = ctx.GetCurrentRenderOptions();
                        continue;
                    }

                    if (command.Type is CommandType.GoBack or CommandType.Quit)
                    {
                        // Don't cancel the analysis — let it continue in the background.
                        // GeneratePodcastAsync will await the pending task to reuse
                        // extracted articles instead of re-extracting from scratch.
                        ctx.Logger.LogInformation(
                            "User skipped analysis UI; extraction continues in background");

                        // Observe exceptions so they don't become unobserved task exceptions
                        _ = analysisTask.ContinueWith(
                            t => ctx.Logger.LogWarning(
                                t.Exception?.InnerException,
                                "Background analysis task faulted: {Message}",
                                t.Exception?.InnerException?.Message),
                            TaskContinuationOptions.OnlyOnFaulted);

                        return null;
                    }
                }
                else if (completed == analysisTask)
                {
                    break;
                }
                else
                {
                    animFrame = (animFrame + 1) % PodcastCommandHandler.AnimationFrames.Length;
                }
            }

            if (analysisTask.IsCompleted)
            {
                try
                {
                    return await analysisTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Cache analysis failed, continuing without it");
                    return null;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Cache analysis screen failed");
            return null;
        }
    }

    /// <summary>
    /// Shows a cache-wait screen while the preload service caches collection articles.
    /// Returns true if the screen was skipped (already complete), false if user waited.
    /// </summary>
    internal static async Task<bool> ShowCacheWaitScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        Domain.Entities.Collections.Collection collection,
        CancellationToken ct)
    {
        const int maxWaitMs = 120_000;
        const int pollIntervalMs = 500;

        // Use preload service's progress as the authoritative "done" signal
        // (it tracks needs-browser URLs internally)
        var preloadProgress = ctx.PreloadService.GetProgress();
        if (preloadProgress.IsComplete)
        {
            return true;
        }

        var animFrame = 0;
        var elapsed = 0;
        Task<NavigationCommand>? pendingKeyTask = null;

        while (!ct.IsCancellationRequested && elapsed < maxWaitMs)
        {
            var progress = CollectionCacheHelper.GetProgress(collection, ctx.PageCache, ctx.PreloadService);

            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);
            PodcastCommandHandler.RenderBox(helpers, p, "Caching Articles", width);
            helpers.WriteLine();

            // Progress summary
            var dots = PodcastCommandHandler.AnimationFrames[animFrame % PodcastCommandHandler.AnimationFrames.Length];
            helpers.WriteLine(
                $"  {p.PrimaryText.AnsiFg}{progress.CachedCount}{Reset}" +
                $"{p.SecondaryText.AnsiFg} of {progress.Total} cached{dots}{Reset}");
            helpers.WriteLine();

            // Per-article status indicators
            var maxTitleLen = Math.Max(10, width - 10);
            foreach (var article in progress.Articles)
            {
                var (indicator, color) = article.State switch
                {
                    ArticleCacheState.Cached => ("✓", p.GetSuccessFg()),
                    ArticleCacheState.Caching => ("⟳", p.SecondaryText),
                    ArticleCacheState.NeedsBrowser => ("▸", p.ErrorFg),
                    _ => ("·", p.SecondaryText)
                };

                var title = RenderHelpers.TruncateText(article.Title, maxTitleLen);
                helpers.WriteLine($"    {color.AnsiFg}{indicator}{Reset} {p.PrimaryText.AnsiFg}{title}{Reset}");
            }

            // Key hints
            helpers.WriteLine();
            helpers.WriteLine(
                $"  {p.GetAccentFg().AnsiFg}Esc{Reset}{p.SecondaryText.AnsiFg}:skip waiting{Reset}");

            helpers.ClearRemainingLines();

            // Check preload service's progress (authoritative for needs-browser tracking)
            preloadProgress = ctx.PreloadService.GetProgress();
            if (preloadProgress.IsComplete)
            {
                break;
            }

            // Poll: wait for key press or tick
            pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(ct);
            using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tickTask = Task.Delay(pollIntervalMs, tickCts.Token);
            var completed = await Task.WhenAny(pendingKeyTask, tickTask).ConfigureAwait(false);
            await tickCts.CancelAsync().ConfigureAwait(false);

            if (completed == pendingKeyTask)
            {
                var command = await pendingKeyTask.ConfigureAwait(false);
                pendingKeyTask = null;

                if (command.Type == CommandType.TerminalResized)
                {
                    options = ctx.GetCurrentRenderOptions();
                    continue;
                }

                if (command.Type is CommandType.GoBack or CommandType.Quit)
                {
                    break;
                }
            }

            animFrame++;
            elapsed += pollIntervalMs;
        }

        return false;
    }

    internal static async Task<bool> ShowConfirmationScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        Domain.Entities.Collections.Collection collection,
        ITtsService ttsService,
        GcsConfiguration gcsConfig,
        IUserSettingsStore settingsStore,
        GcsStorageClient? gcsClient,
        CacheAnalysis? cacheAnalysis,
        string? preflightBucketError,
        string? preflightFeedUrl,
        string? preflightFeedStatusNote,
        CancellationToken ct)
    {
        var isTtsConfigured = ttsService.IsConfigured;
        var isGcsConfigured = !string.IsNullOrWhiteSpace(gcsConfig.BucketName);
        var bucketError = preflightBucketError;
        var feedUrl = preflightFeedUrl;
        var feedStatusNote = preflightFeedStatusNote;
        var keyPath = gcsClient?.GetServiceAccountKeyPath();
        var isKeyConfigured = !string.IsNullOrWhiteSpace(keyPath);
        string? inlineError = null;

        // Resolve Anthropic services for AI Hierarchy status (optional — may not be registered)
        bool isAnthropicConfigured = false;
        int savedConfigCount = 0;
        string anthropicModel = "claude-haiku-4-5-20251001";
        try
        {
            using var aiScope = ctx.ScopeFactory.CreateScope();
            var hierarchyAnalyzer = aiScope.ServiceProvider.GetService<IHierarchyAnalyzer>();
            var hierarchyConfigStore = aiScope.ServiceProvider.GetService<IHierarchyConfigStore>();
            var anthropicConfigOpts = aiScope.ServiceProvider.GetService<IOptions<AnthropicConfiguration>>();
            if (hierarchyAnalyzer != null)
            {
                isAnthropicConfigured = hierarchyAnalyzer.IsConfigured;
            }

            if (hierarchyConfigStore != null)
            {
                savedConfigCount = await hierarchyConfigStore.GetConfigCountAsync().ConfigureAwait(false);
            }

            if (anthropicConfigOpts != null)
            {
                anthropicModel = anthropicConfigOpts.Value.Model;
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Failed to resolve Anthropic services for settings display");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);
            PodcastCommandHandler.RenderBox(helpers, p, "Generate Podcast", width);
            helpers.WriteLine();
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Collection:{Reset}   {p.PrimaryText.AnsiFg}{collection.Name}{Reset}");
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Articles:{Reset}     {p.PrimaryText.AnsiFg}{collection.Items.Count}{Reset}");
            helpers.WriteLine();

            // --- Credentials ---
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Credentials{Reset}");

            var ttsIndicator = isTtsConfigured
                ? $"    {p.PromptFg.AnsiFg}●{Reset} OpenAI TTS API key     {p.PromptFg.AnsiFg}configured{Reset}  {p.SecondaryText.AnsiFg}[a] change{Reset}"
                : $"    {p.ErrorFg.AnsiFg}○{Reset} OpenAI TTS API key     {p.ErrorFg.AnsiFg}not set{Reset}";
            helpers.WriteLine(ttsIndicator);

            if (!isTtsConfigured)
            {
                helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Required — get a key at platform.openai.com/api-keys{Reset}");
            }

            if (gcsClient != null)
            {
                if (isKeyConfigured && keyPath is not null)
                {
                    var displayPath = keyPath.Length > 40 ? "…" + keyPath[^39..] : keyPath;
                    helpers.WriteLine($"    {p.PromptFg.AnsiFg}●{Reset} GCS service account    {p.PromptFg.AnsiFg}{displayPath}{Reset}  {p.SecondaryText.AnsiFg}[k] change{Reset}");
                }
                else
                {
                    helpers.WriteLine($"    {p.SecondaryText.AnsiFg}○{Reset} GCS service account    {p.SecondaryText.AnsiFg}not set{Reset}  {p.PrimaryText.AnsiFg}[k] setup wizard{Reset}");
                    helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Optional — enables RSS feed publishing to Google Cloud Storage{Reset}");
                }
            }

            var gcsIndicator = isGcsConfigured
                ? $"    {p.PromptFg.AnsiFg}●{Reset} GCS bucket             {p.PromptFg.AnsiFg}{gcsConfig.BucketName}{Reset}  {p.SecondaryText.AnsiFg}[:] change{Reset}"
                : $"    {p.SecondaryText.AnsiFg}○{Reset} GCS bucket             {p.SecondaryText.AnsiFg}not set (local-only){Reset}";
            helpers.WriteLine(gcsIndicator);

            if (bucketError != null)
            {
                helpers.WriteLine($"      {p.ErrorFg.AnsiFg}{bucketError}{Reset}");
            }
            else if (!isGcsConfigured)
            {
                helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Optional — enables RSS feed for podcast apps{Reset}");
                helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Format: my-bucket-name (3–63 chars, lowercase, a–z/0–9/hyphens/dots){Reset}");
            }

            // --- AI Hierarchy ---
            helpers.WriteLine();
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}AI Hierarchy{Reset}");

            var aiKeyIndicator = isAnthropicConfigured
                ? $"    {p.PromptFg.AnsiFg}●{Reset} API Key                {p.PromptFg.AnsiFg}configured{Reset}"
                : $"    {p.SecondaryText.AnsiFg}○{Reset} API Key                {p.SecondaryText.AnsiFg}not set{Reset}";
            helpers.WriteLine(aiKeyIndicator);
            helpers.WriteLine($"    {p.SecondaryText.AnsiFg}Model:{Reset}                  {p.PrimaryText.AnsiFg}{anthropicModel}{Reset}");
            helpers.WriteLine($"    {p.SecondaryText.AnsiFg}Saved Configs:{Reset}          {p.PrimaryText.AnsiFg}{savedConfigCount}{Reset}");

            if (inlineError != null)
            {
                helpers.WriteLine();
                helpers.WriteLine($"      {p.ErrorFg.AnsiFg}Error: {inlineError}{Reset}");
                inlineError = null;
            }

            // --- Output ---
            helpers.WriteLine();
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Output{Reset}");
            helpers.WriteLine($"    {p.PrimaryText.AnsiFg}M4B audiobook with chapter markers per article{Reset}");

            if (isGcsConfigured)
            {
                if (feedUrl != null)
                {
                    var maxUrlWidth = Math.Max(20, width - 12);
                    var displayUrl = RenderHelpers.TruncateUrl(feedUrl, maxUrlWidth);
                    helpers.WriteLine(
                        $"    {p.SecondaryText.AnsiFg}Feed:{Reset} " +
                        $"{p.PromptFg.AnsiFg}{displayUrl}{Reset}");
                }
                else
                {
                    helpers.WriteLine(
                        $"    {p.SecondaryText.AnsiFg}Feed: pending verification{Reset}");
                }

                if (feedStatusNote != null)
                {
                    helpers.WriteLine($"    {p.SecondaryText.AnsiFg}{feedStatusNote}{Reset}");
                }
            }
            else
            {
                helpers.WriteLine($"    {p.SecondaryText.AnsiFg}Saved locally — configure GCS bucket for RSS feed{Reset}");
            }

            // --- Cache / Cost ---
            if (cacheAnalysis != null)
            {
                helpers.WriteLine();
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Estimate{Reset}");
                helpers.WriteLine(
                    $"    {p.PromptFg.AnsiFg}{cacheAnalysis.CachedArticles}{Reset}" +
                    $"{p.SecondaryText.AnsiFg} of {cacheAnalysis.TotalArticles} articles have audio generated{Reset}");

                if (cacheAnalysis.UncachedArticles > 0)
                {
                    helpers.WriteLine(
                        $"    {p.SecondaryText.AnsiFg}TTS cost for {cacheAnalysis.UncachedArticles} articles:{Reset} " +
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
                        $"    {p.PromptFg.AnsiFg}All audio already generated — no TTS cost{Reset}");
                }
            }

            // --- Key hints ---
            helpers.WriteLine();

            var hints = new StringBuilder();
            if (!isTtsConfigured)
            {
                hints.Append($"  {p.GetAccentFg().AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:set API key   {Reset}");
            }
            else
            {
                hints.Append($"  {p.GetAccentFg().AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:generate   {Reset}");
                hints.Append($"{p.GetAccentFg().AnsiFg}a{Reset}{p.SecondaryText.AnsiFg}:change API key   {Reset}");
            }

            if (!isGcsConfigured)
            {
                hints.Append($"{p.GetAccentFg().AnsiFg}:{Reset}{p.SecondaryText.AnsiFg}set bucket   {Reset}");
            }
            else
            {
                hints.Append($"{p.GetAccentFg().AnsiFg}:{Reset}{p.SecondaryText.AnsiFg}change bucket   {Reset}");
            }

            if (gcsClient != null)
            {
                hints.Append($"{p.GetAccentFg().AnsiFg}k{Reset}{p.SecondaryText.AnsiFg}:{(isKeyConfigured ? "change" : "set")} key   {Reset}");
            }

            hints.Append($"{p.GetAccentFg().AnsiFg}Esc{Reset}{p.SecondaryText.AnsiFg}:cancel{Reset}");
            helpers.WriteLine(hints.ToString());

            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                return false;
            }

            // [a] re-enter API key when already configured
            if (command.RawKeyChar == 'a' && isTtsConfigured)
            {
                var apiKey = await ctx.InputHandler.PromptForInputAsync(
                    "OpenAI API key (platform.openai.com/api-keys): ", ct, isSecret: true).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    var trimmedKey = apiKey.Trim();
                    ttsService.SetApiKeyOverride(trimmedKey);

                    Console.Write($"\r  {p.SecondaryText.AnsiFg}Verifying API key...{Reset}");
                    var validation = await ttsService.ValidateApiKeyAsync(ct).ConfigureAwait(false);

                    if (validation.IsValid)
                    {
                        Console.Write($"\r  {p.PromptFg.AnsiFg}API key verified     {Reset}");
                        try
                        {
                            settingsStore.Set("OpenAiApiKey", trimmedKey, encrypt: true);
                        }
                        catch (Exception ex)
                        {
                            ctx.Logger.LogWarning(ex, "Failed to persist API key");
                        }

                        await Task.Delay(800, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        Console.Write(
                            $"\r  {p.ErrorFg.AnsiFg}{validation.ErrorMessage ?? "Invalid API key"}{Reset}" +
                            new string(' ', 20));
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        ttsService.SetApiKeyOverride(string.Empty);
                        isTtsConfigured = false;
                    }
                }

                continue;
            }

            // [k] set/change GCS service account key
            if (command.RawKeyChar == 'k' && gcsClient != null)
            {
                if (!isKeyConfigured)
                {
                    // --- Multi-step wizard for first-time setup ---
                    var wizardResult = await PodcastGcsWizard.RunGcsKeyWizardAsync(
                        ctx, options, p, gcsClient, gcsConfig, settingsStore, ct).ConfigureAwait(false);

                    if (wizardResult.KeySaved)
                    {
                        keyPath = gcsClient.GetServiceAccountKeyPath();
                        isKeyConfigured = true;
                    }

                    if (wizardResult.BucketSaved)
                    {
                        isGcsConfigured = true;
                        feedUrl = wizardResult.FeedUrl;
                        feedStatusNote = wizardResult.FeedStatusNote;
                        bucketError = wizardResult.BucketError;
                    }
                }
                else
                {
                    // --- Direct prompt for changing existing key ---
                    var keyInput = await ctx.InputHandler.PromptForInputAsync(
                        "GCS key (file path or paste JSON): ", ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(keyInput))
                    {
                        var (keySaved, keyError) = await PodcastGcsWizard.ValidateAndSaveKeyAsync(
                            keyInput.Trim(), gcsClient).ConfigureAwait(false);

                        if (keySaved)
                        {
                            keyPath = gcsClient.GetServiceAccountKeyPath();
                            isKeyConfigured = true;

                            Console.Write($"\r  {p.PromptFg.AnsiFg}Service account key saved     {Reset}");
                            await Task.Delay(800, ct).ConfigureAwait(false);

                            var changeBucket = gcsConfig.BucketName;
                            if (isGcsConfigured && !string.IsNullOrWhiteSpace(changeBucket))
                            {
                                bucketError = null;
                                var (success, url, feedExisted, error) = await PodcastGcsWizard.ValidateAndBootstrapBucketAsync(
                                    ctx, options, changeBucket, gcsConfig, ct).ConfigureAwait(false);
                                if (success)
                                {
                                    feedUrl = url;
                                    feedStatusNote = feedExisted ? "Existing feed found" : "New feed created";
                                }
                                else if (error != null)
                                {
                                    bucketError = error;
                                }
                            }
                        }
                        else
                        {
                            Console.Write(
                                $"\r  {p.ErrorFg.AnsiFg}{keyError}{Reset}" +
                                new string(' ', 20));
                            await Task.Delay(2000, ct).ConfigureAwait(false);
                        }
                    }
                }

                continue;
            }

            if (command.Type == CommandType.ActivateLink)
            {
                if (!isTtsConfigured)
                {
                    var apiKey = await ctx.InputHandler.PromptForInputAsync(
                        "OpenAI API key (platform.openai.com/api-keys): ", ct, isSecret: true).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        var trimmedKey = apiKey.Trim();
                        ttsService.SetApiKeyOverride(trimmedKey);

                        // Validate with a minimal API call before persisting
                        Console.Write($"\r  {p.SecondaryText.AnsiFg}Verifying API key...{Reset}");
                        var validation = await ttsService.ValidateApiKeyAsync(ct).ConfigureAwait(false);

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

                            await Task.Delay(800, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            Console.Write(
                                $"\r  {p.ErrorFg.AnsiFg}{validation.ErrorMessage ?? "Invalid API key"}{Reset}" +
                                new string(' ', 20));
                            await Task.Delay(2000, ct).ConfigureAwait(false);

                            // Revert — do not persist invalid keys
                            ttsService.SetApiKeyOverride(string.Empty);
                            isTtsConfigured = false;
                        }
                    }

                    continue;
                }

                return true;
            }

            if (command.Type == CommandType.OpenCommandLine)
            {
                bucketError = null;
                var bucketName = await ctx.InputHandler.PromptForInputAsync(
                    "GCS bucket name (e.g. my-podcast-feed, or 'clear' to remove): ", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(bucketName))
                {
                    var trimmed = bucketName.Trim();

                    // Handle clear bucket
                    if (trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase) && isGcsConfigured)
                    {
                        gcsConfig.BucketName = null;
                        isGcsConfigured = false;
                        feedUrl = null;
                        feedStatusNote = null;
                        bucketError = null;
                        try
                        {
                            settingsStore.Remove("GcsBucketName");
                        }
                        catch (Exception ex)
                        {
                            ctx.Logger.LogWarning(ex, "Failed to remove persisted bucket name");
                        }
                    }
                    else if (GcsConfiguration.IsValidBucketName(trimmed))
                    {
                        var (success, url, feedExisted, error) = await PodcastGcsWizard.ValidateAndBootstrapBucketAsync(
                            ctx, options, trimmed, gcsConfig, ct).ConfigureAwait(false);
                        if (success)
                        {
                            gcsConfig.BucketName = trimmed;
                            isGcsConfigured = true;
                            bucketError = null;
                            feedUrl = url;
                            feedStatusNote = feedExisted
                                ? "Existing feed found — new episodes will be appended"
                                : "New feed created";
                            try
                            {
                                settingsStore.Set("GcsBucketName", trimmed);
                            }
                            catch (Exception ex)
                            {
                                ctx.Logger.LogWarning(ex, "Failed to persist bucket name");
                            }
                        }
                        else if (error != null)
                        {
                            bucketError = error;
                        }

                        // else: cancelled (Escape) — no error, just re-render
                    }
                    else
                    {
                        bucketError = $"Invalid: \"{trimmed}\" — must be 3–63 chars, lowercase a–z/0–9/hyphens/dots";
                    }
                }

                continue;
            }

            // Unhandled key — silently ignore and re-render
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Error on podcast confirmation screen (non-fatal)");
                inlineError = ex.Message;
            }
        }

        return false;
    }
}
