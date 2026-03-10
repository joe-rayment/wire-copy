// Educational and personal use only.

namespace TermReader.Infrastructure.Podcast.Cache;

/// <summary>
/// Persistent cache for extracted article content, keyed by URL.
/// Allows the podcast flow to skip expensive re-extraction when
/// the same article has been processed recently.
/// </summary>
internal interface IArticleContentCache
{
    /// <summary>
    /// Attempts to retrieve a previously cached article by URL.
    /// Returns null on cache miss or expiry.
    /// </summary>
    Task<ExtractedArticle?> TryGetAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores extracted article content in the cache, keyed by normalized URL.
    /// </summary>
    Task PutAsync(string url, ExtractedArticle article, CancellationToken cancellationToken = default);
}
