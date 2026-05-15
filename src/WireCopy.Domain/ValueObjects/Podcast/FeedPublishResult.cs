// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Podcast;

/// <summary>
/// Classifies how a feed publish operation failed so callers can render
/// targeted remediation instead of a generic error.
/// </summary>
public enum FeedPublishFailureClass
{
    /// <summary>Publish succeeded.</summary>
    None = 0,

    /// <summary>Every requested episode skipped because its local audio file was missing (workspace-mie2).</summary>
    NoAudioFiles,

    /// <summary>feed.xml uploaded, but the anonymous public GET returned a non-2xx status (workspace-nb6b).</summary>
    FeedNotReachable,

    /// <summary>feed.xml fetched OK but the response body is not parseable XML (workspace-nb6b).</summary>
    FeedNotParseable,

    /// <summary>Catch-all for anything that doesn't fit a specific class.</summary>
    Generic,
}

/// <summary>
/// Detail about an episode that was skipped during publish — captured so the
/// result screen can name what was lost (workspace-mie2).
/// </summary>
public record SkippedEpisodeDetail
{
    public required string Title { get; init; }

    public required string MissingPath { get; init; }

    public required string Reason { get; init; }
}

/// <summary>
/// Result of publishing a podcast feed to cloud storage.
/// </summary>
public record FeedPublishResult
{
    /// <summary>
    /// Gets whether the publish operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the public URL of the published RSS feed.
    /// </summary>
    public required string FeedUrl { get; init; }

    /// <summary>
    /// Gets the number of episodes included in the published feed.
    /// </summary>
    public required int EpisodesPublished { get; init; }

    /// <summary>
    /// Gets the optional error message if the publish failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the UTC timestamp when the feed was published.
    /// </summary>
    public required DateTime PublishedAtUtc { get; init; }

    /// <summary>
    /// Classifies the failure so callers can render targeted remediation.
    /// </summary>
    public FeedPublishFailureClass FailureClass { get; init; } = FeedPublishFailureClass.None;

    /// <summary>
    /// Episodes that were skipped during publish (their local audio file was missing).
    /// Empty for a successful run.
    /// </summary>
    public IReadOnlyList<SkippedEpisodeDetail> SkippedEpisodes { get; init; } = Array.Empty<SkippedEpisodeDetail>();

    /// <summary>
    /// Creates a successful publish result.
    /// </summary>
    public static FeedPublishResult Successful(string feedUrl, int episodesPublished)
    {
        return new FeedPublishResult
        {
            Success = true,
            FeedUrl = feedUrl,
            EpisodesPublished = episodesPublished,
            PublishedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Creates a successful publish result that nevertheless skipped some
    /// episodes because their audio files were missing. The publish still
    /// surfaces a result-screen warning so the user knows what didn't ship.
    /// </summary>
    public static FeedPublishResult Partial(
        string feedUrl,
        int episodesPublished,
        IReadOnlyList<SkippedEpisodeDetail> skipped)
    {
        return new FeedPublishResult
        {
            Success = true,
            FeedUrl = feedUrl,
            EpisodesPublished = episodesPublished,
            SkippedEpisodes = skipped,
            PublishedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Creates a failed publish result.
    /// </summary>
    public static FeedPublishResult Failure(string errorMessage)
    {
        return new FeedPublishResult
        {
            Success = false,
            FeedUrl = string.Empty,
            EpisodesPublished = 0,
            ErrorMessage = errorMessage,
            FailureClass = FeedPublishFailureClass.Generic,
            PublishedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Creates a typed failed publish result with a specific
    /// <see cref="FeedPublishFailureClass"/> so the result screen can render
    /// targeted remediation copy.
    /// </summary>
    public static FeedPublishResult Failure(
        string errorMessage,
        FeedPublishFailureClass failureClass,
        IReadOnlyList<SkippedEpisodeDetail>? skipped = null)
    {
        return new FeedPublishResult
        {
            Success = false,
            FeedUrl = string.Empty,
            EpisodesPublished = 0,
            ErrorMessage = errorMessage,
            FailureClass = failureClass,
            SkippedEpisodes = skipped ?? Array.Empty<SkippedEpisodeDetail>(),
            PublishedAtUtc = DateTime.UtcNow,
        };
    }
}
