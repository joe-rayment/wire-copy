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

    private readonly IUnattendedSectionLoader _loader;
    private readonly ISectionResolver _resolver;
    private readonly IPodcastOrchestrator _orchestrator;
    private readonly IPodcastBackgroundJobManager _jobManager;
    private readonly IScheduledRunRepository _runRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IScheduleStore _scheduleStore;
    private readonly ILogger<RecipeRunPipeline> _logger;
    private readonly ISemanticSectionRecovery? _semanticRecovery;
    private readonly IOptions<GcsConfiguration>? _gcsOptions;
    private readonly IUserSettingsStore? _settingsStore;
    private readonly IHierarchyConfigStore? _configStore;

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
        IOptions<GcsConfiguration>? gcsOptions = null,
        IUserSettingsStore? settingsStore = null,
        IHierarchyConfigStore? configStore = null)
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
        _gcsOptions = gcsOptions;
        _settingsStore = settingsStore;
        _configStore = configStore;
    }

    public async Task RunAsync(ScheduleRecipe recipe, ScheduledRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(run);

        try
        {
            var assembled = new List<(string Url, string Title)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outcomes = new List<RunStepOutcome>();

            foreach (var step in recipe.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = assembled.Count;
                var (status, matchCount, diagnostic) =
                    await ResolveStepAsync(step, assembled, seen, cancellationToken).ConfigureAwait(false);
                outcomes.Add(new RunStepOutcome
                {
                    SectionName = step.SectionName,
                    SourceUrl = step.SourceUrl,
                    Status = status,
                    Required = step.Required,
                    MatchCount = matchCount,
                    ItemsContributed = assembled.Count - before,
                    Diagnostic = diagnostic,
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

            await GenerateAndFinalizeAsync(recipe, run, assembled, outcomes, outcomesJson, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host shutdown or the per-run timeout fired. Mark Interrupted (terminal,
            // visible) rather than letting a bare OCE bubble up and tear down the
            // scheduler's tick loop. Best-effort persist on a fresh token.
            await MarkInterruptedAsync(recipe, run).ConfigureAwait(false);
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
    /// workspace-frpl.17 (B13): the manual flow bridges the user's GcsBucketName setting
    /// into the (config-bound) GcsConfiguration before publishing (PodcastCommandHandler);
    /// the scheduler/run-now path must do the SAME or scheduled runs publish to a null
    /// bucket. Idempotent + best-effort; the manual path already does this when active.
    /// </summary>
    private void BridgeGcsBucketFromSettings()
    {
        if (_gcsOptions?.Value is not { } gcs || _settingsStore is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(gcs.BucketName))
        {
            return;
        }

        var savedBucket = _settingsStore.Get("GcsBucketName");
        if (!string.IsNullOrWhiteSpace(savedBucket) && GcsConfiguration.IsValidBucketName(savedBucket))
        {
            gcs.BucketName = savedBucket;
        }
    }

    /// <summary>
    /// Loads + resolves one step, appending its (deduped) items to <paramref name="assembled"/>.
    /// Returns the run-time status string, the section match count, and a diagnostic.
    /// </summary>
    private async Task<(string Status, int MatchCount, string? Diagnostic)> ResolveStepAsync(
        RecipeStep step,
        List<(string Url, string Title)> assembled,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        var load = await _loader.LoadLinksAndConfigAsync(step.SourceUrl, cancellationToken).ConfigureAwait(false);
        if (load.Outcome != LoadOutcome.Ok)
        {
            // workspace-frpl.11 (B8): a Blocked load is a logged-out/paywalled session,
            // not a transient error — give the user an actionable, human diagnostic
            // (surfaced by B11) instead of producing a silent empty episode. We never
            // attempt unattended re-auth; the user refreshes by browsing the site once.
            var diagnostic = load.Outcome == LoadOutcome.Blocked
                ? $"{HostOf(step.SourceUrl)}: appears logged-out or paywalled — open it in WireCopy and sign in to refresh your session. " +
                  "The scheduled run skipped this step rather than publish an empty episode."
                : $"Background load failed ({load.Outcome}) for {step.SourceUrl}.";
            return (load.Outcome.ToString(), 0, diagnostic);
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
                  $"no longer matches {step.SourceUrl} — re-add this step in :schedules to re-pin it"
                : $"No saved layout for {HostOf(step.SourceUrl)} yet — set the site up once (g l) so scheduled runs can resolve sections";
            return (ResolutionStatus.SectionNotFound.ToString(), 0, diagnostic);
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
            return (resolution.Status.ToString(), resolution.MatchCount, resolution.Diagnostic);
        }

        // first-wins cross-step URL dedup (seen.Add is the side-effecting filter).
        foreach (var item in ApplyTakeMode(resolution.Items, step).Where(item => seen.Add(item.Url)))
        {
            assembled.Add(item);
        }

        return (resolution.Status.ToString(), resolution.MatchCount, resolution.Diagnostic);
    }

    private async Task GenerateAndFinalizeAsync(
        ScheduleRecipe recipe,
        ScheduledRun run,
        IReadOnlyList<(string Url, string Title)> assembled,
        IReadOnlyList<RunStepOutcome> outcomes,
        string outcomesJson,
        CancellationToken cancellationToken)
    {
        // UNIQUE transient collection name: embed the occurrence so neither the
        // orchestrator's name-keyed article cache NOR its output path can collide
        // with a previous occurrence or a manual run of the same name.
        var occurrenceToken = run.OccurrenceKey.Replace(':', '.');
        var collection = Collection.Create($"{recipe.OutputCollectionName} {occurrenceToken}");
        collection.AddItemsAtEnd(assembled);

        BridgeGcsBucketFromSettings();

        var targets = await _orchestrator.ResolveTargetsAsync(collection, cancellationToken).ConfigureAwait(false);

        PodcastResult result;
        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var progress = new Progress<PodcastProgress>(_jobManager.ReportProgress);
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
        var degraded = outcomes.Any(o => o.Status != ResolutionStatus.Resolved.ToString());
        var scheduledStatus = degraded ? ScheduledRunStatus.PartialSuccess : ScheduledRunStatus.Completed;
        var runStatus = degraded ? RunStatus.PartialSuccess : RunStatus.Success;

        run.Finish(
            scheduledStatus,
            itemCount: assembled.Count,
            targetLocalPath: result.LocalFilePath ?? targets.LocalFilePath,
            targetFeedUrl: result.FeedUrl ?? targets.FeedUrl,
            stepOutcomesJson: outcomesJson);
        await PersistAsync(recipe, run, runStatus).ConfigureAwait(false);
    }

    private async Task MarkInterruptedAsync(ScheduleRecipe recipe, ScheduledRun run)
    {
        if (run.Status is not (ScheduledRunStatus.Running or ScheduledRunStatus.Pending))
        {
            return;
        }

        run.MarkInterrupted("Run cancelled (host shutdown or per-run timeout)");
        try
        {
            await PersistAsync(recipe, run, RunStatus.Failed).ConfigureAwait(false);
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
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recipe {Recipe}: run-state cache update failed (non-fatal)", recipe.Name);
        }
    }
}
