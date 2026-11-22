// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.AudioEngine;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.8 (B7) — runs ONE recipe occurrence end-to-end. For each ordered
/// step it loads the source unattended in the background (B5), resolves the durable section against
/// today's links (B2), applies the TakeMode, and appends the items to a single
/// running list (first-wins cross-step URL dedup). A QUALITY FLOOR then decides
/// whether to publish at all: it ABORTS with no episode if nothing resolved, if any
/// <see cref="RecipeStep.Required"/> step contributed nothing, or if the assembled
/// count is below the floor — so the user never gets a silent near-empty episode.
/// Otherwise it builds a transient collection whose name embeds the occurrence key
/// (so the orchestrator's name-keyed article cache AND its output path never collide
/// with a prior or manual run), generates + publishes via the orchestrator while the
/// existing job-manager surface shows progress, and FINALIZES the supplied
/// <see cref="ScheduledRun"/> (the caller — scheduler B6 or run-now B12a — owns the
/// generation gate and wrote the Running row; this pipeline only ever moves it to a
/// terminal state). All failure paths are caught and recorded on the run so a single
/// bad occurrence never escapes to kill the scheduler loop.
/// </summary>
internal sealed class RecipeRunPipeline : IRecipeRunPipeline
{
    /// <summary>The assembled-item floor below which we refuse to publish (an empty/near-empty episode is never useful).</summary>
    private const int MinimumItems = 1;

    /// <summary>How many times a page that fails for a network reason is tried before its step is given up.</summary>
    private const int LoadAttempts = 3;

    /// <summary>
    /// workspace-wxll0.6 — how many no-progress windows one narrated part may take (40 minutes by
    /// default). audio-engine gives its narrator 30 minutes to load the model - the first job
    /// after the engine or its model changed downloads several GB, and a narrator it restarts
    /// loads again - plus 10 minutes for the part itself, and fails the part with its own reason
    /// when that runs out. Stopping the run sooner would cut off an engine that is still within
    /// its budget, and the next run would start the same download under the same clock.
    /// </summary>
    private const int NarrationPatience = 2;

    /// <summary>What the narrate tile says while audio-engine narrates another job first (the tile shows about 34 characters).</summary>
    private const string WaitingTurnHint = "another narration first";

    private readonly IUnattendedSectionLoader _loader;
    private readonly ISectionResolver _resolver;
    private readonly IPodcastOrchestrator _orchestrator;
    private readonly IPodcastBackgroundJobManager _jobManager;
    private readonly IScheduledRunRepository _runRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IScheduleStore _scheduleStore;
    private readonly ILogger<RecipeRunPipeline> _logger;
    private readonly ISemanticSectionRecovery? _semanticRecovery;
    private readonly IHierarchyConfigStore? _configStore;
    private readonly IPodcastNarrator? _narrator;
    private readonly IAudioEngineLauncher? _launcher;
    private readonly ILogBuffer? _logBuffer;
    private readonly IRoutineRunTracker? _tracker;

    public RecipeRunPipeline(
        IUnattendedSectionLoader loader,
        ISectionResolver resolver,
        IPodcastOrchestrator orchestrator,
        IPodcastBackgroundJobManager jobManager,
        IScheduledRunRepository runRepository,
        IUnitOfWork unitOfWork,
        IScheduleStore scheduleStore,
        ILogger<RecipeRunPipeline> logger,
        ISemanticSectionRecovery? semanticRecovery = null,
        IHierarchyConfigStore? configStore = null,
        IPodcastNarrator? narrator = null,
        IAudioEngineLauncher? launcher = null,
        ILogBuffer? logBuffer = null,
        IRoutineRunTracker? tracker = null)
    {
        _loader = loader;
        _resolver = resolver;
        _orchestrator = orchestrator;
        _jobManager = jobManager;
        _runRepository = runRepository;
        _unitOfWork = unitOfWork;
        _scheduleStore = scheduleStore;
        _logger = logger;
        _semanticRecovery = semanticRecovery;
        _configStore = configStore;
        _narrator = narrator;
        _launcher = launcher;
        _logBuffer = logBuffer;
        _tracker = tracker;
    }

    public async Task RunAsync(ScheduleRecipe recipe, ScheduledRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(run);

        var tracked = false;
        RunStallWatchdog? stall = null;
        RunKeepAwake? awake = null;
        try
        {
            if (recipe.Steps.Count == 0)
            {
                // A zero-step routine never generates audio (S4): recorded Skipped.
                run.Finish(
                    ScheduledRunStatus.Skipped,
                    itemCount: 0,
                    errorClass: "NothingToCollect",
                    errorMessage: RoutineText.NothingToCollect);
                await PersistAsync(recipe, run, RunStatus.Skipped).ConfigureAwait(false);
                return;
            }

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ct = runCts.Token;
            BeginTracking(recipe, run, runCts);
            tracked = true;

            // workspace-q06ia.4: the run is stopped for standing still, never for taking long.
            stall = new RunStallWatchdog(runCts, RunStallWatchdog.ResolveWindow(_logger));

            // workspace-wxll0.3: a run lasts minutes to hours with nobody at the machine; a computer
            // that goes to sleep under it freezes it.
            awake = RunKeepAwake.Start(_logger);

            _logger.LogInformation("Routine run {Recipe:l} ({Occurrence:l}) started with {Steps} section(s)", recipe.Name, run.OccurrenceKey, recipe.Steps.Count);

            // workspace-b6cmm: fail fast (and self-start a local audio-engine) before any page load.
            _tracker?.SetStage(recipe.Id, "narration", RoutineRunStageState.Running);
            if (_narrator != null)
            {
                // workspace-wxll0.5: before the preflight looks at the engine - one that is busy only
                // with this routine's leftover can then be replaced when its code has moved on.
                await SupersedeEarlierEpisodesAsync(recipe, null, ct).ConfigureAwait(false);
                var narration = await NarratorPreflight.EnsureReadyAsync(_narrator, _launcher, ct).ConfigureAwait(false);
                if (!narration.Ready)
                {
                    var message = string.IsNullOrWhiteSpace(narration.Message) ? "audio-engine isn't ready." : narration.Message;
                    _logger.LogInformation("Narration preflight: not ready - {Message:l}", message);
                    var firstSentence = message.Split(". ", 2, StringSplitOptions.None)[0];
                    _tracker?.SetStage(recipe.Id, "narration", RoutineRunStageState.Failed, firstSentence);
                    run.Finish(
                        ScheduledRunStatus.Failed,
                        itemCount: 0,
                        errorClass: "NarrationNotReady",
                        errorMessage: message);
                    await PersistAsync(recipe, run, RunStatus.Failed).ConfigureAwait(false);
                    return;
                }

                _logger.LogInformation("Narration preflight: audio-engine ready");

                // workspace-wxll0.1: audio-engine publishes to ITS default feed. With none set the
                // episode only lands on this computer — say so now, not after hours of narration.
                if (await _narrator.GetDefaultFeedAsync(ct).ConfigureAwait(false) is null)
                {
                    _logger.LogInformation("audio-engine has no default feed: this run's episode will be saved on this computer, not published");
                    _tracker?.SetStage(recipe.Id, "publish", RoutineRunStageState.Waiting, "no feed set: saved here");
                }
            }

            _tracker?.SetStage(recipe.Id, "narration", RoutineRunStageState.Done, "audio-engine is ready");
            stall.Report("narration ready");

            var assembled = new List<(string Url, string Title)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outcomes = new List<RunStepOutcome>();

            // workspace-wxll0.1: sections of one page are read from ONE load of it — three steps on
            // Today's Paper used to load it three times, seconds apart, and each could see a different page.
            var pages = new Dictionary<string, UnattendedSectionLoad>(StringComparer.OrdinalIgnoreCase);

            var stepIndex = 0;
            foreach (var step in recipe.Steps)
            {
                ct.ThrowIfCancellationRequested();
                var stageKey = $"step:{stepIndex++}";
                _tracker?.SetStage(recipe.Id, stageKey, RoutineRunStageState.Running, HostOf(step.SourceUrl));
                var before = assembled.Count;
                if (!pages.ContainsKey(step.SourceUrl))
                {
                    _logger.LogInformation("Loading {Url:l}", step.SourceUrl);
                }

                stall.Report(stageKey + " loading");
                var (status, matchCount, diagnostic, todaysHeading) =
                    await ResolveStepAsync(step, assembled, seen, pages, stall, ct).ConfigureAwait(false);
                stall.Report(stageKey + " " + status);
                _logger.LogInformation(
                    "{Section:l}: {Status:l}, {Count} stories{Diagnostic:l}",
                    step.SectionName,
                    status,
                    assembled.Count - before,
                    string.IsNullOrEmpty(diagnostic) ? string.Empty : " - " + diagnostic);
                var contributed = assembled.Count - before;
                if (contributed > 0)
                {
                    // A section printed under a day's own heading says so: "8 stories · “Sunday Business” today".
                    var where = todaysHeading is null ? HostOf(step.SourceUrl) : $"“{todaysHeading}” today";
                    var stories = contributed == 1 ? "story" : "stories";
                    _tracker?.SetStage(recipe.Id, stageKey, RoutineRunStageState.Done, $"{contributed} {stories} · {where}");
                }
                else
                {
                    _tracker?.SetStage(recipe.Id, stageKey, RoutineRunStageState.Failed, string.IsNullOrWhiteSpace(diagnostic) ? status : diagnostic);
                }

                outcomes.Add(new RunStepOutcome
                {
                    SectionName = step.SectionName,
                    SourceUrl = step.SourceUrl,
                    Status = status,
                    Required = step.Required,
                    MatchCount = matchCount,
                    ItemsContributed = assembled.Count - before,
                    Diagnostic = diagnostic,
                    TodaysHeading = todaysHeading,
                    ItemUrls = assembled.Skip(before).Select(a => a.Url).ToList(),
                });
            }

            var outcomesJson = JsonSerializer.Serialize(outcomes);

            // QUALITY FLOOR: never publish an empty/near-empty episode, and never
            // publish if a REQUIRED source contributed nothing.
            var requiredMissed = outcomes.Where(o => o.Required).Any(o => o.ItemsContributed == 0);
            if (assembled.Count < MinimumItems || requiredMissed)
            {
                var reason = requiredMissed
                    ? "A required section contributed no articles this occurrence"
                    : "No articles resolved across any step this occurrence";
                _logger.LogWarning(
                    "Recipe {Recipe} occurrence {Occurrence} aborted by quality floor: {Reason}",
                    recipe.Name,
                    run.OccurrenceKey,
                    reason);
                run.Finish(
                    ScheduledRunStatus.Failed,
                    itemCount: assembled.Count,
                    stepOutcomesJson: outcomesJson,
                    errorClass: "NoContentResolved",
                    errorMessage: reason);
                await PersistAsync(recipe, run, RunStatus.Failed).ConfigureAwait(false);
                return;
            }

            await GenerateAndFinalizeAsync(recipe, run, assembled, outcomes, outcomesJson, stall, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user's stop, the app closing, or no progress for the stall window. Mark
            // Interrupted (terminal, visible) rather than letting a bare OCE bubble up and
            // tear down the scheduler's tick loop. Best-effort persist on a fresh token.
            var userStopped = _tracker?.Get(recipe.Id)?.CancelRequested == true;
            var stalled = !userStopped && stall is { Fired: true };
            await MarkInterruptedAsync(recipe, run, userStopped, stalled ? StalledMessage(recipe.Id, stall!.Patience) : null)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recipe {Recipe} occurrence {Occurrence} failed", recipe.Name, run.OccurrenceKey);
            if (run.Status is ScheduledRunStatus.Running or ScheduledRunStatus.Pending)
            {
                run.Finish(
                    ScheduledRunStatus.Failed,
                    itemCount: 0,
                    errorClass: ex.GetType().Name,
                    errorMessage: ex.Message);
                await PersistAsync(recipe, run, RunStatus.Failed).ConfigureAwait(false);
            }
        }
        finally
        {
            stall?.Dispose();
            awake?.Dispose();
            if (tracked)
            {
                _tracker?.End(recipe.Id);
            }
        }
    }

    private static IEnumerable<(string Url, string Title)> ApplyTakeMode(
        IReadOnlyList<(string Url, string Title)> items, RecipeStep step) => step.TakeMode switch
    {
        TakeMode.SingleTopStory => items.Take(1),
        TakeMode.TopN => items.Take(step.TakeCount ?? items.Count),
        _ => items,
    };

    /// <summary>Best-effort host for a human diagnostic (falls back to the raw url).</summary>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>
    /// What the stall watchdog compares: only things that mean the run got somewhere — the
    /// phase, the story, the narrated part, the percentage, assembled segments, uploaded
    /// bytes. Elapsed time, messages and retry counters change without anything advancing.
    /// </summary>
    /// <summary>The narration service's estimate in words ("3 h 20 min", "45 min"); empty without one.</summary>
    private static string TimeLeft(TimeSpan? remaining)
    {
        if (remaining is not { } left || left < TimeSpan.FromSeconds(45))
        {
            return string.Empty;
        }

        var minutes = (int)Math.Round(left.TotalMinutes);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
    }

    /// <summary>A page load fails for a network reason when the browser says so: its own error code or a timeout.</summary>
    private static bool LooksLikeNetworkTrouble(string? reason) =>
        reason is not null
        && (reason.Contains("net::ERR_", StringComparison.OrdinalIgnoreCase) || reason.Contains("Timeout", StringComparison.OrdinalIgnoreCase));

    /// <summary>The pause between tries of a page that would not load; <c>WIRECOPY_ROUTINE_LOAD_RETRY_SECONDS</c> overrides it.</summary>
    private static TimeSpan LoadRetryDelay() =>
        double.TryParse(
            Environment.GetEnvironmentVariable("WIRECOPY_ROUTINE_LOAD_RETRY_SECONDS"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(20);

    private static string ProgressPicture(PodcastProgress p) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{p.Phase} {p.CurrentArticle}/{p.TotalArticles} {p.IsArticleComplete} {p.CurrentArticleChunkIndex}/{p.CurrentArticleChunkTotal} {p.PercentComplete}% {p.AssembledSegments} {p.UploadedEpisodes} {p.UploadedBytes} {p.WaitingBehind} {p.QueueDone}");

    // Warnings and worse always; Information only from the scheduler and podcast code, so the drill-down
    // is not buried in unrelated prefetch chatter from the same seconds.
    private static bool KeepInRunLog(LogRecord r) =>
        r.Level >= LogSeverity.Warning ||
        (r.SourceContext?.StartsWith("WireCopy.Infrastructure.Scheduling", StringComparison.Ordinal) ?? false) ||
        (r.SourceContext?.StartsWith("WireCopy.Infrastructure.Podcast", StringComparison.Ordinal) ?? false);

    private void BeginTracking(ScheduleRecipe recipe, ScheduledRun run, CancellationTokenSource runCts)
    {
        if (_tracker is null)
        {
            return;
        }

        var stages = new List<RoutineRunStage> { new() { Key = "narration", Title = "NARRATION", State = RoutineRunStageState.Waiting } };
        var index = 0;
        foreach (var step in recipe.Steps)
        {
            stages.Add(new RoutineRunStage
            {
                Key = $"step:{index++}",
                Title = step.SectionName.ToUpperInvariant(),
                State = RoutineRunStageState.Waiting,
                Hint = HostOf(step.SourceUrl),
            });
        }

        stages.Add(new RoutineRunStage { Key = "narrate", Title = "NARRATE", State = RoutineRunStageState.Waiting });
        stages.Add(new RoutineRunStage
        {
            Key = "publish",
            Title = "PUBLISH",
            State = RoutineRunStageState.Waiting,
            Hint = $"feed “{recipe.OutputCollectionName}”",
        });
        _tracker.Begin(
            new RoutineRunProgress
            {
                RecipeId = recipe.Id,
                RunId = run.Id,
                RoutineName = recipe.Name,
                StartedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(run.StartedAtUtc, DateTimeKind.Utc)),
                Stages = stages,
            },
            runCts);
    }

    private string NarratedHint(Guid recipeId, int reportedStories)
    {
        var stories = reportedStories > 0 ? reportedStories : (_tracker?.Get(recipeId)?.StoriesTotal ?? 0);
        return stories > 0 ? $"{stories} stories" : "narrated";
    }

    private void ReportStage(Guid recipeId, PodcastProgress p)
    {
        if (_tracker is null)
        {
            return;
        }

        switch (p.Phase)
        {
            case PodcastPhase.CachingContent:
                if (p.TotalArticles > 0 && p.CurrentArticle > 0)
                {
                    var reading = string.IsNullOrWhiteSpace(p.ArticleTitle) ? null : $"“{p.ArticleTitle}”";
                    _tracker.SetStage(recipeId, "narrate", RoutineRunStageState.Running, $"reading story {p.CurrentArticle} of {p.TotalArticles}", reading);
                    _tracker.Update(recipeId, r => r with { StoriesDone = p.CurrentArticle, StoriesTotal = p.TotalArticles });
                }
                else
                {
                    _tracker.SetStage(recipeId, "narrate", RoutineRunStageState.Running, "reading the stories");
                }

                break;
            case PodcastPhase.GeneratingAudio:
                if (p.EngineNotAnswering)
                {
                    // workspace-wxll0.4: the narrator waits out an engine that went quiet; say so.
                    _tracker.SetStage(recipeId, "narrate", RoutineRunStageState.Running, "audio-engine is not answering");
                }
                else if (p.WaitingBehind is not null)
                {
                    // workspace-wxll0.5: the engine's one lane is on another job; this run's waits its turn.
                    _tracker.SetStage(
                        recipeId,
                        "narrate",
                        RoutineRunStageState.Running,
                        p.QueueTotal > 0 ? $"{WaitingTurnHint} · {p.QueueDone}/{p.QueueTotal}" : WaitingTurnHint,
                        $"“{p.WaitingBehind}”");
                }
                else if (p.TotalArticles > 0 && p.CurrentArticle > 0)
                {
                    var narratingTitle = string.IsNullOrWhiteSpace(p.ArticleTitle) ? null : $"“{p.ArticleTitle}”";

                    // The tile shows about 34 characters: with the engine's estimate the line drops "narrating".
                    var left = TimeLeft(p.Remaining);
                    var narrating = left.Length == 0
                        ? $"narrating story {p.CurrentArticle} of {p.TotalArticles}"
                        : $"story {p.CurrentArticle} of {p.TotalArticles} · {left} left";
                    _tracker.SetStage(recipeId, "narrate", RoutineRunStageState.Running, narrating, narratingTitle);
                    _tracker.Update(recipeId, r => r with { StoriesDone = p.CurrentArticle, StoriesTotal = p.TotalArticles });
                }
                else if (p.CurrentArticleChunkTotal > 0)
                {
                    var part = Math.Min(p.CurrentArticleChunkIndex + 1, p.CurrentArticleChunkTotal);
                    _tracker.SetStage(recipeId, "narrate", RoutineRunStageState.Running, $"narrating · part {part} of {p.CurrentArticleChunkTotal}");
                }
                else if (p.PercentComplete > 0)
                {
                    _tracker.SetStage(recipeId, "narrate", RoutineRunStageState.Running, $"narrating · {p.PercentComplete}%");
                }
                else
                {
                    _tracker.SetStage(recipeId, "narrate", RoutineRunStageState.Running, "narrating the stories");
                }

                break;
            case PodcastPhase.AssemblingAudio:
                _tracker.SetStage(recipeId, "narrate", RoutineRunStageState.Running, "putting the episode together");
                break;
            case PodcastPhase.Publishing:
                _tracker.SetStage(recipeId, "narrate", RoutineRunStageState.Done, NarratedHint(recipeId, p.TotalArticles));
                _tracker.SetStage(recipeId, "publish", RoutineRunStageState.Running, "uploading");
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// workspace-wxll0.5 — a run replaces the routine's earlier episodes that audio-engine is still
    /// narrating. A run that was cut off (the app quit or died, the engine was restarted) leaves its
    /// job behind; the engine narrates one job at a time, so the new episode would wait hours for
    /// an old one nobody collects. Other jobs in the engine are not touched. Never fails the run.
    /// </summary>
    private async Task SupersedeEarlierEpisodesAsync(ScheduleRecipe recipe, string? listId, CancellationToken cancellationToken)
    {
        if (_narrator is null)
        {
            return;
        }

        try
        {
            var series = new NarrationSeries($"routine:{recipe.Id}", listId, recipe.OutputCollectionName + " · ");
            await _narrator.SupersedeEarlierEpisodesAsync(series, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not clear this routine's earlier narrations out of audio-engine");
        }
    }

    private async Task<string> EpisodeNameAsync(ScheduleRecipe recipe, ScheduledRun run, CancellationToken cancellationToken)
    {
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(run.StartedAtUtc, DateTimeKind.Utc), TimeZoneInfo.Local);
        var name = $"{recipe.OutputCollectionName} · {localStart.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture)}";
        var recent = await _runRepository.GetRecentForRecipeAsync(recipe.Id, 50, cancellationToken).ConfigureAwait(false);
        var sameDay = recent.Count(r =>
            r.Id != run.Id
            && (r.Status is ScheduledRunStatus.Completed or ScheduledRunStatus.PartialSuccess)
            && TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.StartedAtUtc, DateTimeKind.Utc), TimeZoneInfo.Local).Date == localStart.Date);
        return sameDay > 0 ? $"{name} ({sameDay + 1})" : name;
    }

    /// <summary>
    /// The run's one load of a page. A good load is kept for the other steps that read the same
    /// page; a load that failed for a network reason (the machine just woke, the connection is not
    /// up yet) is tried again a few times before the step is given up (workspace-wxll0.3).
    /// </summary>
    private async Task<UnattendedSectionLoad> LoadPageOnceAsync(
        string url, Dictionary<string, UnattendedSectionLoad> pages, RunStallWatchdog stall, CancellationToken cancellationToken)
    {
        if (pages.TryGetValue(url, out var kept))
        {
            return kept;
        }

        var retryDelay = LoadRetryDelay();
        var load = await _loader.LoadLinksAndConfigAsync(url, cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; attempt < LoadAttempts; attempt++)
        {
            if (load.Outcome != LoadOutcome.LoadFailed || !LooksLikeNetworkTrouble(load.FailureReason))
            {
                break;
            }

            _logger.LogInformation(
                "Couldn't load {Host:l} (try {Attempt} of {Attempts}): {Reason:l} - trying again in {Seconds:F0} s",
                HostOf(url),
                attempt,
                LoadAttempts,
                load.FailureReason,
                retryDelay.TotalSeconds);
            stall.Report($"load retry {url} {attempt}");
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            load = await _loader.LoadLinksAndConfigAsync(url, cancellationToken).ConfigureAwait(false);
        }

        if (load.Outcome == LoadOutcome.Ok)
        {
            pages[url] = load;
        }

        return load;
    }

    /// <summary>
    /// Loads + resolves one step, appending its (deduped) items to <paramref name="assembled"/>.
    /// Returns the run-time status string, the section match count, a diagnostic, and the heading
    /// the section is printed under today when that is not its own name.
    /// </summary>
    private async Task<(string Status, int MatchCount, string? Diagnostic, string? TodaysHeading)> ResolveStepAsync(
        RecipeStep step,
        List<(string Url, string Title)> assembled,
        HashSet<string> seen,
        Dictionary<string, UnattendedSectionLoad> pages,
        RunStallWatchdog stall,
        CancellationToken cancellationToken)
    {
        var load = await LoadPageOnceAsync(step.SourceUrl, pages, stall, cancellationToken).ConfigureAwait(false);
        if (load.Outcome != LoadOutcome.Ok)
        {
            // workspace-frpl.11 (B8): a Blocked load is a logged-out/paywalled session,
            // not a transient error — give the user an actionable, human diagnostic
            // (surfaced by B11) instead of producing a silent empty episode. We never
            // attempt unattended re-auth; the user refreshes by browsing the site once.
            string diagnostic;
            if (load.Outcome == LoadOutcome.Blocked)
            {
                diagnostic = $"{HostOf(step.SourceUrl)}: appears logged-out or paywalled — open it in WireCopy and sign in to refresh your session. " +
                    "The routine run skipped this step rather than publish an empty episode.";
            }
            else if (string.IsNullOrWhiteSpace(load.FailureReason))
            {
                diagnostic = $"Background load failed ({load.Outcome}) for {step.SourceUrl}.";
            }
            else
            {
                diagnostic = $"Couldn't load {HostOf(step.SourceUrl)}: {load.FailureReason}";
            }

            return (load.Outcome.ToString(), 0, diagnostic, null);
        }

        // workspace-42q8.1: resolve the config by the step's DURABLE key
        // (ConfigUrlPattern) first, with the adapter's URL-matched config as the
        // legacy fallback — and when nothing resolves, say the truthful thing:
        // "the site's saved layout no longer covers this URL" is actionable in a
        // way the old blanket "no saved layout" (often a lie) was not.
        var lookup = _configStore != null
            ? await ScheduleConfigResolution.ForStepAsync(_configStore, step, load.FinalUrl).ConfigureAwait(false)
            : null;
        var config = lookup?.Config ?? load.Config;
        if (config is null)
        {
            var diagnostic = lookup is { SiteHasAnyConfig: true }
                ? $"The saved layout for {HostOf(step.SourceUrl)} (covers {ScheduleConfigResolution.DescribeSitePatterns(lookup.SiteConfigs)}) " +
                  $"no longer matches {step.SourceUrl} — re-add this step in :routines to re-pin it"
                : $"No saved layout for {HostOf(step.SourceUrl)} yet — set the site up once (g l) so routine runs can resolve sections";
            return (ResolutionStatus.SectionNotFound.ToString(), 0, diagnostic, null);
        }

        var resolution = _resolver.Resolve(config, load.Links, step);

        // workspace-frpl.10 (B9b): when the durable match AND B9a's deterministic
        // re-derivation both yielded 0, try the budgeted semantic recovery tier as a
        // last resort before skipping. It returns a Recovered resolution only when a
        // single high-confidence model classification ALSO passes a self-test; null
        // otherwise (the loud ZeroMatch then stands and the quality floor handles it).
        if (resolution.Status == ResolutionStatus.ZeroMatch && _semanticRecovery != null)
        {
            var recovered = await _semanticRecovery
                .TryRecoverAsync(config, load.Links, step, cancellationToken)
                .ConfigureAwait(false);
            if (recovered != null)
            {
                resolution = recovered;
            }
        }

        // Resolved AND Recovered (B9a re-derivation or B9b semantic recovery) both
        // carry items; any other status contributed nothing this occurrence.
        if (resolution.Status is not (ResolutionStatus.Resolved or ResolutionStatus.Recovered))
        {
            return (resolution.Status.ToString(), resolution.MatchCount, resolution.Diagnostic, null);
        }

        // first-wins cross-step URL dedup (seen.Add is the side-effecting filter).
        foreach (var item in ApplyTakeMode(resolution.Items, step).Where(item => seen.Add(item.Url)))
        {
            assembled.Add(item);
        }

        return (resolution.Status.ToString(), resolution.MatchCount, resolution.Diagnostic, resolution.TodaysHeading);
    }

    private async Task GenerateAndFinalizeAsync(
        ScheduleRecipe recipe,
        ScheduledRun run,
        IReadOnlyList<(string Url, string Title)> assembled,
        IReadOnlyList<RunStepOutcome> outcomes,
        string outcomesJson,
        RunStallWatchdog stall,
        CancellationToken cancellationToken)
    {
        // UNIQUE transient collection name: embed the occurrence so neither the
        // orchestrator's name-keyed article cache NOR its output path can collide
        // with a previous occurrence or a manual run of the same name.
        var collection = Collection.Create(await EpisodeNameAsync(recipe, run, cancellationToken).ConfigureAwait(false));
        collection.AddItemsAtEnd(assembled);

        // Again now that the episode has its id: an engine this run had to start has picked its
        // unfinished jobs back up, and this episode's own job is marked with the routine.
        await SupersedeEarlierEpisodesAsync(recipe, collection.Id.ToString(), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Generating podcast from {Count} articles", assembled.Count);
        var targets = await _orchestrator.ResolveTargetsAsync(collection, cancellationToken).ConfigureAwait(false);

        PodcastResult result;
        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // In order, on the reporting thread: Progress<T> hands its callbacks to the thread pool
            // with no ordering, so an older picture could land after a newer one - the run view
            // then showed a stale line, and the next (unchanged) report looked like progress and
            // gave a standing run a fresh window (workspace-wxll0.6).
            var progress = new SyncProgress<PodcastProgress>(p =>
            {
                _jobManager.ReportProgress(p);
                ReportStage(recipe.Id, p);

                stall.Report(
                    ProgressPicture(p),
                    p.Phase == PodcastPhase.GeneratingAudio ? stall.Window * NarrationPatience : stall.Window);
            });
            var generationTask = _orchestrator.GeneratePodcastAsync(collection, progress, generationCts.Token);

            // Surface the scheduled generation on the same status-bar/launcher badge the
            // manual flow uses, so foreground article-extraction jank is explained. The
            // gate (held by the caller) guarantees no manual job is active, but guard
            // defensively against a stale registration.
            if (!_jobManager.HasActiveJob)
            {
                _jobManager.StartJob(collection, targets, generationTask, generationCts);
            }

            result = await generationTask.ConfigureAwait(false);
        }
        finally
        {
            _jobManager.Clear();
        }

        if (!result.Success)
        {
            run.Finish(
                ScheduledRunStatus.Failed,
                itemCount: assembled.Count,
                targetLocalPath: result.LocalFilePath ?? targets.LocalFilePath,
                targetFeedUrl: result.FeedUrl ?? targets.FeedUrl,
                stepOutcomesJson: outcomesJson,
                errorClass: "GenerationFailed",
                errorMessage: result.ErrorMessage);
            await PersistAsync(recipe, run, RunStatus.Failed).ConfigureAwait(false);
            return;
        }

        // Past the quality floor every Required step resolved, so any degraded
        // (optional) step makes this a PartialSuccess; otherwise a clean Completed.
        _tracker?.SetStage(recipe.Id, "narrate", RoutineRunStageState.Done, NarratedHint(recipe.Id, 0));
        _tracker?.SetStage(recipe.Id, "publish", RoutineRunStageState.Done, result.FeedUrl is null ? "saved on this computer" : "published");
        var degraded = outcomes.Any(o => o.Status != ResolutionStatus.Resolved.ToString());
        var scheduledStatus = degraded ? ScheduledRunStatus.PartialSuccess : ScheduledRunStatus.Completed;
        var runStatus = degraded ? RunStatus.PartialSuccess : RunStatus.Success;

        run.Finish(
            scheduledStatus,
            itemCount: assembled.Count,
            targetLocalPath: result.LocalFilePath ?? targets.LocalFilePath,
            targetFeedUrl: result.FeedUrl,
            stepOutcomesJson: outcomesJson,
            errorMessage: result.FeedUrl is null ? result.PublicationNote : null);
        await PersistAsync(recipe, run, runStatus).ConfigureAwait(false);
    }

    /// <summary>Says how long the run stood still and what it was doing, for the run's own record.</summary>
    private string StalledMessage(Guid recipeId, TimeSpan window)
    {
        var stage = _tracker?.Get(recipeId)?.Stages.LastOrDefault(s => s.State == RoutineRunStageState.Running);
        string? doing = null;
        if (stage is not null)
        {
            if (stage.Key.StartsWith("step:", StringComparison.Ordinal))
            {
                doing = $"loading {stage.Hint ?? stage.Title}";
            }
            else if (stage.Key == "narration")
            {
                doing = "checking audio-engine";
            }
            else if (stage.Key == "narrate" && (stage.Hint?.StartsWith(WaitingTurnHint, StringComparison.Ordinal) ?? false))
            {
                doing = $"waiting for audio-engine to finish another narration ({stage.Detail ?? "another job"}), which was not getting anywhere";
            }
            else
            {
                doing = string.IsNullOrWhiteSpace(stage.Hint) ? stage.Title.ToLowerInvariant() : stage.Hint;
            }
        }

        var howLong = RunStallWatchdog.Describe(window);
        return doing is null
            ? $"No progress for {howLong}, so the run was stopped."
            : $"No progress for {howLong} — stopped while {doing}.";
    }

    private async Task MarkInterruptedAsync(ScheduleRecipe recipe, ScheduledRun run, bool userStopped, string? stalledMessage)
    {
        if (run.Status is not (ScheduledRunStatus.Running or ScheduledRunStatus.Pending))
        {
            return;
        }

        if (userStopped)
        {
            run.MarkCancelled("You stopped this run");
            run.Acknowledge();
        }
        else
        {
            run.MarkInterrupted(stalledMessage ?? "Run cancelled (the app was closing)");
        }

        try
        {
            await PersistAsync(recipe, run, userStopped ? RunStatus.Stopped : RunStatus.Failed).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The scope/context may already be torn down during shutdown; the
            // startup orphan sweep is the backstop for any still-Running row.
            _logger.LogWarning(ex, "Recipe {Recipe}: could not persist interrupted run", recipe.Name);
        }
    }

    /// <summary>
    /// Persists the finalized run (EF change-tracking handles the mutation; the
    /// explicit UpdateAsync keeps the repository contract symmetric for test doubles)
    /// and best-effort updates the recipe's convenience run-state cache. Uses
    /// <see cref="CancellationToken.None"/> so finalization still records even when
    /// the run was cancelled.
    /// </summary>
    private async Task PersistAsync(ScheduleRecipe recipe, ScheduledRun run, RunStatus cacheStatus)
    {
        // The status goes in as a string: ":l" on the enum itself reaches Enum.ToString("l"),
        // which throws inside the log sink after "Routine run finished: " is already written,
        // so the line said nothing and never ended (workspace-q06ia.2).
        _logger.LogInformation(
            "Routine run finished: {Status:l}{Error:l}",
            run.Status.ToString(),
            string.IsNullOrEmpty(run.ErrorMessage) ? string.Empty : " - " + run.ErrorMessage);
        WriteRunLog(run);
        await _runRepository.UpdateAsync(run, CancellationToken.None).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            var localDate = DateOnly.FromDateTime(run.StartedAtUtc);
            await _scheduleStore.UpdateRunStateAsync(recipe.Id, new RecipeRunState
            {
                LastRunLocalDate = localDate,
                LastRunOccurrenceKey = run.OccurrenceKey,
                LastStatus = cacheStatus,
                LastRunStartedAtUtc = run.StartedAtUtc,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recipe {Recipe}: run-state cache update failed (non-fatal)", recipe.Name);
        }
    }

    private void WriteRunLog(ScheduledRun run)
    {
        if (_logBuffer == null)
        {
            return;
        }

        try
        {
            var since = new DateTimeOffset(DateTime.SpecifyKind(run.StartedAtUtc, DateTimeKind.Utc));
            RoutineRunLog.Write(run.Id, _logBuffer.Snapshot().Where(r => r.Timestamp >= since && KeepInRunLog(r)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Writing the routine run log failed (non-fatal)");
        }
    }
}
