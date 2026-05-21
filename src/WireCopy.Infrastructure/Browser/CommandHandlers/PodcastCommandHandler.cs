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
    /// background job is running and the user has detached it via 'D' (or
    /// ":podcast" while a run is in flight). Subscribes to the live progress
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
                        var validation = await ttsService.ValidateApiKeyAsync(validationCts.Token).ConfigureAwait(false);

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

            // workspace-yib5 Phase 5: pre-cache-analysis missing-key modal.
            // When the user pressed `p` without an OpenAI key, surface a
            // one-line modal BEFORE the slow cache-analysis + cost-gate so
            // they don't sit through minutes of work before being asked to
            // set up. Pressing `s` deep-links into HandleSetApiKey with a
            // resume callback that re-enters the generate flow on save
            // success, satisfying the bead's "zero extra keystrokes beyond
            // the actual key paste + Enter" acceptance criterion.
            if (!ttsService.IsConfigured)
            {
                var choice = await PodcastMissingKeyModal.ShowAsync(ctx, options, ct).ConfigureAwait(false);
                if (choice == PodcastMissingKeyModal.Outcome.Cancel)
                {
                    ctx.NavigationService.SetStatusMessage("Set up cancelled — press p again when ready");
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    return false;
                }

                var resumed = false;
                await SettingsCommandHandler.HandleSetApiKey(
                    ctx,
                    options,
                    ct,
                    subtitle: "Set this up and we'll continue generating your podcast.",
                    resumeAfterSave: () =>
                    {
                        resumed = true;
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);

                if (resumed)
                {
                    // Hydration above already pulled the saved key into the
                    // service; on a fresh save the FormField path also calls
                    // SetApiKeyOverride. Loop the whole attempt so cache
                    // analysis + cost-gate run with the configured key.
                    options = ctx.GetCurrentRenderOptions();
                    return true;
                }

                ctx.NavigationService.SetStatusMessage("Set up cancelled — press p again when ready");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return false;
            }

            // Pre-flight FFmpeg check: fail fast before slow cache analysis
            var audioAssembler = scope.ServiceProvider.GetRequiredService<IAudioAssembler>();
            if (!await audioAssembler.ValidatePrerequisitesAsync(ct).ConfigureAwait(false))
            {
                await PodcastProgressScreens.ShowErrorScreenAsync(ctx, options, "FFmpeg is not installed or not found in PATH.", [], ct).ConfigureAwait(false);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return false;
            }

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

            // Pre-flight cache analysis for cost estimation (with progress UI)
            CacheAnalysis? cacheAnalysis = await PodcastConfirmationScreens
                .ShowCacheAnalysisScreenAsync(ctx, options, collection, orchestrator, ct)
                .ConfigureAwait(false);
            options = ctx.GetCurrentRenderOptions();

            // workspace-lr80: cost-gate confirm. Only pops when the estimated
            // spend OR article count exceeds the user's configured threshold.
            // Below threshold = silent kickoff (preserves Phase 1's
            // one-keystroke contract from workspace-kuu7).
            if (cacheAnalysis != null)
            {
                var costGateConfig = LoadCostGateConfig(settingsStore);
                var proceed = await PodcastCostGateModal.ShowAsync(ctx, options, cacheAnalysis, costGateConfig, ct).ConfigureAwait(false);
                if (!proceed)
                {
                    ctx.NavigationService.SetStatusMessage("Podcast cancelled");
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    return false;
                }

                options = ctx.GetCurrentRenderOptions();
            }

            // Show cache-wait screen if preloading is still in progress
            var skippedWait = await PodcastConfirmationScreens.ShowCacheWaitScreenAsync(ctx, options, collection, ct).ConfigureAwait(false);

            // Re-fetch render options in case terminal resized during wait
            if (!skippedWait)
            {
                options = ctx.GetCurrentRenderOptions();
            }

            // workspace-ny44: ShowConfirmationScreenAsync deleted. With Phase 1
            // (workspace-kuu7, modal-for-missing-key) and Phase 5
            // (workspace-yib5, resume-after-save) in place, the user has
            // already cleared every pre-flight gate by the time we reach
            // here — there is no remaining ambiguous state to confirm. We
            // proceed straight to the progress screen.
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
                // If TTS failed due to auth error, clear the persisted key so user is re-prompted.
                // workspace-p1px: BucketNotPublic / FeedNotReachable produce 403 in their diagnostic
                // strings (the public HTTP GET returned 403) but are STORAGE failures, not TTS auth
                // failures — guarded against clearing the OpenAI key for the wrong reason.
                var error = result.ErrorMessage ?? "Unknown error";
                var isStorageFailure = result.FailureDetail?.FailureClass
                    is FeedPublishFailureClass.BucketNotPublic
                    or FeedPublishFailureClass.FeedNotReachable
                    or FeedPublishFailureClass.FeedNotParseable;
                if (!isStorageFailure
                    && (error.Contains("401", StringComparison.Ordinal)
                        || error.Contains("403", StringComparison.Ordinal)
                        || error.Contains("not configured", StringComparison.OrdinalIgnoreCase)))
                {
                    settingsStore.Remove("OpenAiApiKey");
                    ttsService.SetApiKeyOverride(string.Empty);
                }

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
                ctx.NavigationService.SetStatusMessage($"Podcast error: {ex.Message}");
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
