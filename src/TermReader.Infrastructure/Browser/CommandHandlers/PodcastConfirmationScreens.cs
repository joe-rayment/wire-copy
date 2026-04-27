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
    private const string Bold = "\x1b[1m";

    /// <summary>
    /// Selectable row identity for the confirmation screen. Used to track which row
    /// is highlighted and which action runs when the user presses Enter.
    /// </summary>
    private enum ConfirmRow
    {
        TtsKey,
        GcsKey,
        GcsBucket,
        Generate,
    }

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

        // Build the list of selectable rows in render order. The TtsKey row is
        // always present; GcsKey is present only when the GCS client is wired up.
        // Default selection: first unconfigured row if any, else Generate.
        ConfirmRow[] BuildRows() => gcsClient != null
            ? [ConfirmRow.TtsKey, ConfirmRow.GcsKey, ConfirmRow.GcsBucket, ConfirmRow.Generate]
            : [ConfirmRow.TtsKey, ConfirmRow.GcsBucket, ConfirmRow.Generate];

        var rows = BuildRows();
        var selectedIndex = Array.FindIndex(rows, r => r switch
        {
            ConfirmRow.TtsKey => !isTtsConfigured,
            ConfirmRow.GcsKey => !isKeyConfigured,
            ConfirmRow.GcsBucket => !isGcsConfigured,
            _ => false,
        });
        if (selectedIndex < 0)
        {
            selectedIndex = Array.IndexOf(rows, ConfirmRow.Generate);
        }

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
            // When selected, the row gets a ▌ accent bar in muted + sel-bg fill.
            void RenderRow(ConfirmRow rowId, string statusIcon, string statusColor, string label, string value, string valueColor, string actionLabel)
            {
                var isSelected = rows[selectedIndex] == rowId;
                var prefix = isSelected
                    ? $"  {p.GetMutedFg().AnsiFg}▌{Reset} "
                    : "    ";

                // Layout: "icon  label                value           [Enter] action"
                // We pad the label column to ~24 cols so values align.
                const int labelCol = 24;
                var paddedLabel = label.Length < labelCol ? label + new string(' ', labelCol - label.Length) : label;

                var actionBtn = $"{p.GetAccentFg().AnsiFg}[Enter]{Reset} {p.PrimaryText.AnsiFg}{actionLabel}{Reset}";

                // Compute body before the action button so we can right-align the button.
                var body = $"{statusColor}{statusIcon}{Reset} {paddedLabel}{valueColor}{value}{Reset}";
                var bodyPlainLen = 1 + 1 + paddedLabel.Length + value.Length; // icon + space + label + value
                var actionPlainLen = "[Enter] ".Length + actionLabel.Length;
                var totalPlainLen = 4 /*prefix*/ + bodyPlainLen + actionPlainLen;
                var pad = Math.Max(2, width - totalPlainLen);

                var line = isSelected
                    ? $"{prefix}{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg} {body} {new string(' ', pad - 2)}{actionBtn} {Reset}"
                    : $"{prefix}{body}{new string(' ', pad)}{actionBtn}";
                helpers.WriteLine(line);
            }

            // OpenAI TTS API key row
            if (isTtsConfigured)
            {
                RenderRow(ConfirmRow.TtsKey, "●", p.PromptFg.AnsiFg, "OpenAI TTS API key", "configured", p.PromptFg.AnsiFg, "Change");
            }
            else
            {
                RenderRow(ConfirmRow.TtsKey, "○", p.ErrorFg.AnsiFg, "OpenAI TTS API key", "not set", p.ErrorFg.AnsiFg, "Set up");
                helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Required — get a key at platform.openai.com/api-keys{Reset}");
            }

            // GCS service account key row
            if (gcsClient != null)
            {
                if (isKeyConfigured && keyPath is not null)
                {
                    var displayPath = keyPath.Length > 30 ? "…" + keyPath[^29..] : keyPath;
                    RenderRow(ConfirmRow.GcsKey, "●", p.PromptFg.AnsiFg, "GCS service account", displayPath, p.PromptFg.AnsiFg, "Change");
                }
                else
                {
                    RenderRow(ConfirmRow.GcsKey, "○", p.SecondaryText.AnsiFg, "GCS service account", "not set", p.SecondaryText.AnsiFg, "Set up");
                    helpers.WriteLine($"      {p.SecondaryText.AnsiFg}Optional — enables RSS feed publishing to Google Cloud Storage{Reset}");
                }
            }

            // GCS bucket row
            if (isGcsConfigured)
            {
                RenderRow(ConfirmRow.GcsBucket, "●", p.PromptFg.AnsiFg, "GCS bucket", gcsConfig.BucketName ?? string.Empty, p.PromptFg.AnsiFg, "Change");
            }
            else
            {
                RenderRow(ConfirmRow.GcsBucket, "○", p.SecondaryText.AnsiFg, "GCS bucket", "not set (local-only)", p.SecondaryText.AnsiFg, "Set up");
            }

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
}
