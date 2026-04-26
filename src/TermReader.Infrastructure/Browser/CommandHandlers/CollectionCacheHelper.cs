// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Collections;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Builds per-article cache progress for a collection.
/// </summary>
internal static class CollectionCacheHelper
{
    /// <summary>
    /// Builds a <see cref="CollectionCacheProgress"/> snapshot for the given collection
    /// by checking each article URL against the page cache.
    /// </summary>
    public static CollectionCacheProgress GetProgress(
        Collection collection,
        IPageCache pageCache,
        IPreloadService preloadService)
    {
        var cachedUrls = pageCache.GetCachedUrls();
        var articleCachedUrls = preloadService.GetArticleCachedUrls();

        var articles = collection.Items.Select(item =>
        {
            var state = cachedUrls.Contains(item.Url) || articleCachedUrls.Contains(item.Url)
                ? ArticleCacheState.Cached
                : ArticleCacheState.Pending;

            return new ArticleCacheStatus
            {
                Url = item.Url,
                Title = item.Title,
                State = state
            };
        }).ToList();

        return new CollectionCacheProgress { Articles = articles };
    }
}
