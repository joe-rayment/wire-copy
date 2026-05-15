// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.ValueObjects.Podcast;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Anonymous HTTP GET of a freshly published feed URL, to confirm the bytes
/// Apple Podcasts (and any other RSS reader) would see are reachable and
/// parseable (workspace-nb6b). Extracted to an interface so unit tests can
/// stub network behaviour without touching <see cref="HttpClient"/>.
/// </summary>
public interface IFeedReachabilityProbe
{
    Task<FeedReachabilityResult> CheckAsync(string feedUrl, CancellationToken cancellationToken);
}

/// <summary>
/// Result of a post-publish reachability probe.
/// </summary>
public record FeedReachabilityResult
{
    public required FeedPublishFailureClass FailureClass { get; init; }

    public required string Diagnostic { get; init; }

    public int? HttpStatusCode { get; init; }

    public string? ContentType { get; init; }

    public static FeedReachabilityResult Ok() => new()
    {
        FailureClass = FeedPublishFailureClass.None,
        Diagnostic = "OK",
    };
}
