// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Browser;

/// <summary>
/// Per-URL stages the background pre-loader moves through when caching a
/// page (workspace-7xw0). Exposed via <c>PreloadProgress.CurrentStage</c> so
/// the prefetch detail panel can show the user where work is currently
/// stuck — answering "is the loader frozen or making progress?"
///
/// <para>
/// Stages map to <c>BackgroundPreloadService.PreloadUrlAsync</c>'s flow:
/// </para>
///
/// <list type="bullet">
///   <item><see cref="Fetching"/>: HTTP request in flight to the origin.</item>
///   <item><see cref="Detecting"/>: response received; running cheap checks
///   (HumanActionDetector, paywall preview detection, content quality gate).</item>
///   <item><see cref="ExtractingContent"/>: pulling readable article body
///   for the article-cache (heuristic + optional AI escalation).</item>
///   <item><see cref="PersistingCache"/>: writing the page-build-cache and
///   article-content-cache to disk.</item>
///   <item><see cref="Idle"/>: no URL currently in flight.</item>
/// </list>
/// </summary>
public enum PreloadStage
{
    /// <summary>No URL currently being processed.</summary>
    Idle = 0,

    /// <summary>HTTP fetch in flight.</summary>
    Fetching,

    /// <summary>Response received; running HumanActionDetector / paywall / quality gates.</summary>
    Detecting,

    /// <summary>Pulling readable article body for the article cache.</summary>
    ExtractingContent,

    /// <summary>Writing build cache + article cache to disk.</summary>
    PersistingCache,
}
