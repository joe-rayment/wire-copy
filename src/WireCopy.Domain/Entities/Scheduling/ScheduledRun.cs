// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Scheduling;

namespace WireCopy.Domain.Entities.Scheduling;

/// <summary>
/// workspace-frpl.12 (B10) — the durable, AUTHORITATIVE record of one scheduled
/// recipe occurrence (the counterpart to PodcastJob for the scheduler). The
/// scheduler writes a Running row FIRST (crash-safe dedup: the presence of a row
/// for an occurrence key means it already fired), then the run pipeline finalizes
/// it to a terminal state. A row left Running across an app restart is swept to
/// <see cref="ScheduledRunStatus.Interrupted"/> at startup.
/// </summary>
public class ScheduledRun
{
    private ScheduledRun(Guid recipeId, string recipeName, string occurrenceKey)
    {
        Id = Guid.NewGuid();
        RecipeId = recipeId;
        RecipeName = recipeName;
        OccurrenceKey = occurrenceKey;
        Status = ScheduledRunStatus.Running;
        StartedAtUtc = DateTime.UtcNow;
    }

    // EF Core constructor.
    private ScheduledRun()
    {
        RecipeName = string.Empty;
        OccurrenceKey = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>The recipe this run is for. NOT a strict FK so history survives recipe deletion.</summary>
    public Guid RecipeId { get; private set; }

    /// <summary>Recipe name snapshot (so the badge renders without a join).</summary>
    public string RecipeName { get; private set; }

    /// <summary>The cadence occurrence key (yyyy-MM-dd@HH:mm) — the dedup key for this recipe.</summary>
    public string OccurrenceKey { get; private set; }

    public ScheduledRunStatus Status { get; private set; }

    public DateTime StartedAtUtc { get; private set; }

    public DateTime? FinishedAtUtc { get; private set; }

    /// <summary>How many articles the assembled episode contained.</summary>
    public int ItemCount { get; private set; }

    public string? TargetLocalPath { get; private set; }

    public string? TargetFeedUrl { get; private set; }

    /// <summary>Typed error class on a failure/skip (free-form, no migration on churn).</summary>
    public string? ErrorClass { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>JSON of the per-step outcomes (Resolved/Recovered/ZeroMatch/Blocked) for the UX.</summary>
    public string? StepOutcomesJson { get; private set; }

    /// <summary>When the user dismissed this run's result badge (B11). Null = still surfaced.</summary>
    public DateTime? AcknowledgedAtUtc { get; private set; }

    /// <summary>Creates a new Running row — written BEFORE the pipeline starts (dedup marker).</summary>
    public static ScheduledRun Start(Guid recipeId, string recipeName, string occurrenceKey)
    {
        if (string.IsNullOrWhiteSpace(recipeName))
        {
            throw new ArgumentException("Recipe name cannot be empty", nameof(recipeName));
        }

        if (string.IsNullOrWhiteSpace(occurrenceKey))
        {
            throw new ArgumentException("Occurrence key cannot be empty", nameof(occurrenceKey));
        }

        return new ScheduledRun(recipeId, recipeName, occurrenceKey);
    }

    /// <summary>Finalizes the run to a terminal state. Refuses to leave a terminal state.</summary>
    public void Finish(
        ScheduledRunStatus terminalStatus,
        int itemCount,
        string? targetLocalPath = null,
        string? targetFeedUrl = null,
        string? stepOutcomesJson = null,
        string? errorClass = null,
        string? errorMessage = null)
    {
        if (terminalStatus is ScheduledRunStatus.Running or ScheduledRunStatus.Pending)
        {
            throw new ArgumentException($"Finish requires a terminal status, got {terminalStatus}", nameof(terminalStatus));
        }

        if (Status is not (ScheduledRunStatus.Running or ScheduledRunStatus.Pending))
        {
            throw new InvalidOperationException($"Cannot finish run {Id}: already terminal ({Status})");
        }

        Status = terminalStatus;
        ItemCount = itemCount;
        TargetLocalPath = targetLocalPath;
        TargetFeedUrl = targetFeedUrl;
        StepOutcomesJson = stepOutcomesJson;
        ErrorClass = errorClass;
        ErrorMessage = errorMessage;
        FinishedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Marks an orphaned Running row Interrupted at startup. Idempotent on terminal rows.</summary>
    public void MarkInterrupted(string reason)
    {
        if (Status is not (ScheduledRunStatus.Running or ScheduledRunStatus.Pending))
        {
            return;
        }

        Status = ScheduledRunStatus.Interrupted;
        ErrorClass = "Interrupted";
        ErrorMessage = reason;
        FinishedAtUtc = DateTime.UtcNow;
    }

    public void Acknowledge() => AcknowledgedAtUtc ??= DateTime.UtcNow;
}
