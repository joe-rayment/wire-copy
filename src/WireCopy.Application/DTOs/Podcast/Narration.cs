// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Why a narration call did not succeed. Lets the UI and <c>PodcastFailureClassifier</c> route
/// a failure without parsing message text.
/// </summary>
public enum NarrationErrorKind
{
    /// <summary>No error.</summary>
    None,

    /// <summary>The audio-engine URL or API key has not been set.</summary>
    NotConfigured,

    /// <summary>audio-engine could not be reached (not running, wrong address).</summary>
    Unreachable,

    /// <summary>audio-engine rejected the API key (401/403).</summary>
    Unauthorized,

    /// <summary>The selected engine inside audio-engine has no credentials (for example no OpenAI key).</summary>
    EngineNotConfigured,

    /// <summary>audio-engine refused the job because its estimated cost is over the budget (402).</summary>
    BudgetExceeded,

    /// <summary>audio-engine rejected the request itself (409/422).</summary>
    Rejected,

    /// <summary>The job ran and failed inside audio-engine.</summary>
    Failed,

    /// <summary>The caller cancelled the narration.</summary>
    Cancelled,

    /// <summary>A retryable server-side or network failure (5xx, dropped connection).</summary>
    Transient,
}

/// <summary>Coarse stage of a narration job, mapped from audio-engine's job phase.</summary>
public enum NarrationPhase
{
    /// <summary>Waiting in audio-engine's queue.</summary>
    Queued,

    /// <summary>Turning chapter text into audio.</summary>
    Synthesizing,

    /// <summary>Joining chapters into the final file.</summary>
    Assembling,

    /// <summary>Finished.</summary>
    Done,
}

/// <summary>One chapter of a narration request (one article of a reading list).</summary>
/// <param name="Title">Chapter title shown in the audiobook's chapter list.</param>
/// <param name="Text">The text to speak.</param>
/// <param name="SourceUrl">The article URL, if any (informational).</param>
public sealed record NarrationChapter(string Title, string Text, string? SourceUrl = null);

/// <summary>Everything audio-engine needs to narrate one audiobook.</summary>
public sealed record NarrationRequest
{
    /// <summary>Gets the audiobook title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the author tag.</summary>
    public string? Author { get; init; }

    /// <summary>Gets the album tag.</summary>
    public string? Album { get; init; }

    /// <summary>Gets the description tag.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the genre tag.</summary>
    public string? Genre { get; init; }

    /// <summary>Gets the path of a JPEG or PNG cover image; ignored when the file does not exist.</summary>
    public string? CoverArtPath { get; init; }

    /// <summary>Gets the chapters, in order.</summary>
    public required IReadOnlyList<NarrationChapter> Chapters { get; init; }

    /// <summary>Gets the path the finished m4b is written to.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Gets the reading list id, recorded as job metadata and part of the idempotency key.</summary>
    public string? ListId { get; init; }
}

/// <summary>Cost and cache estimate for one chapter.</summary>
public sealed record NarrationChapterEstimate(int Index, string? Title, int ChunksTotal, int ChunksCached, decimal EstimateUsd);

/// <summary>What narrating a <see cref="NarrationRequest"/> would cost, without doing it.</summary>
public sealed record NarrationEstimate
{
    /// <summary>Gets a value indicating whether the estimate was obtained.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the user-facing failure message (starts with "audio-engine"); null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets the failure category.</summary>
    public NarrationErrorKind ErrorKind { get; init; }

    /// <summary>Gets the cost of the chunks not already cached in audio-engine.</summary>
    public decimal EstimateUsd { get; init; }

    /// <summary>Gets the cost with nothing cached.</summary>
    public decimal FullEstimateUsd { get; init; }

    /// <summary>Gets the estimated audio length in seconds.</summary>
    public double EstimatedDurationSeconds { get; init; }

    /// <summary>Gets a value indicating whether audio-engine's budget allows this job.</summary>
    public bool BudgetAllowed { get; init; } = true;

    /// <summary>Gets the reason the budget disallows the job, if it does.</summary>
    public string? BudgetReason { get; init; }

    /// <summary>Gets audio-engine's budget limit in USD, when one is set.</summary>
    public decimal? BudgetLimitUsd { get; init; }

    /// <summary>Gets a value indicating whether the engine runs on this machine (no per-character cost).</summary>
    public bool EngineIsLocal { get; init; }

    /// <summary>Gets the per-chapter breakdown.</summary>
    public IReadOnlyList<NarrationChapterEstimate> Chapters { get; init; } = [];
}

/// <summary>A progress update while a narration job runs.</summary>
public sealed record NarrationProgress
{
    /// <summary>Gets the coarse stage.</summary>
    public NarrationPhase Phase { get; init; }

    /// <summary>Gets overall completion from 0 to 1.</summary>
    public double Fraction { get; init; }

    /// <summary>Gets the 0-based chapter being narrated, when known.</summary>
    public int? ChapterIndex { get; init; }

    /// <summary>Gets the title of the chapter being narrated, when known.</summary>
    public string? ChapterTitle { get; init; }

    /// <summary>Gets the number of chapters, when known.</summary>
    public int? ChaptersTotal { get; init; }

    /// <summary>Gets the number of text chunks already synthesized.</summary>
    public int ChunksDone { get; init; }

    /// <summary>Gets the total number of text chunks.</summary>
    public int ChunksTotal { get; init; }

    /// <summary>Gets a free-text status line from audio-engine.</summary>
    public string? Message { get; init; }
}

/// <summary>A chapter audio-engine skipped because narrating it failed.</summary>
public sealed record NarrationSkippedChapter(int Index, string? Title, string? Error);

/// <summary>The outcome of a narration call.</summary>
public sealed record NarrationResult
{
    /// <summary>Gets a value indicating whether the m4b was produced and downloaded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets the path of the downloaded file; null on failure.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Gets the audio length.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Gets the size of the downloaded file in bytes.</summary>
    public long Bytes { get; init; }

    /// <summary>Gets the cost audio-engine recorded for the job.</summary>
    public decimal CostUsd { get; init; }

    /// <summary>Gets the chapters audio-engine skipped (the file was still produced).</summary>
    public IReadOnlyList<NarrationSkippedChapter> SkippedChapters { get; init; } = [];

    /// <summary>Gets the user-facing failure message (starts with "audio-engine"); null on success.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets the failure category.</summary>
    public NarrationErrorKind ErrorKind { get; init; }

    /// <summary>Gets the audio-engine job id, when a job was created.</summary>
    public string? JobId { get; init; }
}

/// <summary>One narration engine hosted by audio-engine.</summary>
public sealed record NarratorEngine(string Name, bool IsConfigured, IReadOnlyList<string> Voices, string? DefaultVoice);

/// <summary>Result of <see cref="Interfaces.Podcast.IPodcastNarrator.CheckAsync"/>: can narration run right now?</summary>
public sealed record NarratorStatus
{
    /// <summary>Gets the audio-engine address that was checked.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether audio-engine answered.</summary>
    public bool Reachable { get; init; }

    /// <summary>Gets a value indicating whether the API key was accepted.</summary>
    public bool Authorized { get; init; }

    /// <summary>Gets a value indicating whether every check passed and narration can start.</summary>
    public bool Ready { get; init; }

    /// <summary>Gets the engine the current settings select ("openai" or "chatterbox").</summary>
    public string EngineName { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the selected engine has its credentials inside audio-engine.</summary>
    public bool EngineConfigured { get; init; }

    /// <summary>Gets the engines audio-engine reports (empty when it could not be asked).</summary>
    public IReadOnlyList<NarratorEngine> Engines { get; init; } = [];

    /// <summary>Gets the user-facing message (starts with "audio-engine"); describes readiness or the first failure.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the failure category; <see cref="NarrationErrorKind.None"/> when ready.</summary>
    public NarrationErrorKind ErrorKind { get; init; }
}
