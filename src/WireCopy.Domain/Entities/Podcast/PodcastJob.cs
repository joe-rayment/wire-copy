// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Podcast;

namespace WireCopy.Domain.Entities.Podcast;

/// <summary>
/// Persistent record of a podcast generation job — the durable counterpart
/// to the in-process <c>IPodcastBackgroundJobManager</c>. Lets background
/// generation survive app restart (workspace-ur0e Phase F).
/// </summary>
/// <remarks>
/// <para>
/// Created by <c>PodcastOrchestrator</c> on entry (status = <see cref="PodcastJobStatus.Running"/>),
/// updated as phases advance, and finalized on exit. If the row is still
/// <see cref="PodcastJobStatus.Running"/> at the next app launch the
/// <c>PodcastJobLifecycleService</c> marks it
/// <see cref="PodcastJobStatus.Interrupted"/> (F.2) — and, when F.4
/// lands, will optionally re-enqueue it for resume.
/// </para>
/// <para>
/// Only one job is intended to be Running at a time, matching the
/// single-active-job invariant on <c>IPodcastBackgroundJobManager</c>.
/// </para>
/// </remarks>
public class PodcastJob
{
    /// <summary>Stable identifier for this job row.</summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// The collection the job is generating from. Not a strict FK because
    /// the collection may legitimately be renamed or removed while the
    /// row sticks around as a history record; the orchestrator looks the
    /// collection up by id at resume time and fails the job if it is gone.
    /// </summary>
    public Guid CollectionId { get; private set; }

    /// <summary>
    /// Human-readable collection title at the moment the job was created.
    /// Snapshotted so the launcher badge does not need a join to render.
    /// </summary>
    public string CollectionTitle { get; private set; }

    /// <summary>Current lifecycle state.</summary>
    public PodcastJobStatus Status { get; private set; }

    /// <summary>Phase within Running.</summary>
    public PodcastJobPhase Phase { get; private set; }

    /// <summary>When the row was first written (job created).</summary>
    public DateTime StartedAtUtc { get; private set; }

    /// <summary>When a phase or progress update last touched this row.</summary>
    public DateTime LastProgressAtUtc { get; private set; }

    /// <summary>
    /// JSON snapshot of the most recent <c>PodcastProgress</c> DTO so the
    /// launcher badge can render details (current article index, ETA)
    /// without re-querying the orchestrator. Nullable — fresh rows have
    /// no snapshot yet.
    /// </summary>
    public string? LastProgressJson { get; private set; }

    /// <summary>
    /// Where the final M4B will land on disk. Captured at job-start so
    /// the badge can show the destination while generation is in flight.
    /// </summary>
    public string? TargetLocalPath { get; private set; }

    /// <summary>Where the feed.xml will be published (GCS public URL).</summary>
    public string? TargetFeedUrl { get; private set; }

    /// <summary>
    /// Typed error class when <see cref="Status"/> is one of the failure
    /// modes (Cancelled / PartialSuccess / TotalFailure / Interrupted).
    /// Free-form string so domain-side enum churn doesn't force migrations.
    /// </summary>
    public string? ErrorClass { get; private set; }

    /// <summary>Human-readable error message paired with <see cref="ErrorClass"/>.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// When the user last acknowledged the result of this job (closed the
    /// completion modal / pressed Continue). The launcher badge stops
    /// showing rows whose AcknowledgedAtUtc is non-null.
    /// </summary>
    public DateTime? AcknowledgedAtUtc { get; private set; }

    private PodcastJob(
        Guid collectionId,
        string collectionTitle,
        string? targetLocalPath,
        string? targetFeedUrl)
    {
        Id = Guid.NewGuid();
        CollectionId = collectionId;
        CollectionTitle = collectionTitle;
        Status = PodcastJobStatus.Running;
        Phase = PodcastJobPhase.NotStarted;
        StartedAtUtc = DateTime.UtcNow;
        LastProgressAtUtc = StartedAtUtc;
        TargetLocalPath = targetLocalPath;
        TargetFeedUrl = targetFeedUrl;
    }

    // EF Core constructor
    private PodcastJob()
    {
        CollectionTitle = string.Empty;
    }

    /// <summary>
    /// Creates a new Running job row.
    /// </summary>
    public static PodcastJob Start(
        Guid collectionId,
        string collectionTitle,
        string? targetLocalPath = null,
        string? targetFeedUrl = null)
    {
        if (string.IsNullOrWhiteSpace(collectionTitle))
        {
            throw new ArgumentException("Collection title cannot be empty", nameof(collectionTitle));
        }

        return new PodcastJob(collectionId, collectionTitle, targetLocalPath, targetFeedUrl);
    }

    /// <summary>
    /// Records progress within the current phase. Debounce upstream — this
    /// writes whatever you pass it. <paramref name="progressJson"/> may be
    /// <c>null</c> to clear; pass the serialized <c>PodcastProgress</c>
    /// otherwise.
    /// </summary>
    public void RecordProgress(PodcastJobPhase phase, string? progressJson)
    {
        Phase = phase;
        LastProgressJson = progressJson;
        LastProgressAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks the job as the given terminal state with optional typed error
    /// info. Refuses to move out of a terminal state — call sites should
    /// only invoke this once.
    /// </summary>
    public void Finish(PodcastJobStatus terminalStatus, string? errorClass = null, string? errorMessage = null)
    {
        if (terminalStatus == PodcastJobStatus.Running || terminalStatus == PodcastJobStatus.Pending)
        {
            throw new ArgumentException(
                $"Finish requires a terminal status, got {terminalStatus}",
                nameof(terminalStatus));
        }

        if (Status != PodcastJobStatus.Running && Status != PodcastJobStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Cannot finish job {Id}: already in terminal state {Status}");
        }

        Status = terminalStatus;
        Phase = PodcastJobPhase.Done;
        ErrorClass = errorClass;
        ErrorMessage = errorMessage;
        LastProgressAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Marks an orphaned Running row as Interrupted at app startup. Idempotent
    /// — silently no-ops on rows that are already terminal.
    /// </summary>
    public void MarkInterrupted(string reason)
    {
        if (Status != PodcastJobStatus.Running && Status != PodcastJobStatus.Pending)
        {
            return;
        }

        Status = PodcastJobStatus.Interrupted;
        ErrorClass = "Interrupted";
        ErrorMessage = reason;
        LastProgressAtUtc = DateTime.UtcNow;
    }

    /// <summary>Records that the user has seen the final result.</summary>
    public void Acknowledge()
    {
        AcknowledgedAtUtc ??= DateTime.UtcNow;
    }
}
