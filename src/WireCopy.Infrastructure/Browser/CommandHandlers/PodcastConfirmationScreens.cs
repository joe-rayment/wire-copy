// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Audio;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Cache-analysis, cache-wait, and confirmation screens for podcast generation,
/// extracted from PodcastCommandHandler.
/// </summary>
internal static class PodcastConfirmationScreens
{
    private const string Reset = PodcastCommandHandler.Reset;
    private const string Bold = "\x1b[1m";

    /// <summary>
    /// OpenAI TTS voice catalogue used by the picker. Matches the values mapped in
    /// OpenAiTtsService.MapVoice and ordered roughly alphabetically.
    /// </summary>
    private static readonly string[] AvailableVoices =
    {
        "alloy",
        "ash",
        "ballad",
        "coral",
        "echo",
        "fable",
        "onyx",
        "nova",
        "sage",
        "shimmer",
    };

    /// <summary>
    /// OpenAI TTS models. The HD model produces higher quality audio at ~2x cost.
    /// </summary>
    private static readonly string[] AvailableModels =
    {
        "tts-1",
        "tts-1-hd",
    };

    /// <summary>
    /// Selectable row identity for the confirmation screen. Used to track which row
    /// is highlighted and which action runs when the user presses Enter.
    /// </summary>
    private enum ConfirmRow
    {
        TtsKey,
        GcsKey,
        GcsBucket,
        OutputFolder,
        Voice,
        Model,
        Generate,
    }

    /// <summary>
    /// User decision returned by the local-only warning panel.
    /// </summary>
    private enum LocalOnlyDecision
    {
        GenerateLocally,
        SetUpBucket,
        Cancel,
    }

    /// <summary>
    /// Builds one selectable confirmation row. Delegates to the shared
    /// <see cref="SettingsRowRenderer"/> so the Generate Podcast screen and the
    /// unified <c>:config</c> Setup screen share row code (workspace-fn1u).
    /// </summary>
    /// <returns>(mainLine, subLine) — subLine is null when neither warning nor helper text was provided.</returns>
    internal static (string MainLine, string? SubLine) BuildConfirmationRow(
        ThemePalette palette,
        int width,
        bool isSelected,
        bool isWarning,
        string statusIcon,
        string statusColor,
        string label,
        string value,
        string valueColor,
        string actionLabel,
        string? warningText = null,
        string? helperText = null,
        int labelCol = 24) =>
        SettingsRowRenderer.Build(
            palette,
            width,
            isSelected,
            isWarning,
            statusIcon,
            statusColor,
            label,
            value,
            valueColor,
            actionLabel,
            warningText,
            helperText,
            labelCol);

    /// <summary>
    /// Strips CSI ANSI escape sequences so callers can compute visible column
    /// positions. Forwarded to <see cref="SettingsRowRenderer.StripAnsi(string)"/>.
    /// </summary>
    internal static string StripAnsi(string s) => SettingsRowRenderer.StripAnsi(s);

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

        // Resolve the output / voice / model so the user can edit them in place.
        // These rows pull their initial value from IUserSettingsStore (the canonical
        // runtime override) and fall back to the bound configuration when unset.
        string ResolveCurrentOutputFolder() =>
            settingsStore.Get("PodcastOutputFolder")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WireCopy",
                    "output");

        string ResolveCurrentVoice() =>
            settingsStore.Get("OpenAiTtsVoice") ?? GetDefaultVoiceFromOptions(ctx) ?? "nova";

        string ResolveCurrentModel() =>
            settingsStore.Get("OpenAiTtsModel") ?? GetDefaultModelFromOptions(ctx) ?? "tts-1";

        var currentOutputFolder = ResolveCurrentOutputFolder();
        var currentVoice = ResolveCurrentVoice();
        var currentModel = ResolveCurrentModel();

        // Build the list of selectable rows in render order. The TtsKey row is
        // always present; GcsKey is present only when the GCS client is wired up.
        // Output, Voice, and Model rows are always editable so the user can fix
        // every config value directly from this screen (workspace-urko).
        ConfirmRow[] BuildRows() => gcsClient != null
            ? [
                ConfirmRow.TtsKey,
                ConfirmRow.GcsKey,
                ConfirmRow.GcsBucket,
                ConfirmRow.OutputFolder,
                ConfirmRow.Voice,
                ConfirmRow.Model,
                ConfirmRow.Generate,
            ]
            : [
                ConfirmRow.TtsKey,
                ConfirmRow.GcsBucket,
                ConfirmRow.OutputFolder,
                ConfirmRow.Voice,
                ConfirmRow.Model,
                ConfirmRow.Generate,
            ];

        var rows = BuildRows();

        // Always focus the first row on screen entry so the screen reads as a menu.
        // The user can navigate down to any unconfigured row or to the Generate
        // action; previously we'd auto-jump to whichever row was unconfigured (or
        // straight to Generate when everything was configured), which made the
        // focused state ambiguous on entry.
        var selectedIndex = 0;

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

            // Helper: render a selectable row with status icon, label, value, and right-aligned action button.
            // Delegates to BuildConfirmationRow (a pure helper) so the layout logic
            // can be unit-tested in isolation.
            void RenderRow(
                ConfirmRow rowId,
                string statusIcon,
                string statusColor,
                string label,
                string value,
                string valueColor,
                string actionLabel,
                bool isWarning = false,
                string? warningText = null,
                string? helperText = null)
            {
                var isSelected = rows[selectedIndex] == rowId;
                var (mainLine, subLine) = BuildConfirmationRow(
                    p,
                    width,
                    isSelected,
                    isWarning,
                    statusIcon,
                    statusColor,
                    label,
                    value,
                    valueColor,
                    actionLabel,
                    warningText,
                    helperText);
                helpers.WriteLine(mainLine);
                if (subLine != null)
                {
                    helpers.WriteLine(subLine);
                }
            }

            // OpenAI TTS API key row
            //
            // The TtsKey row owns its own error/warning text on a sub-line directly
            // beneath it, rather than emitting a disconnected error at the bottom of
            // the screen. inlineError is set when the user attempts Generate without
            // a TTS key — surface it right where the fix needs to happen.
            if (isTtsConfigured)
            {
                RenderRow(
                    ConfirmRow.TtsKey,
                    "●",
                    p.PromptFg.AnsiFg,
                    "OpenAI TTS API key",
                    "configured",
                    p.PromptFg.AnsiFg,
                    "Change",
                    isWarning: false,
                    warningText: inlineError);
            }
            else
            {
                RenderRow(
                    ConfirmRow.TtsKey,
                    "○",
                    p.GetWarningFg().AnsiFg,
                    "OpenAI TTS API key",
                    "not set",
                    p.GetWarningFg().AnsiFg,
                    "Set up",
                    isWarning: true,
                    warningText: inlineError ?? "Required — get a key at platform.openai.com/api-keys");
            }

            // Clear inlineError now that it's been surfaced under the relevant row.
            inlineError = null;

            // GCS service account key row
            if (gcsClient != null)
            {
                if (isKeyConfigured && keyPath is not null)
                {
                    var displayPath = keyPath.Length > 30 ? "…" + keyPath[^29..] : keyPath;
                    RenderRow(
                        ConfirmRow.GcsKey,
                        "●",
                        p.PromptFg.AnsiFg,
                        "GCS service account",
                        displayPath,
                        p.PromptFg.AnsiFg,
                        "Change");
                }
                else
                {
                    RenderRow(
                        ConfirmRow.GcsKey,
                        "○",
                        p.SecondaryText.AnsiFg,
                        "GCS service account",
                        "not set",
                        p.SecondaryText.AnsiFg,
                        "Set up",
                        helperText: "Optional — enables RSS feed publishing to Google Cloud Storage");
                }
            }

            // GCS bucket row
            //
            // When bucketError is non-null we paint the entire row in warning color
            // so the visual error attaches to the row, not to a stray bottom message.
            if (bucketError != null)
            {
                RenderRow(
                    ConfirmRow.GcsBucket,
                    "○",
                    p.GetWarningFg().AnsiFg,
                    "GCS bucket",
                    isGcsConfigured ? (gcsConfig.BucketName ?? string.Empty) : "not set",
                    p.GetWarningFg().AnsiFg,
                    "Change",
                    isWarning: true,
                    warningText: bucketError);
            }
            else if (isGcsConfigured)
            {
                RenderRow(
                    ConfirmRow.GcsBucket,
                    "●",
                    p.PromptFg.AnsiFg,
                    "GCS bucket",
                    gcsConfig.BucketName ?? string.Empty,
                    p.PromptFg.AnsiFg,
                    "Change");
            }
            else
            {
                RenderRow(
                    ConfirmRow.GcsBucket,
                    "○",
                    p.SecondaryText.AnsiFg,
                    "GCS bucket",
                    "not set (local-only)",
                    p.SecondaryText.AnsiFg,
                    "Set up",
                    helperText: "Optional — enables RSS feed for podcast apps");
            }

            // --- Output / Voice / Model (selectable) ---
            // Each row is editable in place so the user can fix every podcast config
            // value without leaving the confirmation screen (workspace-urko).
            helpers.WriteLine();
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Podcast settings{Reset}");

            var folderDisplay = TruncateMiddle(currentOutputFolder, Math.Max(20, width - 40));
            RenderRow(
                ConfirmRow.OutputFolder,
                "●",
                p.PromptFg.AnsiFg,
                "Output folder",
                folderDisplay,
                p.PromptFg.AnsiFg,
                "Change");

            RenderRow(
                ConfirmRow.Voice,
                "●",
                p.PromptFg.AnsiFg,
                "TTS voice",
                currentVoice,
                p.PromptFg.AnsiFg,
                "Change");

            RenderRow(
                ConfirmRow.Model,
                "●",
                p.PromptFg.AnsiFg,
                "TTS model",
                currentModel,
                p.PromptFg.AnsiFg,
                "Change");

            // --- AI Hierarchy ---
            helpers.WriteLine();
            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}AI Hierarchy{Reset}");

            var aiKeyIndicator = isAnthropicConfigured
                ? $"    {p.PromptFg.AnsiFg}●{Reset} API Key                {p.PromptFg.AnsiFg}configured{Reset}"
                : $"    {p.SecondaryText.AnsiFg}○{Reset} API Key                {p.SecondaryText.AnsiFg}not set{Reset}";
            helpers.WriteLine(aiKeyIndicator);
            helpers.WriteLine($"    {p.SecondaryText.AnsiFg}Model:{Reset}                  {p.PrimaryText.AnsiFg}{anthropicModel}{Reset}");
            helpers.WriteLine($"    {p.SecondaryText.AnsiFg}Saved Configs:{Reset}          {p.PrimaryText.AnsiFg}{savedConfigCount}{Reset}");

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

            // --- Generate button (selectable row) ---
            helpers.WriteLine();
            var generateSelected = rows[selectedIndex] == ConfirmRow.Generate;
            var canGenerate = isTtsConfigured;
            var generateLabel = canGenerate ? "Generate Podcast" : "Generate Podcast (set OpenAI key first)";
            var generateLabelColor = canGenerate ? p.HeaderTitleFg.AnsiFg : p.SecondaryText.AnsiFg;
            var generatePrefix = generateSelected
                ? $"  {p.GetMutedFg().AnsiFg}▌{Reset} "
                : "    ";
            var generateIcon = canGenerate ? "▶▶" : "○";
            var generateHint = canGenerate ? "[Enter]" : "[Esc]";
            var generateAccent = p.GetAccentFg().AnsiFg;
            var generateBodyLen = generateIcon.Length + 1 + generateLabel.Length;
            var generatePad = Math.Max(2, width - 4 - generateBodyLen - generateHint.Length);

            var generateLine = generateSelected
                ? $"{generatePrefix}{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg} {generateIcon} {generateLabel}{new string(' ', generatePad - 2)}{generateAccent}{generateHint}{Reset}{p.SelectedItemBg.AnsiBg} {Reset}"
                : $"{generatePrefix}{generateLabelColor}{Bold}{generateIcon} {generateLabel}{Reset}{new string(' ', generatePad)}{generateAccent}{generateHint}{Reset}";
            helpers.WriteLine(generateLine);

            // --- Bottom hint bar ---
            helpers.WriteLine();
            helpers.WriteLine(
                $"  {p.GetAccentFg().AnsiFg}↑↓{Reset}{p.GetDimFg().AnsiFg}:navigate   " +
                $"{p.GetAccentFg().AnsiFg}Enter{p.GetDimFg().AnsiFg}:select   " +
                $"{p.GetAccentFg().AnsiFg}Esc{p.GetDimFg().AnsiFg}:cancel{Reset}");

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

            // --- Navigation: j/k or arrow keys move selection between rows ---
            if (command.Type == CommandType.MoveDown)
            {
                rows = BuildRows();
                selectedIndex = (selectedIndex + 1) % rows.Length;
                continue;
            }

            if (command.Type == CommandType.MoveUp)
            {
                rows = BuildRows();
                selectedIndex = (selectedIndex - 1 + rows.Length) % rows.Length;
                continue;
            }

            // --- Enter: invoke action for the selected row ---
            if (command.Type == CommandType.ActivateLink)
            {
                rows = BuildRows();
                var current = rows[selectedIndex];

                if (current == ConfirmRow.Generate)
                {
                    if (!isTtsConfigured)
                    {
                        // Bounce back: jump to the TTS row so the user can fix it.
                        selectedIndex = Array.IndexOf(rows, ConfirmRow.TtsKey);
                        inlineError = "Set the OpenAI TTS API key first";
                        continue;
                    }

                    // No GCS bucket → warn that this run is local-only and surface
                    // the absolute output path before the user commits to TTS spend.
                    if (!isGcsConfigured)
                    {
                        var decision = await ShowLocalOnlyWarningAsync(
                            ctx, options, collection.Name, ct).ConfigureAwait(false);
                        if (decision == LocalOnlyDecision.Cancel)
                        {
                            continue;
                        }

                        if (decision == LocalOnlyDecision.SetUpBucket)
                        {
                            rows = BuildRows();
                            selectedIndex = Array.IndexOf(rows, ConfirmRow.GcsBucket);
                            continue;
                        }

                        // GenerateLocally falls through to return true
                    }

                    return true;
                }

                if (current == ConfirmRow.TtsKey)
                {
                    var (ok, newConfigured) = await PromptAndSetOpenAiKeyAsync(
                        ctx, p, ttsService, settingsStore, isTtsConfigured, ct).ConfigureAwait(false);
                    if (ok)
                    {
                        isTtsConfigured = newConfigured;
                    }

                    continue;
                }

                if (current == ConfirmRow.GcsKey && gcsClient != null)
                {
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

                    continue;
                }

                if (current == ConfirmRow.GcsBucket)
                {
                    bucketError = await PromptAndSetBucketAsync(
                        ctx,
                        options,
                        p,
                        gcsConfig,
                        settingsStore,
                        x => isGcsConfigured = x,
                        x => feedUrl = x,
                        x => feedStatusNote = x,
                        ct).ConfigureAwait(false);
                    continue;
                }

                if (current == ConfirmRow.OutputFolder)
                {
                    var newFolder = await PromptAndSetOutputFolderAsync(
                        ctx, settingsStore, currentOutputFolder, ct).ConfigureAwait(false);
                    if (newFolder != null)
                    {
                        currentOutputFolder = newFolder;
                    }

                    continue;
                }

                if (current == ConfirmRow.Voice)
                {
                    var newVoice = await PromptAndPickVoiceAsync(
                        ctx, options, settingsStore, currentVoice, ct).ConfigureAwait(false);
                    if (newVoice != null)
                    {
                        currentVoice = newVoice;
                    }

                    continue;
                }

                if (current == ConfirmRow.Model)
                {
                    var newModel = await PromptAndPickModelAsync(
                        ctx, options, settingsStore, currentModel, ct).ConfigureAwait(false);
                    if (newModel != null)
                    {
                        currentModel = newModel;
                    }

                    continue;
                }

                continue;
            }

            // --- Power-user accelerators (still work, but actions are now visible per row) ---

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

            // [k] set/change GCS service account key — always run the wizard so
            // the user gets the same paste-JSON-or-file-path UX for set and change.
            if (command.RawKeyChar == 'k' && gcsClient != null)
            {
                var wizardResult = await PodcastGcsWizard.RunGcsKeyWizardAsync(
                    ctx, options, p, gcsClient, gcsConfig, settingsStore, ct).ConfigureAwait(false);

                if (wizardResult.KeySaved)
                {
                    keyPath = gcsClient.GetServiceAccountKeyPath();
                    isKeyConfigured = true;

                    // Re-validate the bucket against the new key if a bucket is
                    // already configured but no fresh wizard bucket update came back.
                    var changeBucket = gcsConfig.BucketName;
                    if (!wizardResult.BucketSaved && isGcsConfigured && !string.IsNullOrWhiteSpace(changeBucket))
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

                if (wizardResult.BucketSaved)
                {
                    isGcsConfigured = true;
                    feedUrl = wizardResult.FeedUrl;
                    feedStatusNote = wizardResult.FeedStatusNote;
                    bucketError = wizardResult.BucketError;
                }

                continue;
            }

            // [: ] OpenCommandLine accelerator → bucket prompt
            if (command.Type == CommandType.OpenCommandLine)
            {
                bucketError = await PromptAndSetBucketAsync(
                    ctx,
                    options,
                    p,
                    gcsConfig,
                    settingsStore,
                    x => isGcsConfigured = x,
                    x => feedUrl = x,
                    x => feedStatusNote = x,
                    ct).ConfigureAwait(false);
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

    /// <summary>
    /// Truncates the middle of a long path with an ellipsis so both ends stay visible.
    /// Used for output paths the user wants to copy/paste into Finder.
    /// </summary>
    internal static string TruncateMiddle(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxWidth || maxWidth <= 1)
        {
            return text;
        }

        if (maxWidth <= 3)
        {
            return new string('.', maxWidth);
        }

        var keep = maxWidth - 1; // 1 char for the ellipsis
        var leftKeep = (keep + 1) / 2;
        var rightKeep = keep - leftKeep;
        return text[..leftKeep] + "…" + text[^rightKeep..];
    }

    /// <summary>
    /// Prompts the user for an OpenAI API key, validates it against the API,
    /// and persists it. Returns (handled, isConfigured) so the caller can update
    /// its loop state. Showing nothing on a blank submission is intentional —
    /// the user can press Esc instead.
    /// </summary>
    private static async Task<(bool Handled, bool IsConfigured)> PromptAndSetOpenAiKeyAsync(
        CommandContext ctx,
        ThemePalette p,
        ITtsService ttsService,
        IUserSettingsStore settingsStore,
        bool currentlyConfigured,
        CancellationToken ct)
    {
        var apiKey = await ctx.InputHandler.PromptForInputAsync(
            "OpenAI API key (platform.openai.com/api-keys): ", ct, isSecret: true).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, currentlyConfigured);
        }

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
            return (true, true);
        }

        Console.Write(
            $"\r  {p.ErrorFg.AnsiFg}{validation.ErrorMessage ?? "Invalid API key"}{Reset}" +
            new string(' ', 20));
        await Task.Delay(2000, ct).ConfigureAwait(false);
        ttsService.SetApiKeyOverride(string.Empty);
        return (true, false);
    }

    /// <summary>
    /// Prompts for a GCS bucket name, validates it against the cloud, and updates
    /// the configuration. Returns the error message to display (or null on success/skip).
    /// </summary>
    private static async Task<string?> PromptAndSetBucketAsync(
        CommandContext ctx,
        RenderOptions options,
        ThemePalette p,
        GcsConfiguration gcsConfig,
        IUserSettingsStore settingsStore,
        Action<bool> setIsGcsConfigured,
        Action<string?> setFeedUrl,
        Action<string?> setFeedStatusNote,
        CancellationToken ct)
    {
        _ = p; // reserved for future inline status rendering
        var bucketName = await ctx.InputHandler.PromptForInputAsync(
            "GCS bucket name (e.g. my-podcast-feed, or 'clear' to remove): ", ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(bucketName))
        {
            return null;
        }

        var trimmed = bucketName.Trim();

        // Handle clear bucket
        if (trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            gcsConfig.BucketName = null;
            setIsGcsConfigured(false);
            setFeedUrl(null);
            setFeedStatusNote(null);
            try
            {
                settingsStore.Remove("GcsBucketName");
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to remove persisted bucket name");
            }

            return null;
        }

        if (!GcsConfiguration.IsValidBucketName(trimmed))
        {
            return $"Invalid: \"{trimmed}\" — must be 3–63 chars, lowercase a–z/0–9/hyphens/dots";
        }

        var (success, url, feedExisted, error) = await PodcastGcsWizard.ValidateAndBootstrapBucketAsync(
            ctx, options, trimmed, gcsConfig, ct).ConfigureAwait(false);

        if (success)
        {
            gcsConfig.BucketName = trimmed;
            setIsGcsConfigured(true);
            setFeedUrl(url);
            setFeedStatusNote(feedExisted
                ? "Existing feed found — new episodes will be appended"
                : "New feed created");
            try
            {
                settingsStore.Set("GcsBucketName", trimmed);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to persist bucket name");
            }

            return null;
        }

        return error;
    }

    /// <summary>
    /// Renders an amber-bordered warning panel telling the user that without a GCS
    /// bucket the podcast will be saved locally only (no RSS feed). Lets the user
    /// choose between generating locally, switching to the bucket setup row, or
    /// cancelling outright.
    /// </summary>
    private static async Task<LocalOnlyDecision> ShowLocalOnlyWarningAsync(
        CommandContext ctx,
        RenderOptions options,
        string collectionName,
        CancellationToken ct)
    {
        // Resolve the absolute output path so the user knows exactly where the
        // M4B will land before they spend money on TTS.
        string outputPath;
        try
        {
            using var pathScope = ctx.ScopeFactory.CreateScope();
            var orchestrator = pathScope.ServiceProvider.GetRequiredService<IPodcastOrchestrator>();
            outputPath = orchestrator.GetOutputFilePath(collectionName);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to resolve output path for warning panel");
            outputPath = "(output path unavailable)";
        }

        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var totalWidth = Math.Max(40, options.TerminalWidth);
            var boxWidth = Math.Min(totalWidth - 4, 78);
            var leftPad = Math.Max(0, (totalWidth - boxWidth) / 2);
            var pad = new string(' ', leftPad);
            var inner = boxWidth - 2;
            var warnFg = p.GetWarningFg().AnsiFg;

            // Vertical centering — push the box down a bit so it doesn't sit at the top.
            var topMargin = Math.Max(1, (options.TerminalHeight - 14) / 2);
            for (var i = 0; i < topMargin; i++)
            {
                helpers.WriteLine();
            }

            string Border(string mid) => $"{pad}{warnFg}{mid}{Reset}";
            string BodyLine(string content)
            {
                var truncated = RenderHelpers.TruncateText(content, inner - 2);
                var visible = truncated.PadRight(inner - 2);
                return $"{pad}{warnFg}│{Reset} {p.PrimaryText.AnsiFg}{visible}{Reset} {warnFg}│{Reset}";
            }

            string MutedLine(string content)
            {
                var truncated = RenderHelpers.TruncateText(content, inner - 2);
                var visible = truncated.PadRight(inner - 2);
                return $"{pad}{warnFg}│{Reset} {p.SecondaryText.AnsiFg}{visible}{Reset} {warnFg}│{Reset}";
            }

            string EmptyLine() => $"{pad}{warnFg}│{Reset} {new string(' ', inner - 2)} {warnFg}│{Reset}";

            string KeyLine(string key, string text)
            {
                var combined = $"{key} {text}";
                var padCount = Math.Max(0, inner - 2 - combined.Length);
                return $"{pad}{warnFg}│{Reset} {p.GetAccentFg().AnsiFg}{key}{Reset} {p.PrimaryText.AnsiFg}{text}{Reset}{new string(' ', padCount)} {warnFg}│{Reset}";
            }

            helpers.WriteLine(Border("╭" + new string('─', inner) + "╮"));

            // Heading line (manually padded since icon counts as 1 visible char)
            const string heading = "⚠  No GCS bucket configured";
            var headingPad = Math.Max(0, inner - 2 - heading.Length);
            helpers.WriteLine($"{pad}{warnFg}│{Reset} {Bold}{warnFg}{heading}{Reset}{new string(' ', headingPad)} {warnFg}│{Reset}");

            helpers.WriteLine(EmptyLine());
            helpers.WriteLine(BodyLine("The podcast file will be saved locally only — it"));
            helpers.WriteLine(BodyLine("will not be published as an RSS feed for podcast"));
            helpers.WriteLine(BodyLine("apps to subscribe to."));
            helpers.WriteLine(EmptyLine());
            helpers.WriteLine(MutedLine("The file will be at:"));
            helpers.WriteLine(BodyLine(TruncateMiddle(outputPath, inner - 2)));
            helpers.WriteLine(EmptyLine());
            helpers.WriteLine(KeyLine("[Enter]", "generate locally"));
            helpers.WriteLine(KeyLine("[b]    ", "set up bucket first"));
            helpers.WriteLine(KeyLine("[Esc]  ", "cancel"));
            helpers.WriteLine(Border("╰" + new string('─', inner) + "╯"));
            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                return LocalOnlyDecision.Cancel;
            }

            if (command.Type == CommandType.ActivateLink)
            {
                return LocalOnlyDecision.GenerateLocally;
            }

            if (command.RawKeyChar is 'b' or 'B')
            {
                return LocalOnlyDecision.SetUpBucket;
            }

            // Unhandled key — re-render and wait
        }

        return LocalOnlyDecision.Cancel;
    }

    /// <summary>
    /// Resolves the default voice from <see cref="OpenAiTtsConfiguration"/>.
    /// Returns null when the DI scope can't yield options (test harness).
    /// </summary>
    private static string? GetDefaultVoiceFromOptions(CommandContext ctx)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var opts = scope.ServiceProvider.GetService<IOptions<OpenAiTtsConfiguration>>();
            return opts?.Value.Voice;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Failed to resolve default voice");
            return null;
        }
    }

    private static string? GetDefaultModelFromOptions(CommandContext ctx)
    {
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var opts = scope.ServiceProvider.GetService<IOptions<OpenAiTtsConfiguration>>();
            return opts?.Value.Model;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Failed to resolve default model");
            return null;
        }
    }

#pragma warning disable SA1202 // internal row-dispatch helpers grouped with their related private impls
    /// <summary>
    /// Prompts for an absolute output folder path. Persists the trimmed value via
    /// <see cref="IUserSettingsStore"/>. Returns the new path on success, or null
    /// when the user cancels (Esc / blank input).
    ///
    /// Shared entry point used by both the Generate Podcast confirmation screen
    /// and the unified <c>:config</c> Setup screen (workspace-fn1u).
    ///
    /// The path is not validated for existence — <see cref="PodcastOrchestrator.GetOutputFilePath"/>
    /// creates the directory on demand. ~ is expanded by the orchestrator at runtime.
    /// </summary>
    internal static async Task<string?> PromptAndSetOutputFolderAsync(
        CommandContext ctx,
        IUserSettingsStore settingsStore,
        string currentValue,
        CancellationToken ct)
    {
        var input = await ctx.InputHandler.PromptForInputAsync(
            $"Output folder [{currentValue}] (empty to keep, 'reset' to revert to default): ",
            ct,
            isSecret: false,
            initialInput: currentValue).ConfigureAwait(false);

        if (input == null || string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();

        if (trimmed.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                settingsStore.Remove("PodcastOutputFolder");
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Failed to clear output folder override");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WireCopy",
                "output");
        }

        try
        {
            settingsStore.Set("PodcastOutputFolder", trimmed);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist output folder");
        }

        return trimmed;
    }

    /// <summary>
    /// Renders a numbered list picker for the given values and returns the user's
    /// choice. Esc returns null (no change). Numeric keys 1..N pick a value.
    /// </summary>
    private static async Task<string?> RunListPickerAsync(
        CommandContext ctx,
        RenderOptions options,
        string title,
        IReadOnlyList<string> values,
        string currentValue,
        CancellationToken ct)
    {
        var selectedIndex = Math.Max(0, values.ToList().FindIndex(v =>
            string.Equals(v, currentValue, StringComparison.OrdinalIgnoreCase)));

        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);
            PodcastCommandHandler.RenderBox(helpers, p, title, width);
            helpers.WriteLine();

            for (var i = 0; i < values.Count; i++)
            {
                var isSelected = i == selectedIndex;
                var marker = isSelected ? "▌" : " ";
                var indicatorColor = p.GetMutedFg().AnsiFg;
                var current = string.Equals(values[i], currentValue, StringComparison.OrdinalIgnoreCase)
                    ? $"  {p.SecondaryText.AnsiFg}(current){Reset}"
                    : string.Empty;

                if (isSelected)
                {
                    helpers.WriteLine(
                        $"  {indicatorColor}{marker}{Reset} " +
                        $"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg} {i + 1,2}. {values[i]}{current}{Reset}");
                }
                else
                {
                    helpers.WriteLine(
                        $"  {indicatorColor}{marker}{Reset} " +
                        $"{p.PrimaryText.AnsiFg}{i + 1,2}. {values[i]}{Reset}{current}");
                }
            }

            helpers.WriteLine();
            helpers.WriteLine(
                $"  {p.GetAccentFg().AnsiFg}↑↓{Reset}{p.GetDimFg().AnsiFg}:navigate   " +
                $"{p.GetAccentFg().AnsiFg}Enter{Reset}{p.GetDimFg().AnsiFg}:select   " +
                $"{p.GetAccentFg().AnsiFg}Esc{Reset}{p.GetDimFg().AnsiFg}:cancel{Reset}");

            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                return null;
            }

            if (command.Type == CommandType.MoveDown)
            {
                selectedIndex = (selectedIndex + 1) % values.Count;
                continue;
            }

            if (command.Type == CommandType.MoveUp)
            {
                selectedIndex = (selectedIndex - 1 + values.Count) % values.Count;
                continue;
            }

            if (command.Type == CommandType.ActivateLink)
            {
                return values[selectedIndex];
            }

            // Numeric shortcuts (1..9) are intentionally NOT bound: the terminal
            // input handler accumulates digits as motion-count prefixes
            // (10j, 5G, etc.), so they never surface as RawKeyChar. Up/Down +
            // Enter is the only reliable selection path.
        }

        return null;
    }

    /// <summary>
    /// Picks a TTS voice from <see cref="AvailableVoices"/> and persists the
    /// selection via <see cref="IUserSettingsStore"/>. Returns the new value or null
    /// when the user cancels.
    /// </summary>
    internal static async Task<string?> PromptAndPickVoiceAsync(
        CommandContext ctx,
        RenderOptions options,
        IUserSettingsStore settingsStore,
        string currentValue,
        CancellationToken ct)
    {
        var picked = await RunListPickerAsync(
            ctx, options, "Select TTS voice", AvailableVoices, currentValue, ct).ConfigureAwait(false);

        if (picked == null)
        {
            return null;
        }

        try
        {
            settingsStore.Set("OpenAiTtsVoice", picked);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist TTS voice");
        }

        return picked;
    }

    /// <summary>
    /// Picks a TTS model and persists the selection via <see cref="IUserSettingsStore"/>.
    /// </summary>
    internal static async Task<string?> PromptAndPickModelAsync(
        CommandContext ctx,
        RenderOptions options,
        IUserSettingsStore settingsStore,
        string currentValue,
        CancellationToken ct)
    {
        var picked = await RunListPickerAsync(
            ctx, options, "Select TTS model", AvailableModels, currentValue, ct).ConfigureAwait(false);

        if (picked == null)
        {
            return null;
        }

        try
        {
            settingsStore.Set("OpenAiTtsModel", picked);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to persist TTS model");
        }

        return picked;
    }
#pragma warning restore SA1202
}
