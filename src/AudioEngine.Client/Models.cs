using System.Text.Json.Serialization;

namespace AudioEngine.Client;

/// <summary>One chapter of a multi-chapter job (for example one article of a reading list).</summary>
public sealed record ChapterInput(string Title, string Text);

/// <summary>Engine-specific knobs. Everything is optional; unset fields are omitted from the request.</summary>
public sealed record JobOptions
{
    public string? Voice { get; init; }
    public string? Model { get; init; }
    public string? Instructions { get; init; }
    public double? Speed { get; init; }
    public double? Exaggeration { get; init; }
    public string? VoiceSample { get; init; }
}

/// <summary>Body of <c>POST /v1/jobs</c>. Set either <see cref="Text"/> or <see cref="Chapters"/>.</summary>
public sealed record JobRequest
{
    public string? Title { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<ChapterInput>? Chapters { get; init; }

    /// <summary>Engine id (for example <c>openai</c>); the server default when null.</summary>
    public string? Engine { get; init; }
    public string? Voice { get; init; }

    /// <summary>mp3, aac, wav, opus or m4b.</summary>
    public string? Format { get; init; }
    public JobOptions? Options { get; init; }
    public int? Priority { get; init; }

    /// <summary>Submitting the same key twice returns the original job instead of creating another.</summary>
    public string? IdempotencyKey { get; init; }
    public string? CallbackUrl { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Audiobook tags written into the output file.</summary>
    public string? Author { get; init; }
    public string? Album { get; init; }
    public string? Description { get; init; }
    public string? Genre { get; init; }

    /// <summary>Base64 or a <c>data:image/jpeg|png;base64,...</c> URI (JPEG/PNG, at most 10 MB). See <see cref="CoverArtFromFile"/>.</summary>
    public string? CoverArt { get; init; }

    /// <summary><c>fail</c> (server default) fails the job on a chapter error; <c>skip</c> skips that chapter and lists it in <see cref="Job.SkippedChapters"/>.</summary>
    public string? OnChapterError { get; init; }

    /// <summary>Build a <see cref="CoverArt"/> data URI from image bytes (<c>image/jpeg</c> or <c>image/png</c>).</summary>
    public static string CoverArtFromBytes(byte[] bytes, string contentType) =>
        $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";

    /// <summary>Read a JPEG or PNG file into a <see cref="CoverArt"/> data URI (type detected from the file's magic bytes).</summary>
    public static string CoverArtFromFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var type = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg"
            : bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 ? "image/png"
            : throw new ArgumentException("Cover art must be a JPEG or PNG file.", nameof(path));
        return CoverArtFromBytes(bytes, type);
    }

    public static JobRequest FromText(string text, string? title = null, string? format = null) =>
        new() { Text = text, Title = title, Format = format };
}

/// <summary>Response of <c>POST /v1/jobs</c>.</summary>
public sealed record JobCreated(string Id, string Status, DateTimeOffset CreatedAt)
{
    /// <summary>True when the server answered 200 because the idempotency key matched an existing job.</summary>
    [JsonIgnore]
    public bool Replayed { get; init; }
}

/// <summary>Job status strings as the server emits them.</summary>
public static class JobStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";

    /// <summary>True for succeeded, failed, cancelled and interrupted (no further progress without a retry).</summary>
    public static bool IsTerminal(string? status) =>
        status is Succeeded or Failed or Cancelled or Interrupted;
}

/// <summary>A job as returned by <c>GET /v1/jobs/{id}</c>.</summary>
public sealed record Job
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Phase { get; init; }
    public double Progress { get; init; }
    public int ChunksDone { get; init; }
    public int ChunksTotal { get; init; }
    public string? Title { get; init; }
    public string? Engine { get; init; }
    public string? Voice { get; init; }
    public string? Format { get; init; }
    public int Priority { get; init; }
    public int Attempts { get; init; }
    public string? Error { get; init; }
    public decimal EstCostUsd { get; init; }
    public double? DurationSeconds { get; init; }
    public long? Bytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public string? AudioUrl { get; init; }
    public string? TextPreview { get; init; }
    public string? Author { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public string? Description { get; init; }
    public bool HasCoverArt { get; init; }

    /// <summary>Chapters skipped under <c>OnChapterError = "skip"</c>; null when none.</summary>
    public IReadOnlyList<SkippedChapter>? SkippedChapters { get; init; }

    [JsonIgnore]
    public bool IsTerminal => JobStatuses.IsTerminal(Status);

    [JsonIgnore]
    public bool Succeeded => Status == JobStatuses.Succeeded;
}

/// <summary>Response of <c>GET /v1/jobs</c>. Pass <see cref="NextCursor"/> back as <c>cursor</c> for the next page.</summary>
public sealed record JobList(IReadOnlyList<Job> Items, string? NextCursor);

/// <summary>One server-sent event (also the webhook payload shape): a snapshot or progress update for a job.</summary>
public sealed record JobEvent
{
    public string JobId { get; init; } = "";

    /// <summary>job.snapshot, job.started, job.progress, job.succeeded, job.failed or job.cancelled.</summary>
    public string Type { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Phase { get; init; }
    public double Progress { get; init; }
    public int ChunksDone { get; init; }
    public int ChunksTotal { get; init; }
    public string? Message { get; init; }

    /// <summary>0-based chapter being narrated (synthesizing progress events only; null otherwise).</summary>
    public int? ChapterIndex { get; init; }
    public string? ChapterTitle { get; init; }
    public int? ChaptersTotal { get; init; }
    public DateTimeOffset? Timestamp { get; init; }

    [JsonIgnore]
    public bool IsTerminal => JobStatuses.IsTerminal(Status);
}

/// <summary>A chapter that failed and was skipped (<c>OnChapterError = "skip"</c>).</summary>
public sealed record SkippedChapter(int Index, string? Title, string? Error);

/// <summary>Response of <c>POST /v1/jobs/estimate</c>: cost, cache hits and duration without creating a job.</summary>
public sealed record JobEstimate
{
    public string Engine { get; init; } = "";
    public string Format { get; init; } = "";
    public bool EngineConfigured { get; init; }

    /// <summary>Cost of the chunks not already cached.</summary>
    public decimal EstimateUsd { get; init; }
    public decimal FullEstimateUsd { get; init; }
    public int ChunksTotal { get; init; }
    public int ChunksCached { get; init; }
    public int Characters { get; init; }
    public double EstimatedDurationSeconds { get; init; }
    public IReadOnlyList<ChapterEstimate> Chapters { get; init; } = [];
    public EstimateBudget? Budget { get; init; }
}

/// <summary>Per-chapter part of a <see cref="JobEstimate"/>. A chapter is fully cached when ChunksCached == ChunksTotal.</summary>
public sealed record ChapterEstimate
{
    public int Index { get; init; }
    public string? Title { get; init; }
    public int ChunksTotal { get; init; }
    public int ChunksCached { get; init; }
    public int Characters { get; init; }
    public decimal EstimateUsd { get; init; }
    public double EstimatedDurationSeconds { get; init; }
}

/// <summary>Whether submitting would be allowed under the spend budget (false means the submit would get a 402).</summary>
public sealed record EstimateBudget(bool Allowed, string? Reason, decimal? LimitUsd);
