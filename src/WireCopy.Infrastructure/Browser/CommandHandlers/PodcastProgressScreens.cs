// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Animations;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Configuration;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Progress, completion, and error screens for podcast generation,
/// extracted from PodcastCommandHandler.
/// </summary>
internal static class PodcastProgressScreens
{
    private const string Reset = PodcastCommandHandler.Reset;

    /// <summary>
    /// workspace-i3kh: builds the "· 32s" or "· 1m 14s" suffix appended to
    /// completed/failed/cached per-article lines on the progress screen.
    /// Returns an empty string while the article is still in-flight or if
    /// no start timestamp was captured (defensive). Total seconds rounds to
    /// the nearest whole second so the suffix doesn't churn millisecond-to-
    /// millisecond on each render frame.
    /// </summary>
    internal static string FormatElapsedSuffix(PodcastCommandHandler.ArticleStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var elapsed = status.Elapsed;
        if (!elapsed.HasValue)
        {
            return string.Empty;
        }

        var totalSeconds = (int)Math.Round(elapsed.Value.TotalSeconds);
        if (totalSeconds < 0)
        {
            // Defensive: clock skew shouldn't render a "-3s" suffix.
            return string.Empty;
        }

        if (totalSeconds < 60)
        {
            return $"{totalSeconds}s";
        }

        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return seconds == 0 ? $"{minutes}m" : $"{minutes}m {seconds}s";
    }

    /// <summary>
    /// workspace-6dzj: builds the status-bar copy shown after the user
    /// presses Esc to cancel a podcast run. Counts Completed + Cached articles
    /// (work that the user got something useful from) so the message reads
    /// "Cancelled — N articles completed" instead of the bare "Podcast
    /// cancelled" that gave no signal about partial progress.
    /// </summary>
    internal static string BuildCancelledStatusMessage(IReadOnlyList<PodcastCommandHandler.ArticleStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        var completed = statuses.Count(s =>
            s.State is PodcastCommandHandler.ArticleState.Completed
                or PodcastCommandHandler.ArticleState.Cached);

        var suffix = completed == 1 ? string.Empty : "s";
        return $"Cancelled — {completed} article{suffix} completed";
    }

    internal static async Task<PodcastResult?> ShowProgressScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        Domain.Entities.Collections.Collection collection,
        IPodcastOrchestrator orchestrator,
        CancellationToken ct)
    {
        // workspace-vkhr Phase D: resolve the singleton background-job manager
        // so the modal can hand ownership to it on detach. The manager is
        // optional (null in test fixtures that don't register it) — when
        // null, the modal degrades to the pre-Phase-D behaviour: no detach,
        // generation cancelled if the modal exits.
        IPodcastBackgroundJobManager? jobManager = null;
        IPodcastGenerationGate? generationGate = null;
        try
        {
            using var managerScope = ctx.ScopeFactory.CreateScope();
            jobManager = managerScope.ServiceProvider.GetService<IPodcastBackgroundJobManager>();

            // workspace-frpl.1 (B0): the process-wide generation gate (singleton —
            // survives this scope) so a manual run can't overlap a scheduled one.
            generationGate = managerScope.ServiceProvider.GetService<IPodcastGenerationGate>();
        }
        catch (Exception ex)
        {
            ctx.Logger.LogDebug(ex, "Podcast background job manager/gate unavailable (test path?)");
        }

        // workspace-zh3u: resolve destination paths up-front so the in-progress
        // footer can answer "where will this land?" while the pipeline runs.
        // Failures are swallowed and degrade to a null FeedUrl; we never
        // block generation on this lookup.
        PodcastTargets? targets = null;
        try
        {
            targets = await orchestrator.ResolveTargetsAsync(collection, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogWarning(ex, "Failed to resolve podcast targets for progress footer");
        }

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

        // Signal generating state so the CTA renders with progress bar
        ctx.IsPodcastGenerating = true;
        ctx.PodcastGenerationProgress = 0.0;
        ctx.PodcastFeedUrl = targets?.FeedUrl;

        PodcastProgress? latestProgress = null;
        var animFrame = 0;

        // workspace-v34z Phase B: aggregator turns the raw progress events into
        // a weighted global percent + velocity-based ETA, so the bar no longer
        // sits frozen on the 70% TTS step.
        var aggregator = new PodcastProgressAggregator();

        var lastProcessingIndex = -1;
        var lastPhase = PodcastPhase.CachingContent;
        var progress = new Progress<PodcastProgress>(p =>
        {
            Volatile.Write(ref latestProgress, p);
            aggregator.Observe(p);

            // workspace-vkhr Phase D: fan the same event out to the manager
            // so the status-bar badge (and any future re-attached modal) see
            // the live stream. Done up-front so a detached run still updates
            // the badge even when statuses[] mutation paths bail out below.
            jobManager?.ReportProgress(p);

            // Update shared progress for CTA rendering — use the weighted global
            // percent, not the orchestrator's stair-stepped PercentComplete.
            ctx.PodcastGenerationProgress = Math.Clamp(aggregator.GlobalPercent, 0.0, 1.0);

            // Phase transition: reset article statuses when moving to GeneratingAudio
            if (p.Phase == PodcastPhase.GeneratingAudio && lastPhase == PodcastPhase.CachingContent)
            {
                for (var i = 0; i < articleCount; i++)
                {
                    if (statuses[i].State != PodcastCommandHandler.ArticleState.Failed)
                    {
                        statuses[i].State = PodcastCommandHandler.ArticleState.Pending;
                        statuses[i].Method = null;
                    }
                }

                lastProcessingIndex = -1;
            }

            lastPhase = p.Phase;

            // CachingContent phase: track per-article content loading
            if (p.Phase == PodcastPhase.CachingContent && p.CurrentArticle > 0 && p.CurrentArticle <= articleCount)
            {
                var idx = p.CurrentArticle - 1;

                if (p.IsArticleComplete)
                {
                    statuses[idx].State = p.IsArticleSuccess ? PodcastCommandHandler.ArticleState.Completed : PodcastCommandHandler.ArticleState.Failed;
                    statuses[idx].Method = null;
                    statuses[idx].FinishedAtUtc ??= DateTime.UtcNow;
                }
                else
                {
                    if (statuses[idx].State != PodcastCommandHandler.ArticleState.Processing)
                    {
                        statuses[idx].StartedAtUtc ??= DateTime.UtcNow;
                    }

                    statuses[idx].State = PodcastCommandHandler.ArticleState.Processing;
                    statuses[idx].Method = p.ExtractionMethod;
                }
            }

            // GeneratingAudio phase: track per-article audio generation
            if (p.Phase == PodcastPhase.GeneratingAudio && p.CurrentArticle > 0 && p.CurrentArticle <= articleCount)
            {
                var idx = p.CurrentArticle - 1;

                if (lastProcessingIndex >= 0 && lastProcessingIndex < idx &&
                    statuses[lastProcessingIndex].State == PodcastCommandHandler.ArticleState.Processing)
                {
                    statuses[lastProcessingIndex].State = PodcastCommandHandler.ArticleState.Completed;
                    statuses[lastProcessingIndex].FinishedAtUtc ??= DateTime.UtcNow;
                }

                // workspace-i3kh: an article entering the TTS phase carries
                // a stale FinishedAtUtc from when its CachingContent phase
                // marked it complete. Clear that stamp so the in-flight TTS
                // step doesn't render a stale elapsed suffix. PRESERVE
                // StartedAtUtc so the eventual rendered elapsed reflects the
                // TOTAL per-article time (caching + TTS), per the bead's
                // "per-article elapsed time on completion" wording.
                if (!p.IsFromCache && statuses[idx].State != PodcastCommandHandler.ArticleState.Processing)
                {
                    statuses[idx].FinishedAtUtc = null;
                }

                statuses[idx].State = p.IsFromCache ? PodcastCommandHandler.ArticleState.Cached : PodcastCommandHandler.ArticleState.Processing;
                lastProcessingIndex = idx;
            }
        });

        // workspace-frpl.1 (B0): take the generation gate at the moment generation
        // STARTS (not at StartJob/detach) so a manual run and a scheduled run can
        // never contend for the shared foreground page or the same output M4B.
        // If the gate is already held (e.g. a scheduled run is mid-flight), bail
        // with a status message rather than starting a second generation.
        IDisposable? generationLease = null;
        if (generationGate is not null && !generationGate.TryAcquire(out generationLease))
        {
            ctx.NavigationService.SetStatusMessage(
                "A podcast is already being generated — please wait for it to finish.");
            return null;
        }

        // workspace-vkhr Phase D: do NOT wrap genCts in a `using` here — when
        // the user backs out (Esc/b) to detach, ownership of the CTS transfers to
        // the background job manager, and disposing it would cancel the run.
        // The detach path leaks the CTS to the manager; the non-detach path
        // disposes it in the finally block below.
        var genCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var generationTask = orchestrator.GeneratePodcastAsync(collection, progress, genCts.Token);

        // workspace-frpl.1 (B0): release the gate exactly when the generation TASK
        // completes — success, failure, or cancellation — so the lease lives with
        // the work, not the modal. This survives detach (the background task keeps
        // the gate until it finishes) and double-dispose is a no-op.
        if (generationLease is not null)
        {
            var leaseToRelease = generationLease;
            _ = generationTask.ContinueWith(
                _ => leaseToRelease.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // Detach flag — set when 'D' is pressed during an active run. Drives
        // both the early loop exit and the finally-block cleanup decisions.
        var detached = false;

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
                    options.TerminalHeight,
                    targets,
                    aggregator);
                helpers.ClearRemainingLines();

                pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(ct);
                var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task completed;
                try
                {
                    var tickTask = Task.Delay(PodcastCommandHandler.AnimationIntervalMs, tickCts.Token);
                    completed = await Task.WhenAny(pendingKeyTask, generationTask, tickTask).ConfigureAwait(false);
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

                    // Once generation has finished, any exit key just collects
                    // the result.
                    if ((command.Type is CommandType.GoBack or CommandType.CancelRun ||
                         command.RawKeyChar is 'd' or 'D') &&
                        generationTask.IsCompleted)
                    {
                        break;
                    }

                    // workspace-m8es.2: backing out (Esc / b / Backspace — all
                    // CommandType.GoBack) DETACHES: the generation task keeps
                    // running, the CTS + task hand over to the singleton
                    // manager so the status-bar badge, the CTA progress, and
                    // the Shift+P restore path can all find it. Going back
                    // must never threaten the run.
                    // workspace-nahg item 3: 'd' is the explicit, discoverable
                    // detach verb — same behaviour as Esc, but advertised in
                    // the hint line so detach isn't an overloaded Esc.
                    if ((command.Type == CommandType.GoBack || command.RawKeyChar is 'd' or 'D') &&
                        jobManager is not null)
                    {
                        try
                        {
                            jobManager.StartJob(collection, targets, generationTask, genCts);
                            detached = true;
                            ctx.NavigationService.SetStatusMessage(
                                "Generating in background. Press Shift+P to restore.");
                            return null;
                        }
                        catch (InvalidOperationException ex)
                        {
                            // Manager already has a job — should be impossible
                            // here (we own the only modal entry point) but
                            // degrade gracefully and stay in the modal.
                            ctx.Logger.LogWarning(
                                ex, "Failed to detach podcast modal; staying attached.");
                            continue;
                        }
                    }

                    // workspace-m8es.2: cancelling the run is a DELIBERATE act
                    // — its own keystroke ('x') plus a confirmation, never the
                    // default exit. With no job manager wired (degraded/test
                    // paths) GoBack also lands here so the modal stays
                    // escapable.
                    if (command.Type == CommandType.CancelRun || command.Type == CommandType.GoBack ||
                        command.RawKeyChar is 'd' or 'D')
                    {
                        var shouldCancel = await ShowCancellationConfirmAsync(ctx, ct).ConfigureAwait(false);
                        if (shouldCancel)
                        {
                            // workspace-nahg item 2: visible feedback while the
                            // generation task unwinds its in-flight articles —
                            // the await below can take seconds and the screen
                            // would otherwise sit frozen on stale progress.
                            PaintCancellingNotice(ctx, options);

                            await genCts.CancelAsync().ConfigureAwait(false);
                            try
                            {
                                await generationTask.ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // Expected
                            }

                            // workspace-6dzj: surface the cancelled-mid-run count
                            // BEFORE the caller sets a generic "Podcast cancelled"
                            // message. Reading `statuses` here is safe — the
                            // generation task has finished (we just awaited it
                            // through OCE) so no more progress events will mutate
                            // the array.
                            ctx.NavigationService.SetStatusMessage(BuildCancelledStatusMessage(statuses));

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
                    animFrame = (animFrame + 1) % PodcastCommandHandler.AnimationFrames.Length;
                }
            }

            // Collect the result from the completed generation task
            if (generationTask.IsCompleted)
            {
                try
                {
                    var result = await generationTask.ConfigureAwait(false);

                    for (var i = 0; i < statuses.Length; i++)
                    {
                        if (statuses[i].State == PodcastCommandHandler.ArticleState.Processing)
                        {
                            statuses[i].State = PodcastCommandHandler.ArticleState.Completed;

                            // workspace-i3kh: stamp completion time for any
                            // article we forcibly transition to Completed
                            // here — otherwise the elapsed suffix would be
                            // missing for the last article in the run.
                            statuses[i].FinishedAtUtc ??= DateTime.UtcNow;
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

                    // workspace-pvr6: untyped — defensive catch for exceptions
                    // the orchestrator's own outer catch missed (e.g. a
                    // pre-Async ArgumentNullException). The classifier's
                    // generic-fallback "see logs" remediation is the honest
                    // answer for a path the orchestrator didn't claim.
                    return PodcastResult.Failure(ex.Message);
                }
            }
        }
        finally
        {
            // Clear generating state so the CTA returns to normal — but only
            // if we're NOT detaching. A detached run keeps these flags set so
            // the CTA still shows "Generating" until the manager fires
            // Completed (workspace-vkhr Phase D).
            if (!detached)
            {
                ctx.IsPodcastGenerating = false;
                ctx.PodcastGenerationProgress = 0.0;
                ctx.PodcastFeedUrl = null;

                if (!generationTask.IsCompleted)
                {
                    await genCts.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        await generationTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }
                    catch (Exception ex)
                    {
                        ctx.Logger.LogWarning(ex, "Generation task faulted during cleanup: {Message}", ex.Message);
                    }
                }

                // Safe to dispose: nobody else owns this CTS in the non-detach
                // path. In the detach path ownership transferred to the
                // manager and disposing here would cancel the run.
                genCts.Dispose();

                // workspace-vkhr Phase D: clear the manager so the next podcast
                // run can start cleanly. No-op when no job was started (e.g.
                // the modal never hit detach OR the cancellation path).
                if (jobManager is not null && jobManager.HasActiveJob)
                {
                    jobManager.Clear();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// workspace-vkhr Phase D: entry point for restoring the progress modal
    /// from a detached background job. Reads the active job + last snapshot
    /// from the manager, rebuilds a minimal statuses[] view, subscribes to
    /// the live progress stream, and awaits the same generation task the
    /// original modal kicked off. On completion, fans out to the same
    /// result / error / cancellation flows as <see cref="ShowProgressScreenAsync"/>.
    /// </summary>
    /// <returns>The collected <see cref="PodcastResult"/> when the job
    /// completed (including failure), or <c>null</c> on cancellation or
    /// when the user detached again.</returns>
    internal static async Task<PodcastResult?> ShowProgressScreenAttachedAsync(
        CommandContext ctx,
        RenderOptions options,
        IPodcastBackgroundJobManager jobManager,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(jobManager);

        if (!jobManager.HasActiveJob || jobManager.CurrentJobTask is null)
        {
            return null;
        }

        var collection = jobManager.Collection;
        var targets = jobManager.Targets;
        var generationTask = jobManager.CurrentJobTask;

        // Phase F adds full per-article persistence; until then re-attach
        // accepts the per-article history loss — we seed everything to
        // Pending and let the live stream rebuild in-flight state.
        var articleCount = collection?.Items.Count ?? 0;
        var statuses = new PodcastCommandHandler.ArticleStatus[articleCount];
        for (var i = 0; i < articleCount; i++)
        {
            statuses[i] = new PodcastCommandHandler.ArticleStatus
            {
                Title = collection!.Items[i].Title,
                State = PodcastCommandHandler.ArticleState.Pending,
            };
        }

        var aggregator = new PodcastProgressAggregator();

        // Seed both the snapshot and the aggregator from whatever the
        // manager already cached so the first render frame is up-to-date
        // (not a 0% bar that re-jitters on the next event).
        var seedSnapshot = jobManager.LastSnapshot;
        PodcastProgress? latestProgress = seedSnapshot;
        if (seedSnapshot is not null)
        {
            aggregator.Observe(seedSnapshot);
            var seedIdx = seedSnapshot.CurrentArticle - 1;
            if (seedIdx >= 0 && seedIdx < statuses.Length)
            {
                statuses[seedIdx].State = PodcastCommandHandler.ArticleState.Processing;
            }
        }

        var animFrame = 0;
        var detachedAgain = false;
        var lastProcessingIndex = -1;
        var lastPhase = seedSnapshot?.Phase ?? PodcastPhase.CachingContent;

        // Subscriber must be cheap — the orchestrator delivers events on the
        // generation thread. We only mutate volatile/array state; nothing here
        // blocks or renders.
        void OnProgress(object? sender, PodcastProgress p)
        {
            Volatile.Write(ref latestProgress, p);
            aggregator.Observe(p);
            ctx.PodcastGenerationProgress = Math.Clamp(aggregator.GlobalPercent, 0.0, 1.0);

            if (p.Phase == PodcastPhase.GeneratingAudio && lastPhase == PodcastPhase.CachingContent)
            {
                for (var i = 0; i < articleCount; i++)
                {
                    if (statuses[i].State != PodcastCommandHandler.ArticleState.Failed)
                    {
                        statuses[i].State = PodcastCommandHandler.ArticleState.Pending;
                        statuses[i].Method = null;
                    }
                }

                lastProcessingIndex = -1;
            }

            lastPhase = p.Phase;

            if (p.Phase == PodcastPhase.CachingContent && p.CurrentArticle > 0 && p.CurrentArticle <= articleCount)
            {
                var idx = p.CurrentArticle - 1;
                if (p.IsArticleComplete)
                {
                    statuses[idx].State = p.IsArticleSuccess
                        ? PodcastCommandHandler.ArticleState.Completed
                        : PodcastCommandHandler.ArticleState.Failed;
                    statuses[idx].Method = null;
                    statuses[idx].FinishedAtUtc ??= DateTime.UtcNow;
                }
                else
                {
                    if (statuses[idx].State != PodcastCommandHandler.ArticleState.Processing)
                    {
                        statuses[idx].StartedAtUtc ??= DateTime.UtcNow;
                    }

                    statuses[idx].State = PodcastCommandHandler.ArticleState.Processing;
                    statuses[idx].Method = p.ExtractionMethod;
                }
            }

            if (p.Phase == PodcastPhase.GeneratingAudio && p.CurrentArticle > 0 && p.CurrentArticle <= articleCount)
            {
                var idx = p.CurrentArticle - 1;
                if (lastProcessingIndex >= 0 && lastProcessingIndex < idx &&
                    statuses[lastProcessingIndex].State == PodcastCommandHandler.ArticleState.Processing)
                {
                    statuses[lastProcessingIndex].State = PodcastCommandHandler.ArticleState.Completed;
                    statuses[lastProcessingIndex].FinishedAtUtc ??= DateTime.UtcNow;
                }

                if (!p.IsFromCache && statuses[idx].State != PodcastCommandHandler.ArticleState.Processing)
                {
                    statuses[idx].FinishedAtUtc = null;
                }

                statuses[idx].State = p.IsFromCache
                    ? PodcastCommandHandler.ArticleState.Cached
                    : PodcastCommandHandler.ArticleState.Processing;
                lastProcessingIndex = idx;
            }
        }

        jobManager.ProgressUpdated += OnProgress;

        // While re-attached the CTA badge should reflect "still generating"
        // — same flags the original modal sets. They were left alone during
        // detach so this is largely a no-op, but explicit is safer than
        // implicit.
        ctx.IsPodcastGenerating = true;
        ctx.PodcastFeedUrl = targets?.FeedUrl;

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
                    options.TerminalHeight,
                    targets,
                    aggregator);
                helpers.ClearRemainingLines();

                pendingKeyTask ??= ctx.InputHandler.WaitForInputAsync(ct);
                var tickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task completed;
                try
                {
                    var tickTask = Task.Delay(PodcastCommandHandler.AnimationIntervalMs, tickCts.Token);
                    completed = await Task.WhenAny(pendingKeyTask, generationTask, tickTask).ConfigureAwait(false);
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

                    // Once generation has finished, any exit key just collects
                    // the result.
                    if ((command.Type is CommandType.GoBack or CommandType.CancelRun ||
                         command.RawKeyChar is 'd' or 'D') &&
                        generationTask.IsCompleted)
                    {
                        break;
                    }

                    // workspace-m8es.2: backing out (Esc / b / Backspace)
                    // re-detaches. Idempotent — the job is already registered
                    // on the manager, so we just exit the attached loop
                    // without touching the manager state.
                    // workspace-nahg item 3: 'd' is the explicit detach verb.
                    if (command.Type == CommandType.GoBack || command.RawKeyChar is 'd' or 'D')
                    {
                        detachedAgain = true;
                        ctx.NavigationService.SetStatusMessage(
                            "Generating in background. Press Shift+P to restore.");
                        return null;
                    }

                    // workspace-m8es.2: 'x' + confirm is the only cancel path.
                    if (command.Type == CommandType.CancelRun)
                    {
                        var shouldCancel = await ShowCancellationConfirmAsync(ctx, ct).ConfigureAwait(false);
                        if (shouldCancel)
                        {
                            // workspace-nahg item 2: visible feedback while the
                            // in-flight articles unwind after the cancel.
                            PaintCancellingNotice(ctx, options);

                            jobManager.RequestCancellation();
                            try
                            {
                                await generationTask.ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // Expected
                            }

                            ctx.NavigationService.SetStatusMessage(BuildCancelledStatusMessage(statuses));
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
                    animFrame = (animFrame + 1) % PodcastCommandHandler.AnimationFrames.Length;
                }
            }

            if (generationTask.IsCompleted)
            {
                try
                {
                    var result = await generationTask.ConfigureAwait(false);
                    for (var i = 0; i < statuses.Length; i++)
                    {
                        if (statuses[i].State == PodcastCommandHandler.ArticleState.Processing)
                        {
                            statuses[i].State = PodcastCommandHandler.ArticleState.Completed;
                            statuses[i].FinishedAtUtc ??= DateTime.UtcNow;
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
                    ctx.Logger.LogError(ex, "Podcast generation failed (attached)");
                    return PodcastResult.Failure(ex.Message);
                }
            }
        }
        finally
        {
            jobManager.ProgressUpdated -= OnProgress;

            // Only clear when we're not detaching again — re-detach keeps the
            // job alive on the manager.
            if (!detachedAgain)
            {
                ctx.IsPodcastGenerating = false;
                ctx.PodcastGenerationProgress = 0.0;
                ctx.PodcastFeedUrl = null;
                if (jobManager.HasActiveJob)
                {
                    jobManager.Clear();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// workspace-nahg item 1: visual confirm modal for cancelling a run.
    /// Renders the same small 3-row bordered box the cost-gate uses
    /// (via <see cref="PodcastInlineBox"/>) instead of the bare
    /// "Cancel podcast generation? (y/n):" text prompt, and reads a single
    /// keystroke: y confirms, n / Esc / q declines. Unhandled keys flash the
    /// border in the warning colour (workspace-nahg item 4).
    /// </summary>
    internal static async Task<bool> ShowCancellationConfirmAsync(
        CommandContext ctx,
        CancellationToken ct)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var options = ctx.GetCurrentRenderOptions();
        var summary = "Cancel podcast generation?";
        var hint = $"{palette.GetAccentFg().AnsiFg}[y]{Reset} {palette.PrimaryText.AnsiFg}yes{Reset}  " +
                   $"{palette.GetAccentFg().AnsiFg}[n]{Reset} {palette.PrimaryText.AnsiFg}no{Reset}";
        var hintPlain = "[y] yes  [n] no";

        try
        {
            PodcastInlineBox.RenderBox(options, palette, summary, hint, hintPlain);

            while (!ct.IsCancellationRequested)
            {
                var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

                if (command.Type == CommandType.TerminalResized)
                {
                    options = ctx.GetCurrentRenderOptions();
                    PodcastInlineBox.RenderBox(options, palette, summary, hint, hintPlain);
                    continue;
                }

                if (command.RawKeyChar is 'y' or 'Y')
                {
                    return true;
                }

                if (command.RawKeyChar is 'n' or 'N' ||
                    command.Type is CommandType.GoBack or CommandType.Quit)
                {
                    return false;
                }

                await PodcastInlineBox.FlashInvalidKeyAsync(
                    options, palette, summary, hint, hintPlain, ct).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            // The progress loop repaints the full screen on its next frame,
            // but blank the box rows anyway so nothing lingers if the caller
            // returns without another frame (e.g. immediate exit).
            PodcastInlineBox.ClearBoxRows(options);
        }
    }

    /// <summary>
    /// workspace-nahg item 2: paints a single "Cancelling…" status row where
    /// the confirm box sat, so the screen doesn't sit frozen while the
    /// generation task unwinds its in-flight articles after the user confirms
    /// a cancel. Painted once (not animated) — the await that follows can
    /// take a few seconds and this row is the honest signal that the app is
    /// working on tearing the run down.
    /// </summary>
    internal static void PaintCancellingNotice(CommandContext ctx, RenderOptions options)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
        var row = Math.Max(0, options.TerminalHeight - 4);
        helpers.WriteAt(
            0,
            row,
            $"  {palette.GetWarningFg().AnsiFg}⟳ Cancelling in-flight articles…{Reset}");
    }

    internal static async Task<CompletionScreenAction> ShowCompletionScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        PodcastResult result,
        CancellationToken ct)
    {
        // Play celebration animation before showing the completion screen
        try
        {
            using var animScope = ctx.ScopeFactory.CreateScope();
            var browserConfig = animScope.ServiceProvider
                .GetRequiredService<IOptions<BrowserConfiguration>>().Value;

            if (!browserConfig.DisableAnimations)
            {
                var celebrationMessage = CelebrationAnimation.BuildMessage(
                    result.ArticlesProcessed, result.TotalDuration);
                CelebrationAnimation.Play(
                    BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme),
                    celebrationMessage,
                    options.TerminalWidth,
                    options.TerminalHeight);
            }
        }
        catch (Exception ex)
        {
            // Animation failure is non-fatal — proceed to the completion screen
            ctx.Logger.LogDebug(ex, "Celebration animation failed (non-fatal)");
        }

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

            // Key hints — assemble based on what's available so the user
            // only sees keystrokes that do something (workspace-n49i).
            var hintParts = new List<string>
            {
                $"{p.GetAccentFg().AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:back{Reset}",
            };

            if (!string.IsNullOrEmpty(result.LocalFilePath))
            {
                hintParts.Add($"{p.GetAccentFg().AnsiFg}o{Reset}{p.SecondaryText.AnsiFg}:open folder{Reset}");
                hintParts.Add($"{p.GetAccentFg().AnsiFg}c{Reset}{p.SecondaryText.AnsiFg}:copy path{Reset}");
            }

            hintParts.Add($"{p.GetAccentFg().AnsiFg}r{Reset}{p.SecondaryText.AnsiFg}:retry{Reset}");

            if (maxScroll > 0)
            {
                hintParts.Add($"{p.GetAccentFg().AnsiFg}j/k{Reset}{p.SecondaryText.AnsiFg}:scroll{Reset}");
            }

            helpers.WriteLine("  " + string.Join("   ", hintParts));
            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.Type is CommandType.ActivateLink or CommandType.GoBack or CommandType.Quit)
            {
                return CompletionScreenAction.Back;
            }

            // workspace-n49i Phase 4: keystroke handlers for the result screen.
            // `o` opens the OS file manager at the output folder, `c` copies
            // the absolute file path to the clipboard via OSC52. Both no-op
            // gracefully if LocalFilePath is unset. `r` re-runs the generation
            // flow from scratch (retry-from-scratch fallback per the bead).
            if (command.RawKeyChar == 'o' && !string.IsNullOrEmpty(result.LocalFilePath))
            {
                TryOpenOutputFolder(ctx, result.LocalFilePath);
                continue;
            }

            if (command.RawKeyChar == 'c' && !string.IsNullOrEmpty(result.LocalFilePath))
            {
                CopyToClipboardOsc52(result.LocalFilePath);
                ctx.NavigationService.SetStatusMessage("File path copied (if your terminal supports it)");
                continue;
            }

            if (command.RawKeyChar == 'r')
            {
                return CompletionScreenAction.Retry;
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

        return CompletionScreenAction.Back;
    }

    /// <summary>
    /// Opens the OS file manager at the folder containing
    /// <paramref name="localFilePath"/> (workspace-n49i Phase 4). Cross-platform
    /// via <c>UseShellExecute = true</c> — defers to xdg-open on Linux, open
    /// on macOS, explorer.exe on Windows. Failures are logged and swallowed
    /// so the result screen never crashes on a non-critical "show me where
    /// this lives" gesture.
    /// </summary>
    internal static void TryOpenOutputFolder(CommandContext ctx, string localFilePath)
    {
        try
        {
            var folder = Path.GetDirectoryName(localFilePath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                ctx.NavigationService.SetStatusMessage("Output folder not found", StatusSeverity.Error);
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
            ctx.NavigationService.SetStatusMessage("Opened output folder");
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to open output folder for {Path}", localFilePath);
            ctx.NavigationService.SetStatusMessage("Couldn't open folder — file path copied (if your terminal supports it)", StatusSeverity.Error);
            CopyToClipboardOsc52(localFilePath);
        }
    }

    /// <summary>
    /// workspace-n49i Phase 4: emits a labelled value line, wrapping the
    /// value onto a continuation line under the value column when it doesn't
    /// fit. The continuation line uses the same indent as the value (label
    /// column blanked) so the text reads as a single logical row. Avoids
    /// relying on the terminal's natural wrap, which breaks selection
    /// rectangles and OSC2026 reflow.
    /// </summary>
    internal static void EmitLabelledValue(
        List<string> lines,
        ThemePalette p,
        string paddedLabel,
        string blankLabel,
        string value,
        string valueColor,
        string trailingSuffix,
        string suffixColor,
        int valueWidth)
    {
        // The value+suffix together must fit within valueWidth. When they
        // don't, render the value alone on a wrapped line, then the suffix
        // (if any) below it. For now: if value alone fits, append the suffix
        // and rely on hyphen-free copy-friendly wrap only if necessary.
        if (value.Length + trailingSuffix.Length <= valueWidth)
        {
            lines.Add(
                $"  {p.SecondaryText.AnsiFg}{paddedLabel}{Reset}{valueColor}{value}{Reset}" +
                $"{suffixColor}{trailingSuffix}{Reset}");
            return;
        }

        // Path too long for one line. Hard-wrap the value character-by-
        // character into chunks of valueWidth, then place the suffix (if any)
        // on its own line under the value column so it never overlaps.
        var valueChunks = ChunkLine(value, valueWidth);
        for (var i = 0; i < valueChunks.Count; i++)
        {
            var labelHere = i == 0 ? paddedLabel : blankLabel;
            lines.Add($"  {p.SecondaryText.AnsiFg}{labelHere}{Reset}{valueColor}{valueChunks[i]}{Reset}");
        }

        if (!string.IsNullOrWhiteSpace(trailingSuffix))
        {
            lines.Add($"  {p.SecondaryText.AnsiFg}{blankLabel}{Reset}{suffixColor}{trailingSuffix.TrimStart()}{Reset}");
        }
    }

    /// <summary>
    /// Splits a long string into fixed-width chunks at the requested width.
    /// Used for hard-wrapping copy-paste-friendly values (file paths, URLs)
    /// that don't have natural word breaks. Returns at least one chunk —
    /// empty input becomes a single empty chunk so the caller can still
    /// render the label row.
    /// </summary>
    internal static List<string> ChunkLine(string s, int width)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(s) || width <= 0)
        {
            chunks.Add(s ?? string.Empty);
            return chunks;
        }

        for (var i = 0; i < s.Length; i += width)
        {
            var len = Math.Min(width, s.Length - i);
            chunks.Add(s.Substring(i, len));
        }

        return chunks;
    }

    /// <summary>
    /// workspace-n49i Phase 4: classifies the completion result into one of
    /// the three documented shapes. Drives both the headline glyph and which
    /// content blocks render.
    /// </summary>
    internal static CompletionShape ClassifyCompletion(PodcastResult result)
    {
        if (result.ArticlesFailed > 0)
        {
            return CompletionShape.PartialFailure;
        }

        if (string.IsNullOrEmpty(result.FeedUrl))
        {
            return CompletionShape.LocalOnlySuccess;
        }

        return CompletionShape.FullSuccess;
    }

    internal static List<string> BuildCompletionLines(
        ThemePalette p,
        PodcastResult result,
        int width)
    {
        var shape = ClassifyCompletion(result);
        var lines = new List<string>();

        // workspace-n49i: single-line headline per shape (A/B/C). Replaces
        // the previous "Podcast Ready!" box-header that didn't distinguish
        // full success from partial failure.
        var (glyph, glyphColor, headline) = shape switch
        {
            CompletionShape.FullSuccess => ("✓", p.GetSuccessFg().AnsiFg, "Podcast generated and published"),
            CompletionShape.LocalOnlySuccess => ("✓", p.GetSuccessFg().AnsiFg, "Podcast generated (local-only — no RSS publish)"),
            CompletionShape.PartialFailure => ("⚠", p.GetWarningFg().AnsiFg, $"Podcast generated with {result.ArticlesFailed} article failure{(result.ArticlesFailed == 1 ? string.Empty : "s")}"),
            _ => ("✓", p.GetSuccessFg().AnsiFg, "Podcast generated"),
        };

        lines.Add(string.Empty);
        lines.Add($"  {glyphColor}{glyph}{Reset} {p.PrimaryText.AnsiFg}{headline}{Reset}");
        lines.Add(string.Empty);

        // ---- Labelled metadata block: File / Feed / Duration / Cost ----
        // Two-column "key   value" rendering at a fixed gutter so the
        // values line up vertically in all three shapes.
        const int labelWidth = 11; // "Duration" + 3 spaces, longest label below "Feed"/"File"
        string Label(string s) => s.PadRight(labelWidth);

        // Bead acceptance: paths must be copy-friendly with NO truncation, and
        // explicitly wrap onto a separate line under the value column when
        // they don't fit (workspace-n49i Acceptance: "Path display is monospace
        // and copy-friendly (no truncation; long paths wrap on a separate
        // line if needed)"). The natural terminal wrap is unacceptable — it
        // breaks selection rectangles and OSC2026 reflow.
        const int margin = 2;
        var valueColumn = labelWidth + margin;
        var availableForValue = Math.Max(20, width - valueColumn - 2);

        if (!string.IsNullOrEmpty(result.LocalFilePath))
        {
            var suffix = shape == CompletionShape.PartialFailure
                ? $"  ({result.ArticlesProcessed} of {result.ArticlesProcessed + result.ArticlesFailed} articles)"
                : string.Empty;

            EmitLabelledValue(
                lines,
                p,
                Label("File"),
                blankLabel: new string(' ', labelWidth),
                value: result.LocalFilePath,
                valueColor: p.HeaderTitleFg.AnsiFg,
                trailingSuffix: suffix,
                suffixColor: p.SecondaryText.AnsiFg,
                valueWidth: availableForValue);
        }

        if (!string.IsNullOrEmpty(result.FeedUrl) && shape == CompletionShape.FullSuccess)
        {
            EmitLabelledValue(
                lines,
                p,
                Label("Feed"),
                blankLabel: new string(' ', labelWidth),
                value: result.FeedUrl,
                valueColor: p.PromptFg.AnsiFg,
                trailingSuffix: string.Empty,
                suffixColor: p.SecondaryText.AnsiFg,
                valueWidth: availableForValue);
        }

        var durationValue = $"{result.ArticlesProcessed} article{(result.ArticlesProcessed == 1 ? string.Empty : "s")} · {FormatDuration(result.TotalDuration)} audio · {FormatFileSize(result.FileSizeBytes)}";
        lines.Add($"  {p.SecondaryText.AnsiFg}{Label("Duration")}{Reset}{p.PrimaryText.AnsiFg}{durationValue}{Reset}");

        if (result.TotalCost > 0)
        {
            var costValue = $"${result.TotalCost:F4}";
            if (result.ArticlesCached > 0)
            {
                costValue += $" ({result.ArticlesCached} cached, no charge)";
            }

            lines.Add($"  {p.SecondaryText.AnsiFg}{Label("Cost")}{Reset}{p.PrimaryText.AnsiFg}{costValue}{Reset}");
        }

        lines.Add(string.Empty);

        // ---- Shape-specific extras ----
        switch (shape)
        {
            case CompletionShape.FullSuccess:
                // workspace-xx61: OSC 52 clipboard success can't be detected, so
                // don't assert it — the URL is shown above as the reliable copy.
                lines.Add($"  {p.SecondaryText.AnsiFg}Feed URL is shown above (also copied to your clipboard if your terminal supports it). Subscribe in your podcast app:{Reset}");
                lines.Add($"    {p.SecondaryText.AnsiFg}Apple Podcasts / Overcast → Add show by URL → paste{Reset}");
                lines.Add($"    {p.SecondaryText.AnsiFg}Pocket Casts → Search → \"Add by URL\" → paste{Reset}");
                lines.Add($"    {p.SecondaryText.AnsiFg}Any RSS reader → Add feed → paste{Reset}");
                break;

            case CompletionShape.LocalOnlySuccess:
                lines.Add($"  {p.SecondaryText.AnsiFg}Listen with VLC, Apple Books, or any M4B-aware player.{Reset}");
                lines.Add($"  {p.SecondaryText.AnsiFg}Configure a GCS bucket in Setup to enable RSS publishing.{Reset}");
                break;

            case CompletionShape.PartialFailure:
                lines.Add($"  {p.SecondaryText.AnsiFg}Failures:{Reset}");
                var maxList = Math.Min(result.FailedArticleDetails.Count, 5);
                for (var i = 0; i < maxList; i++)
                {
                    var failure = result.FailedArticleDetails[i];
                    var displayUrl = string.IsNullOrEmpty(failure.Url) ? failure.Title : failure.Url;
                    var displayReason = RenderHelpers.TruncateText(failure.Reason, Math.Max(20, width - 8));
                    lines.Add($"    {p.ErrorFg.AnsiFg}{i + 1}.{Reset} {p.PrimaryText.AnsiFg}{RenderHelpers.TruncateText(displayUrl, Math.Max(20, width - 8))}{Reset}");
                    lines.Add($"       {p.SecondaryText.AnsiFg}— {displayReason}{Reset}");
                }

                if (result.FailedArticleDetails.Count > maxList)
                {
                    lines.Add($"    {p.SecondaryText.AnsiFg}... and {result.FailedArticleDetails.Count - maxList} more{Reset}");
                }

                break;
        }

        lines.Add(string.Empty);

        return lines;
    }

    internal static Task<bool> ShowErrorScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        string errorMessage,
        IReadOnlyList<ArticleFailure> failedArticles,
        CancellationToken ct)
    {
        return ShowErrorScreenAsync(ctx, options, errorMessage, failedArticles, typedDetail: null, ct);
    }

    /// <summary>
    /// Renders the typed (Step, Reason, Fix) error screen and waits for user
    /// input. Returns <c>true</c> when the user pressed <c>r</c> to retry —
    /// callers should re-enter the generation flow from scratch. Returns
    /// <c>false</c> on Enter / Esc / Quit. workspace-p1px: enables the
    /// "run gsutil then press [r]" remediation flow without the user having
    /// to back out and press 'p' again.
    /// </summary>
    internal static async Task<bool> ShowErrorScreenAsync(
        CommandContext ctx,
        RenderOptions options,
        string errorMessage,
        IReadOnlyList<ArticleFailure> failedArticles,
        PodcastFailureDetail? typedDetail,
        CancellationToken ct)
    {
        // workspace-n49i Shape D: typed (Step, Reason, Fix) tuple replaces
        // the previous heuristic-bulleted "What to try" list.
        // workspace-3a2k Phase E: prefer the typed FailureDetail when present
        // (publish-step failures with a configured bucket carry it) so the
        // bucket-public remediation surfaces verbatim instead of via the
        // string-pattern heuristic.
        var classification = PodcastFailureClassifier.Classify(typedDetail, errorMessage, failedArticles);

        while (!ct.IsCancellationRequested)
        {
            var p = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            helpers.Clear();

            var width = Math.Max(20, options.TerminalWidth - 2);

            // Headline: single line with ✗ glyph in error color
            helpers.WriteLine();
            helpers.WriteLine($"  {p.ErrorFg.AnsiFg}✗{Reset} {p.PrimaryText.AnsiFg}Podcast generation failed{Reset}");
            helpers.WriteLine();

            // Typed (Step, Reason, Fix) block — single-line each so the
            // user can read the three crucial questions left-to-right.
            // Multi-line wraps indent the continuation under the value
            // column so the labels stay visually anchored.
            const int labelWidth = 13;
            var blankLabel = new string(' ', labelWidth);

            helpers.WriteLine($"  {p.SecondaryText.AnsiFg}{"At step:".PadRight(labelWidth)}{Reset}{p.PrimaryText.AnsiFg}{classification.Step}{Reset}");

            var reasonWrap = RenderHelpers.WrapText(classification.Reason, Math.Max(20, width - labelWidth - 4));
            for (var i = 0; i < reasonWrap.Count; i++)
            {
                var label = i == 0 ? "Reason:".PadRight(labelWidth) : blankLabel;
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}{label}{Reset}{p.PrimaryText.AnsiFg}{reasonWrap[i]}{Reset}");
            }

            var fixWrap = RenderHelpers.WrapText(classification.Fix, Math.Max(20, width - labelWidth - 4));
            for (var i = 0; i < fixWrap.Count; i++)
            {
                var label = i == 0 ? "Fix:".PadRight(labelWidth) : blankLabel;
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}{label}{Reset}{p.PrimaryText.AnsiFg}{fixWrap[i]}{Reset}");
            }

            helpers.WriteLine();

            // Per-article failure details (when present — every-article-failed
            // total errors carry the per-article list).
            if (failedArticles.Count > 0)
            {
                helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Failed articles:{Reset}");

                var maxArticleLines = Math.Max(1,
                    options.TerminalHeight - helpers.LinesWritten - 6);
                var displayCount = Math.Min(failedArticles.Count, maxArticleLines);

                for (var i = 0; i < displayCount; i++)
                {
                    var failure = failedArticles[i];
                    var displayTitle = RenderHelpers.TruncateText(failure.Title, width - 8);
                    helpers.WriteLine(
                        $"    {p.ErrorFg.AnsiFg}✗{Reset} {p.PrimaryText.AnsiFg}{displayTitle}{Reset}");
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

            // workspace-p1px: surface the retry affordance so the BucketNotPublic
            // remediation flow ("run this gsutil one-liner, then press r") works
            // end-to-end. The retry signal returns up through HandleGeneratePodcast's
            // retry loop, which re-runs from scratch with fresh pre-flight checks.
            //
            // workspace-n0kb: when the failure points at a fixable credential
            // (TTS auth → OpenAI key, GCS publish → bucket), surface an `s`
            // hint that deep-links into the relevant Setup row. Hidden for
            // failure modes where no Setup row maps (FFmpeg install, network).
            var hintParts = new List<string>
            {
                $"{p.GetAccentFg().AnsiFg}r{Reset}{p.SecondaryText.AnsiFg}:retry{Reset}",
            };
            if (classification.RelevantSetupRow.HasValue)
            {
                hintParts.Add($"{p.GetAccentFg().AnsiFg}s{Reset}{p.SecondaryText.AnsiFg}:open setup{Reset}");
            }

            hintParts.Add($"{p.GetAccentFg().AnsiFg}Enter{Reset}{p.SecondaryText.AnsiFg}:back{Reset}");
            helpers.WriteLine("  " + string.Join("   ", hintParts));
            helpers.ClearRemainingLines();

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (command.RawKeyChar == 'r')
            {
                return true;
            }

            // workspace-n0kb: 's' opens the relevant Setup row for the
            // failure. Per the bead's "on save, returns to library"
            // acceptance, after DispatchRowAsync unwinds we exit the error
            // screen entirely (return false ⇒ back through
            // RunGeneratePodcastAttempt ⇒ HandleGeneratePodcast's outer loop
            // exits ⇒ user lands on the collection-items view they came
            // from). The user can re-press `p` once their fix is in place.
            if (command.RawKeyChar == 's' && classification.RelevantSetupRow.HasValue)
            {
                await SettingsCommandHandler.DispatchRowAsync(
                    ctx, options, classification.RelevantSetupRow.Value, ct).ConfigureAwait(false);
                return false;
            }

            if (command.Type != CommandType.NoOp)
            {
                return false;
            }
        }

        return false;
    }

    internal static List<string> GetSuggestionsForError(
        string errorMessage,
        IReadOnlyList<ArticleFailure> failedArticles)
    {
        var suggestions = new List<string>();

        // workspace-2xej.10: local-engine failures must never advise checking
        // platform.openai.com — key on the sentinel FIRST.
        if (errorMessage.StartsWith("Local narration: ", StringComparison.Ordinal))
        {
            suggestions.Add("Run the Test in Settings (c → Local engine) to see details");
            suggestions.Add("If this is the first run, the model download may have been interrupted — just retry");
            suggestions.Add("uv installed? → curl -LsSf https://astral.sh/uv/install.sh | sh");
            return suggestions;
        }

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

    internal static void RenderProgressContent(
        RenderHelpers helpers,
        ThemePalette p,
        PodcastProgress? progress,
        int animFrame,
        PodcastCommandHandler.ArticleStatus[] statuses,
        int terminalWidth,
        int terminalHeight,
        PodcastTargets? targets = null,
        PodcastProgressAggregator? aggregator = null)
    {
        var width = Math.Max(20, terminalWidth - 2);
        PodcastCommandHandler.RenderBox(helpers, p, "Generating Podcast", width);
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

        // workspace-v34z: prefer the velocity-based aggregator output when it
        // is available. Falls back to the legacy stair-step on PercentComplete
        // when no aggregator is supplied (test paths, etc.).
        var fraction = aggregator?.GlobalPercent
            ?? Math.Clamp((progress?.PercentComplete ?? 0) / 100.0, 0, 1);
        var percent = (int)Math.Round(fraction * 100);
        var barWidth = Math.Max(10, width - 22);
        var isComplete = percent >= 100;

        // workspace-m8es.1: one bar style across all podcast surfaces (the
        // reading-list CTA's celebration/success treatment).
        var bar = Indicators.PodcastBar(p, fraction, barWidth);
        var etaLabel = FormatEta(aggregator?.Eta, isComplete);
        helpers.WriteLine($"  {bar} {percent,3}%   {p.SecondaryText.AnsiFg}{etaLabel}{Reset}");
        helpers.WriteLine();

        if (aggregator is not null)
        {
            RenderPhaseSubBars(helpers, p, aggregator, width);
            helpers.WriteLine();
        }

        // workspace-rz1c: render a "Rate-limited by OpenAI, retrying in Xs"
        // banner whenever the TTS service is currently waiting out an
        // exponential-backoff sleep. Without this the progress bar appears
        // frozen during retries and the user can't tell whether the job is
        // stuck. The Message field carries the pre-formatted user copy so we
        // don't have to reconstruct it here.
        if (progress is { IsRetrying: true } && !string.IsNullOrEmpty(progress.Message))
        {
            helpers.WriteLine(
                $"  {p.GetWarningFg().AnsiFg}⟳{Reset} {p.PrimaryText.AnsiFg}{RenderHelpers.TruncateText(progress.Message!, width - 4)}{Reset}");
            helpers.WriteLine();
        }

        var maxArticleLines = Math.Max(1, terminalHeight - helpers.LinesWritten - 4);
        var articleCount = Math.Min(statuses.Length, maxArticleLines);

        var isCaching = progress?.Phase == PodcastPhase.CachingContent;

        for (var i = 0; i < articleCount; i++)
        {
            var status = statuses[i];

            // workspace-i3kh: reserve room for the elapsed suffix (e.g.
            // " · 1m 14s") so a long title doesn't crowd it off the line.
            var elapsedSuffix = FormatElapsedSuffix(status);
            var elapsedReserve = elapsedSuffix.Length;
            var displayTitle = RenderHelpers.TruncateText(status.Title, Math.Max(10, width - 10 - elapsedReserve));
            var methodSuffix = status.Method != null
                ? $" {p.SecondaryText.AnsiFg}({status.Method}){Reset}"
                : string.Empty;
            var elapsedRender = elapsedSuffix.Length > 0
                ? $" {p.SecondaryText.AnsiFg}· {elapsedSuffix}{Reset}"
                : string.Empty;

            var line = status.State switch
            {
                PodcastCommandHandler.ArticleState.Cached =>
                    $"  {p.GetSuccessFg().AnsiFg}✓{Reset} {displayTitle} {p.SecondaryText.AnsiFg}(cached){Reset}{elapsedRender}",
                PodcastCommandHandler.ArticleState.Completed =>
                    $"  {p.GetSuccessFg().AnsiFg}✓{Reset} {displayTitle}{elapsedRender}",
                PodcastCommandHandler.ArticleState.Processing when isCaching =>
                    $"  {p.HeaderTitleFg.AnsiFg}↻{Reset} {displayTitle}" +
                    $"{methodSuffix}" +
                    $"{p.SecondaryText.AnsiFg}{PodcastCommandHandler.AnimationFrames[animFrame]}{Reset}",
                PodcastCommandHandler.ArticleState.Processing =>
                    $"  {p.HeaderTitleFg.AnsiFg}♫{Reset} {displayTitle}" +
                    $"{p.SecondaryText.AnsiFg}{PodcastCommandHandler.AnimationFrames[animFrame]}{Reset}",
                PodcastCommandHandler.ArticleState.Failed =>
                    $"  {p.ErrorFg.AnsiFg}✗{Reset} {displayTitle}{elapsedRender}",
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

        // workspace-zh3u: destination footer. Tells the user where the
        // result lands and that closing the terminal cancels the run.
        // Truncate paths in the middle so both the parent folder and the
        // filename remain visible.
        if (targets is not null)
        {
            RenderDestinationFooter(helpers, p, targets, width);
        }

        // workspace-m8es.2: backing out is the safe, primary exit (the run
        // keeps going); cancelling spend is the deliberate, secondary one.
        // workspace-nahg item 3: 'd' names detach explicitly so "keep it
        // running in the background" is a discoverable verb, not an
        // overloaded Esc (which means cancel on the cost-gate modal).
        helpers.WriteLine(
            $"  {p.GetAccentFg().AnsiFg}d{Reset}{p.SecondaryText.AnsiFg}:detach (keeps generating){Reset}   " +
            $"{p.GetAccentFg().AnsiFg}Esc{Reset}{p.SecondaryText.AnsiFg}:back{Reset}   " +
            $"{p.GetAccentFg().AnsiFg}x{Reset}{p.SecondaryText.AnsiFg}:cancel run{Reset}");
    }

    /// <summary>
    /// Renders the destination footer beneath the per-article progress list.
    /// workspace-0v31: when a feed URL is set, the subscribe panel headlines
    /// the footer — a friendly walk-away message followed by 'Subscribe' +
    /// the full feed URL — with the local file path demoted underneath.
    /// When no feed URL is set (local-only mode), shows the original local
    /// path line + the local-only notice; the walk-away copy is suppressed
    /// (nothing to subscribe to).
    /// </summary>
    internal static void RenderDestinationFooter(
        RenderHelpers helpers,
        ThemePalette p,
        PodcastTargets targets,
        int width)
    {
        const string SubscribeLabel = "Subscribe";
        const string LocalLabel = "Will save to";

        if (!string.IsNullOrEmpty(targets.FeedUrl))
        {
            // workspace-0v31: friendly walk-away copy as the headline. Tells
            // the user (a) this takes a few minutes, (b) it's safe to step
            // away, and (c) the action they should take right now is to
            // subscribe in their podcast app — the episode lands there
            // automatically when generation finishes.
            helpers.WriteLine(
                $"  {p.GetAccentFg().AnsiFg}Takes 3–5 minutes. Subscribe in your podcast app now — " +
                $"the episode will appear there as soon as it's ready.{Reset}");
            helpers.WriteLine();

            // Render the full feed URL — copy-friendly, never middle-elided.
            // Wrap to a second line at a slash boundary when the URL is too
            // long to fit on one line at the current terminal width.
            var feedUrl = targets.FeedUrl!;
            var labelText = $"{SubscribeLabel}: ";
            var firstLineWidth = Math.Max(20, width - labelText.Length - 4);

            if (feedUrl.Length <= firstLineWidth)
            {
                helpers.WriteLine(
                    $"  {p.SecondaryText.AnsiFg}{labelText}{Reset}" +
                    $"{p.GetSuccessFg().AnsiFg}{feedUrl}{Reset}");
            }
            else
            {
                // Wrap at the last '/' that fits the first line so each line
                // is a recognisable URL fragment, not a mid-word cut.
                var breakPoint = feedUrl.LastIndexOf('/', Math.Min(feedUrl.Length - 1, firstLineWidth));
                if (breakPoint <= 0)
                {
                    breakPoint = firstLineWidth;
                }

                var head = feedUrl[..breakPoint];
                var tail = feedUrl[breakPoint..];
                helpers.WriteLine(
                    $"  {p.SecondaryText.AnsiFg}{labelText}{Reset}" +
                    $"{p.GetSuccessFg().AnsiFg}{head}{Reset}");

                // Continuation line indented under the URL start so it reads
                // as a single value spanning two lines.
                var indent = new string(' ', 2 + labelText.Length);
                helpers.WriteLine(
                    $"{indent}{p.GetSuccessFg().AnsiFg}{tail}{Reset}");
            }

            helpers.WriteLine();

            // Local path is informational now, not the headline.
            var localLabelWidth = SubscribeLabel.Length + 1;
            var localAvailable = Math.Max(20, width - localLabelWidth - 6);
            var localDisplay = PodcastSetupHelpers.TruncateMiddle(targets.LocalFilePath, localAvailable);
            helpers.WriteLine(
                $"  {p.SecondaryText.AnsiFg}{LocalLabel.PadRight(localLabelWidth)}{Reset}  " +
                $"{p.SecondaryText.AnsiFg}{localDisplay}{Reset}");
        }
        else
        {
            // Local-only mode: no subscribe URL, no walk-away copy.
            var labelWidth = "Will save to".Length;
            var available = Math.Max(20, width - labelWidth - 6);
            var localDisplay = PodcastSetupHelpers.TruncateMiddle(targets.LocalFilePath, available);
            helpers.WriteLine(
                $"  {p.SecondaryText.AnsiFg}{LocalLabel.PadRight(labelWidth)}{Reset}  " +
                $"{p.PrimaryText.AnsiFg}{localDisplay}{Reset}");
            helpers.WriteLine(
                $"  {p.SecondaryText.AnsiFg}(no GCS bucket configured — generating locally only){Reset}");
        }

        helpers.WriteLine();

        // workspace-m8es.2: backing out frees the screen and the job keeps
        // running. Closing the terminal still cancels (that's Phase F).
        helpers.WriteLine(
            $"  {p.SecondaryText.AnsiFg}Running. Press Esc to free the screen — generation continues. " +
            $"Closing this terminal still cancels.{Reset}");
        helpers.WriteLine();
    }

    internal static void CopyToClipboardOsc52(string text)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Console.Write($"\x1b]52;c;{base64}\x07");
    }

    /// <summary>
    /// Renders the ETA suffix shown after the global progress bar
    /// (workspace-v34z). "done" when the run finishes, "ETA —" until the
    /// aggregator has enough history, otherwise a human-friendly seconds /
    /// minutes string. Defensively clamps negative or absurdly large
    /// TimeSpans so a clock-skew event can't render "ETA -3s" or "ETA 300m".
    /// </summary>
    internal static string FormatEta(TimeSpan? eta, bool isComplete)
    {
        if (isComplete)
        {
            return "done";
        }

        if (eta is null)
        {
            return "ETA —";
        }

        var totalSeconds = eta.Value.TotalSeconds;
        if (totalSeconds < 0)
        {
            // Negative ETA is treated as "no useful estimate"; the aggregator
            // already returns null for negatives but this guards direct callers.
            return "ETA —";
        }

        // Cap at 1 hour to avoid rendering stale or unbounded values as
        // huge numbers like "ETA 300m".
        if (totalSeconds > 3600)
        {
            return "ETA >1h";
        }

        var seconds = (int)Math.Round(totalSeconds);
        if (seconds < 1)
        {
            return "ETA <1s";
        }

        if (seconds < 60)
        {
            return $"ETA {seconds}s";
        }

        var minutes = seconds / 60;
        var rem = seconds % 60;
        return rem == 0 ? $"ETA {minutes}m" : $"ETA {minutes}m {rem}s";
    }

    /// <summary>
    /// Renders four sub-bars (extracting / synthesizing / assembling /
    /// publishing) underneath the global bar so the user can see exactly
    /// where the work is happening (workspace-v34z).
    /// </summary>
    internal static void RenderPhaseSubBars(
        RenderHelpers helpers,
        ThemePalette p,
        PodcastProgressAggregator aggregator,
        int width)
    {
        const int LabelWidth = 12;
        var subBarWidth = Math.Max(8, width - LabelWidth - 14);

        RenderPhaseSubBar(
            helpers,
            p,
            "Extracting",
            aggregator.GetPhasePercent(PodcastPhase.CachingContent),
            aggregator.GetPhaseDetail(PodcastPhase.CachingContent),
            subBarWidth,
            LabelWidth);
        RenderPhaseSubBar(
            helpers,
            p,
            "Synthesizing",
            aggregator.GetPhasePercent(PodcastPhase.GeneratingAudio),
            aggregator.GetPhaseDetail(PodcastPhase.GeneratingAudio),
            subBarWidth,
            LabelWidth);
        RenderPhaseSubBar(
            helpers,
            p,
            "Assembling",
            aggregator.GetPhasePercent(PodcastPhase.AssemblingAudio),
            aggregator.GetPhaseDetail(PodcastPhase.AssemblingAudio),
            subBarWidth,
            LabelWidth);
        RenderPhaseSubBar(
            helpers,
            p,
            "Publishing",
            aggregator.GetPhasePercent(PodcastPhase.Publishing),
            aggregator.GetPhaseDetail(PodcastPhase.Publishing),
            subBarWidth,
            LabelWidth);
    }

    internal static void RenderPhaseSubBar(
        RenderHelpers helpers,
        ThemePalette p,
        string label,
        double fraction,
        string detail,
        int barWidth,
        int labelWidth)
    {
        // workspace-lxga: prefix each sub-bar with a state glyph so the user
        // can name the current stage at a glance:
        //   ✓ complete (fraction >= 0.999)
        //   ⟳ in-flight (any partial fraction)
        //   · pending (fraction == 0, not yet started)
        var (glyph, glyphColor) = ClassifyPhaseState(fraction, p);

        // workspace-m8es.1: sub-bars share the unified podcast bar style; the
        // ✓/⟳/· state glyphs stay as the at-a-glance stage indicator.
        var bar = Indicators.PodcastBar(p, fraction, barWidth);

        // Leave room for the glyph + space at the front of the label column so
        // the bars stay column-aligned.
        var glyphCellWidth = 2;
        var paddedLabel = label.PadRight(Math.Max(1, labelWidth - glyphCellWidth));
        helpers.WriteLine(
            $"  {glyphColor}{glyph}{Reset} {p.SecondaryText.AnsiFg}{paddedLabel}{Reset} {bar}  {p.SecondaryText.AnsiFg}{detail}{Reset}");
    }

    /// <summary>
    /// workspace-lxga: maps a phase fraction to the (glyph, color) used as
    /// a state indicator on the four sub-bars. Pulled out so unit tests can
    /// pin the boundary behaviour (0.0 / 0.001 / 0.999 / 1.0) without
    /// re-deriving the threshold logic from the renderer.
    /// </summary>
    internal static (string Glyph, string Color) ClassifyPhaseState(double fraction, ThemePalette p)
    {
        ArgumentNullException.ThrowIfNull(p);

        if (fraction >= 0.999)
        {
            return ("✓", p.GetSuccessFg().AnsiFg);
        }

        if (fraction > 0.0)
        {
            return ("⟳", p.GetWarningFg().AnsiFg);
        }

        return ("·", p.SecondaryText.AnsiFg);
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
    }

    internal static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B",
        };
    }
}
