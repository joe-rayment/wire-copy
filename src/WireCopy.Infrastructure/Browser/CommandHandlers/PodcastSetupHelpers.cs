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
internal static class PodcastSetupHelpers
{
    private const string Reset = PodcastCommandHandler.Reset;

    /// <summary>
    /// OpenAI TTS voice catalogue used by the picker, ordered roughly alphabetically.
    /// OpenAiTtsService.MapVoice forwards the chosen value VERBATIM to the API, so this list
    /// is presentation-only — keep it in sync with the API's supported voices (its 400 error
    /// enumerates them; cedar/marin/verse were added after the original ten).
    /// </summary>
    private static readonly string[] AvailableVoices =
    {
        "alloy",
        "ash",
        "ballad",
        "cedar",
        "coral",
        "echo",
        "fable",
        "marin",
        "nova",
        "onyx",
        "sage",
        "shimmer",
        "verse",
    };

    /// <summary>
    /// OpenAI TTS models. The HD model produces higher quality audio at ~2x cost.
    /// </summary>
    private static readonly string[] AvailableModels =
    {
        "gpt-4o-mini-tts",
        "tts-1",
        "tts-1-hd",
    };

    /// <summary>
    /// workspace-g3uu: cancels a pending <see cref="WireCopy.Application.Interfaces.Browser.IInputHandler.WaitForInputAsync"/>
    /// call and awaits it so the underlying <c>TerminalInputHandler._pendingKeyTask</c>
    /// observes the cancellation before control returns to the caller. Prevents an
    /// orphaned WaitForInputAsync from dequeueing the next screen's keys behind
    /// our back. No-op when <paramref name="pendingKeyTask"/> is null.
    /// </summary>
    internal static async Task DrainPendingKeyAsync(
        Task<NavigationCommand>? pendingKeyTask,
        CancellationTokenSource keyCts)
    {
        ArgumentNullException.ThrowIfNull(keyCts);

        if (pendingKeyTask is null)
        {
            return;
        }

        await keyCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await pendingKeyTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — we just cancelled the source. Swallow.
        }
        catch (Exception)
        {
            // Defensive: any other exception in the orphan is irrelevant to the
            // caller; the screen is exiting anyway. Don't propagate.
        }
    }

    /// <summary>
    /// Strips CSI ANSI escape sequences so callers can compute visible column
    /// positions. Forwarded to <see cref="SettingsRowRenderer.StripAnsi(string)"/>.
    /// Retained as a thin shim because external test helpers reach in by name.
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

        // workspace-g3uu: pendingKeyTask must be cancellable independently of the
        // parent ct so we can ABORT the orphaned WaitForInputAsync when
        // analysisTask wins the WhenAny. Otherwise the orphan stays subscribed to
        // TerminalInputHandler._keyChannel, dequeues the first key the next
        // screen (cost-gate modal) is waiting for, and silently consumes it —
        // the modal then blocks indefinitely on the channel.
        using var keyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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

                pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(keyCts.Token);
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
        finally
        {
            // workspace-g3uu: drain the orphaned WaitForInputAsync on EVERY
            // exit path — happy break, GoBack/Quit return, catch handlers,
            // even cancellation rethrow. `using var keyCts` only releases the
            // CTS resource; it does NOT cancel pending registrations. Without
            // this finally, an exception mid-loop would leak the orphan to
            // the next screen's WaitForInputAsync — the exact bug being
            // fixed. CancellationTokenSource cannot be cancelled after
            // dispose, so we cancel BEFORE the `using` runs Dispose.
            await DrainPendingKeyAsync(pendingKeyTask, keyCts).ConfigureAwait(false);
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

        // workspace-g3uu: see ShowCacheAnalysisScreenAsync for the same pattern.
        // The orphaned WaitForInputAsync would otherwise dequeue the next
        // screen's keys (e.g. the cost-gate modal's Enter/Esc). The drain MUST
        // happen in a `finally` so cancellation / mid-loop exceptions also
        // clean up the orphan — `using var keyCts` doesn't cancel on dispose.
        using var keyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<NavigationCommand>? pendingKeyTask = null;

        try
        {
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
                pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(keyCts.Token);
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
        finally
        {
            // workspace-g3uu: drain orphan on EVERY exit path (loop end,
            // break, cancellation throw) so the next screen's input handler
            // isn't fighting an orphan for the channel read.
            await DrainPendingKeyAsync(pendingKeyTask, keyCts).ConfigureAwait(false);
        }
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
    /// Two-entry narration-engine picker (workspace-2xej.7). Each entry renders
    /// TWO lines — the name + live readiness, then a fixed trade-off note — so the
    /// user can judge both engines before switching. Enter persists TtsEngine and
    /// returns "openai"/"chatterbox"; Esc returns null (no change). Cloned from
    /// RunListPickerAsync's loop/keys rather than reusing it because that picker
    /// has no per-option status line.
    /// </summary>
    internal static async Task<string?> PromptAndPickEngineAsync(
        CommandContext ctx,
        RenderOptions options,
        IUserSettingsStore settingsStore,
        bool currentIsChatterbox,
        string openAiReadiness,
        string chatterboxReadiness,
        CancellationToken ct)
    {
        var entries = new (string Value, string Name, string Readiness, string Note)[]
        {
            ("openai", "OpenAI (cloud)", openAiReadiness,
                "Best quality + tone instructions · a few cents per hour of audio · needs API key"),
            ("chatterbox", "Chatterbox (local)", chatterboxReadiness,
                "Free, private, offline · tone from your voice sample · slower without a GPU"),
        };
        var selectedIndex = currentIsChatterbox ? 1 : 0;
        var currentValue = currentIsChatterbox ? "chatterbox" : "openai";

        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);
            PodcastCommandHandler.RenderBox(helpers, p, "Narration engine — how podcast audio is generated", width);
            helpers.WriteLine();

            for (var i = 0; i < entries.Length; i++)
            {
                var isSelected = i == selectedIndex;
                var marker = isSelected ? "▌" : " ";
                var current = entries[i].Value == currentValue
                    ? $"  {p.SecondaryText.AnsiFg}(current){Reset}"
                    : string.Empty;
                var readiness = RenderHelpers.TruncateText(entries[i].Readiness, Math.Max(10, width - 30));

                if (isSelected)
                {
                    helpers.WriteLine(
                        $"  {p.GetMutedFg().AnsiFg}{marker}{Reset} " +
                        $"{p.SelectedItemBg.AnsiBg}{p.SelectedItemFg.AnsiFg} {i + 1}. {entries[i].Name,-22}{readiness}{current}{Reset}");
                }
                else
                {
                    helpers.WriteLine(
                        $"  {p.GetMutedFg().AnsiFg}{marker}{Reset} " +
                        $"{p.PrimaryText.AnsiFg}{i + 1}. {entries[i].Name,-22}{p.SecondaryText.AnsiFg}{readiness}{Reset}{current}");
                }

                helpers.WriteLine(
                    $"       {p.SecondaryText.AnsiFg}{RenderHelpers.TruncateText(entries[i].Note, Math.Max(10, width - 10))}{Reset}");
                helpers.WriteLine();
            }

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

            if (command.Type is CommandType.MoveDown or CommandType.MoveUp)
            {
                selectedIndex = (selectedIndex + 1) % entries.Length;
                continue;
            }

            if (command.Type == CommandType.ActivateLink)
            {
                var picked = entries[selectedIndex].Value;
                try
                {
                    settingsStore.Set(SettingsCommandHandler.KeyTtsEngine, picked);
                    ctx.NavigationService.SetStatusMessage(
                        picked == "chatterbox" ? "Narration → Chatterbox (local)" : "Narration → OpenAI (cloud)");
                }
                catch (Exception ex)
                {
                    ctx.Logger.LogWarning(ex, "Failed to persist narration engine");
                    ctx.NavigationService.SetStatusMessage("Failed to save narration engine", StatusSeverity.Error);
                    return null;
                }

                return picked;
            }
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
