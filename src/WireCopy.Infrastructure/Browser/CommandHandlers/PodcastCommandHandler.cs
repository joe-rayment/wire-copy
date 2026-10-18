// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Animations;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles podcast generation: confirmation, progress, and completion screens.
/// </summary>
internal static class PodcastCommandHandler
{
    internal const string Reset = "\x1b[0m";
    internal const int AnimationIntervalMs = 500;
    internal const int SpinnerIntervalMs = 250;
    internal static readonly string[] AnimationFrames = [".", "..", "..."];
    internal static readonly string[] SpinnerFrames = ["\u25d0", "\u25d3", "\u25d1", "\u25d2"];

    internal enum ArticleState
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Cached,
    }

    /// <summary>
    /// workspace-vkhr Phase D: restores the in-progress podcast modal when a
    /// background job is running and the user has detached it by backing out
    /// (Esc/b — workspace-m8es.2) or via ":podcast" while a run is in flight.
    /// Subscribes to the live progress
    /// stream from the manager, awaits the same generation task, and runs
    /// the same result/error/cancel completion flow as
    /// <see cref="HandleGeneratePodcast"/>. When no active job exists, the
    /// handler shows a brief status message and returns.
    /// </summary>
    public static async Task HandleRestorePodcastModal(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        IPodcastBackgroundJobManager? jobManager;
        using (var scope = ctx.ScopeFactory.CreateScope())
        {
            jobManager = scope.ServiceProvider.GetService<IPodcastBackgroundJobManager>();
        }

        if (jobManager is null || !jobManager.HasActiveJob)
        {
            ctx.NavigationService.SetStatusMessage("No active podcast generation.");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var result = await PodcastProgressScreens.ShowProgressScreenAttachedAsync(
            ctx, options, jobManager, ct).ConfigureAwait(false);

        if (result == null)
        {
            // Either user re-detached (status message already set), the run
            // was cancelled, or generation faulted into OCE — in all three
            // cases the next render frame is sufficient feedback.
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        if (!result.Success)
        {
            await PodcastProgressScreens.ShowErrorScreenAsync(
                ctx,
                options,
                result.ErrorMessage ?? "Unknown error",
                result.FailedArticleDetails,
                result.FailureDetail,
                ct).ConfigureAwait(false);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        await PodcastProgressScreens.ShowCompletionScreenAsync(ctx, options, result, ct)
            .ConfigureAwait(false);
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Legacy entry point for the ":settings" command — now an alias for the
    /// unified Setup screen (<see cref="SettingsCommandHandler.HandleConfigScreen"/>).
    /// workspace-ny44 (Phase 6 of workspace-mhwa) retired the old podcast
    /// confirmation/settings screen in favour of a single canonical place to
    /// edit every credential + preference.
    /// </summary>
    public static Task HandlePodcastSettings(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct) =>
        SettingsCommandHandler.HandleConfigScreen(ctx, options, ct);

    public static async Task HandleGeneratePodcast(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        // workspace-n49i: pressing 'r' on the completion screen retries the
        // run from scratch. We loop the entire pre-flight + generation flow
        // so the retry sees fresh cache analysis, fresh pre-flight checks,
        // and a clean progress run. Capped at 3 iterations to prevent
        // accidental infinite loops if the failure mode is sticky.
        const int MaxRetries = 3;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var retry = await RunGeneratePodcastAttempt(ctx, options, ct).ConfigureAwait(false);
            if (!retry)
            {
                return;
            }

            // Refresh render options between retries — terminal may have
            // resized while the user sat on the completion screen.
            options = ctx.GetCurrentRenderOptions();
        }
    }

    internal static async Task<bool> RunGeneratePodcastAttempt(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        try
        {
            if (ctx.NavigationService.CurrentContext.ViewMode != ViewMode.CollectionItems)
            {
                ctx.NavigationService.SetStatusMessage("Open a collection first, then press p");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return false;
            }

            var collection = ctx.NavigationService.ActiveCollection;
            if (collection == null || collection.Items.Count == 0)
            {
                ctx.NavigationService.SetStatusMessage("No articles to generate podcast from");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return false;
            }

            // Press feedback: render one frame with inverted button colors, then continue
            var pressedOptions = options with { PodcastButtonState = 1 }; // 1 = Pressed
            await ctx.RenderCurrentPageAsync(pressedOptions, ct).ConfigureAwait(false);
            await Task.Delay(100, ct).ConfigureAwait(false);

            using var scope = ctx.ScopeFactory.CreateScope();
            var narrator = scope.ServiceProvider.GetRequiredService<IPodcastNarrator>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IPodcastOrchestrator>();
            var gcsConfig = scope.ServiceProvider
                .GetRequiredService<IOptions<GcsConfiguration>>().Value;
            var settingsStore = scope.ServiceProvider.GetRequiredService<IUserSettingsStore>();

            if (string.IsNullOrWhiteSpace(gcsConfig.BucketName))
            {
                var savedBucket = settingsStore.Get("GcsBucketName");
                if (!string.IsNullOrWhiteSpace(savedBucket) && GcsConfiguration.IsValidBucketName(savedBucket))
                {
                    gcsConfig.BucketName = savedBucket;
                }
            }

            // workspace-c3ec4.4: narration runs in audio-engine, which owns the TTS keys. Ask it
            // whether it is up, accepts our key and has the selected engine configured BEFORE the
            // slow cache analysis. A not-ready status carries a user-ready message that starts with
            // "audio-engine" — the failure classifier turns it into a remediation screen. The check
            // gets 5 s so an unreachable address never hangs the keypress.
            var status = await CheckNarratorAsync(narrator, ct).ConfigureAwait(false);
            if (!status.Ready)
            {
                await PodcastProgressScreens.ShowErrorScreenAsync(
                    ctx,
                    options,
                    string.IsNullOrWhiteSpace(status.Message) ? "audio-engine isn't ready." : status.Message,
                    [],
                    ct).ConfigureAwait(false);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return false;
            }

            var engineIsLocal = string.Equals(narrator.EngineName, "chatterbox", StringComparison.OrdinalIgnoreCase);

            // Pre-flight GCS validation: check credentials BEFORE slow cache
            // analysis. workspace-ny44 (Phase 6) retired the confirmation
            // screen, so the success/error tuple from this probe is now
            // either consumed by the auto-remediation flow (workspace-p1px,
            // which lives in PodcastPublisher) or surfaced via the typed
            // failure pipeline in the result screen — we no longer need to
            // pass it forward as banner copy.
            var preflightBucketName = gcsConfig.BucketName;
            if (!string.IsNullOrWhiteSpace(preflightBucketName))
            {
                await PodcastGcsWizard.ValidateAndBootstrapBucketAsync(
                    ctx, options, preflightBucketName, gcsConfig, ct).ConfigureAwait(false);
            }

            // workspace-reym: deleted the standalone "Analyzing Articles"
            // and "Caching Articles" screens — they were the second-page the
            // user kept landing on after pressing `p`. Cache analysis still
            // runs (orchestrator.GeneratePodcastAsync extracts inline and
            // emits CachingContent progress events), but those events now
            // render inside the single Progress screen instead of a
            // separate full-screen takeover.
            //
            // The cost-gate also no longer requires its own pre-flight
            // screen. We only run analysis up-front when the cost-gate
            // could *plausibly* fire (article count over threshold or the
            // user explicitly opted into AlwaysShow); for the typical
            // Reading List under the (raised) thresholds, we skip straight
            // to the Progress screen and the user sees ONE screen titled
            // "Generating podcast" from press to result.
            var costGateConfig = LoadCostGateConfig(settingsStore);
            var needsAnalysisForGate = costGateConfig.AlwaysShow
                || collection.Items.Count > costGateConfig.ArticleThreshold;

            if (needsAnalysisForGate)
            {
                // workspace-reym: surface immediate feedback while the silent
                // analysis runs — otherwise the user sees the Reading List
                // unchanged for 10-30s after pressing `p` and re-encounters
                // the original "nothing's happening" complaint.
                //
                // We render an inline 3-row indicator at the bottom of the
                // view (same position the cost-gate modal lands on) rather
                // than using NavigationService.SetStatusMessage, because the
                // CollectionItems render path constructs a fresh empty
                // NavigationContext and silently drops the status message —
                // a pre-existing gap separate from this fix.
                var totalArticles = collection.Items.Count;
                var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                PodcastAnalyzingIndicator.Render(options, palette, completed: 0, total: totalArticles);

                var lastReportedCount = 0;
                var analysisProgress = new Progress<ContentExtractionProgress>(p =>
                {
                    if (!p.IsCompleted)
                    {
                        return;
                    }

                    var done = Interlocked.Increment(ref lastReportedCount);
                    PodcastAnalyzingIndicator.Render(options, palette, done, totalArticles);
                });

                CacheAnalysis analysis;
                try
                {
                    analysis = await orchestrator
                        .AnalyzeCacheStatusAsync(collection, analysisProgress, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    PodcastAnalyzingIndicator.Clear(options);
                }

                if (costGateConfig.ShouldShowGate(analysis))
                {
                    var proceed = await PodcastCostGateModal
                        .ShowAsync(ctx, options, analysis, costGateConfig, ct, engineIsLocal: engineIsLocal)
                        .ConfigureAwait(false);
                    if (!proceed)
                    {
                        ctx.NavigationService.SetStatusMessage("Podcast cancelled");
                        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                        return false;
                    }

                    options = ctx.GetCurrentRenderOptions();
                }
            }

            var result = await PodcastProgressScreens.ShowProgressScreenAsync(ctx, options, collection, orchestrator, ct).ConfigureAwait(false);

            if (result == null)
            {
                // workspace-6dzj: ShowProgressScreenAsync now sets a richer
                // "Cancelled — N articles completed" status message before
                // returning null, so we leave that intact rather than
                // overwriting with the bare "Podcast generation cancelled".
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return false;
            }

            if (!result.Success)
            {
                // workspace-c3ec4.4: narration keys live in audio-engine, so a failed run never
                // clears a saved key here any more — the classifier shows what to fix instead.
                var error = result.ErrorMessage ?? "Unknown error";
                var retry = await PodcastProgressScreens.ShowErrorScreenAsync(
                    ctx, options, error, result.FailedArticleDetails, result.FailureDetail, ct)
                    .ConfigureAwait(false);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return retry;
            }

            var action = await PodcastProgressScreens.ShowCompletionScreenAsync(ctx, options, result, ct).ConfigureAwait(false);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return action == CompletionScreenAction.Retry;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Podcast generation failed with unexpected error");
            try
            {
                ctx.NavigationService.SetStatusMessage($"Podcast error: {ex.Message}", StatusSeverity.Error);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            }
            catch (Exception renderEx)
            {
                // Last-resort: if even the error UI fails, just log it
                ctx.Logger.LogError(renderEx, "Failed to render podcast error screen");
            }

            return false;
        }
    }

    /// <summary>
    /// Runs <see cref="IPodcastNarrator.CheckAsync"/> with a 5 s budget so an unreachable audio-engine
    /// never blocks the keypress. A timeout (as opposed to the user cancelling) becomes a not-ready
    /// status whose message the failure classifier routes to the "start audio-engine" remediation.
    /// </summary>
    /// <param name="narrator">The narrator to check.</param>
    /// <param name="ct">The user's cancellation token.</param>
    /// <returns>The narrator's status, or a synthetic not-ready status on timeout.</returns>
    internal static async Task<NarratorStatus> CheckNarratorAsync(IPodcastNarrator narrator, CancellationToken ct)
    {
        try
        {
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(TimeSpan.FromSeconds(5));
            return await narrator.CheckAsync(checkCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new NarratorStatus
            {
                Ready = false,
                ErrorKind = NarrationErrorKind.Unreachable,
                Message = "audio-engine isn't running or isn't answering (no reply within 5 seconds). "
                    + "Start it (audio-engine README: Run natively) or change its address in Settings.",
            };
        }
    }

    /// <summary>
    /// Reads cost-gate thresholds from the user-settings store, falling back
    /// to the <see cref="PodcastCostGateConfig"/> defaults (workspace-lr80).
    /// Keys: <c>PodcastCostGateThresholdUsd</c>, <c>PodcastCostGateArticleThreshold</c>,
    /// <c>PodcastCostGateAlwaysShow</c>.
    /// </summary>
    internal static PodcastCostGateConfig LoadCostGateConfig(IUserSettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);

        var defaults = new PodcastCostGateConfig();
        var thresholdRaw = settingsStore.Get("PodcastCostGateThresholdUsd");
        var articleRaw = settingsStore.Get("PodcastCostGateArticleThreshold");
        var alwaysRaw = settingsStore.Get("PodcastCostGateAlwaysShow");

        var thresholdUsd = decimal.TryParse(thresholdRaw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var t) && t >= 0
            ? t
            : defaults.ThresholdUsd;
        var articleThreshold = int.TryParse(articleRaw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var a) && a > 0
            ? a
            : defaults.ArticleThreshold;
        var alwaysShow = bool.TryParse(alwaysRaw, out var b) && b;

        return new PodcastCostGateConfig
        {
            ThresholdUsd = thresholdUsd,
            ArticleThreshold = articleThreshold,
            AlwaysShow = alwaysShow,
        };
    }

    internal static void RenderBox(RenderHelpers helpers, ThemePalette p, string title, int width)
    {
        helpers.WriteLine();
        helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}╭{new string('─', width - 2)}╮{Reset}");
        var displayTitle = RenderHelpers.TruncateText(title, width - 4);
        helpers.WriteLine(
            $"{p.HeaderBorderFg.AnsiFg}│ {p.HeaderTitleFg.AnsiFg}" +
            $"{displayTitle.PadRight(width - 4)}{p.HeaderBorderFg.AnsiFg} │{Reset}");
        helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}╰{new string('─', width - 2)}╯{Reset}");
    }

    internal sealed class ArticleStatus
    {
        public required string Title { get; init; }

        public ArticleState State { get; set; }

        public string? Method { get; set; }

        /// <summary>
        /// workspace-i3kh: wall-clock timestamp when the article entered the
        /// Processing state. Null while Pending. Set on the Pending→Processing
        /// transition so <see cref="Elapsed"/> can compute a meaningful
        /// duration on completion.
        /// </summary>
        public DateTime? StartedAtUtc { get; set; }

        /// <summary>
        /// workspace-i3kh: wall-clock timestamp when the article reached a
        /// terminal state (Completed / Failed / Cached). Null while
        /// Processing. Together with <see cref="StartedAtUtc"/> drives the
        /// "· {elapsed}" suffix on the per-article line.
        /// </summary>
        public DateTime? FinishedAtUtc { get; set; }

        /// <summary>
        /// workspace-i3kh: convenience accessor for the elapsed duration
        /// rendered on the progress screen. Returns null while still
        /// Processing or if no start timestamp was captured (defensive).
        /// </summary>
        public TimeSpan? Elapsed
            => StartedAtUtc.HasValue && FinishedAtUtc.HasValue
                ? FinishedAtUtc.Value - StartedAtUtc.Value
                : null;
    }
}
