// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Fetches and extracts readable content from all items in a reading list collection,
/// ensuring pages are loaded and cached before podcast generation begins.
/// </summary>
internal sealed class ReadingListContentProvider
{
    private const int NetworkFetchDelayMs = 3000;

    private readonly IPageLoader _pageLoader;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly ILogger<ReadingListContentProvider> _logger;

    public ReadingListContentProvider(
        IPageLoader pageLoader,
        IReadableContentExtractor contentExtractor,
        ILogger<ReadingListContentProvider> logger)
    {
        _pageLoader = pageLoader;
        _contentExtractor = contentExtractor;
        _logger = logger;
    }

    /// <summary>
    /// Loads and extracts readable content from all items in a collection.
    /// Skips items that fail to load or don't contain article content.
    /// </summary>
    /// <param name="collection">The reading list collection.</param>
    /// <param name="progress">Optional progress callback reporting (current, total, title).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered list of extracted article content.</returns>
    public async Task<IReadOnlyList<ExtractedArticle>> GetAllArticleContentAsync(
        Collection collection,
        IProgress<(int Current, int Total, string Title)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var items = collection.Items;
        if (items.Count == 0)
        {
            return [];
        }

        _logger.LogInformation(
            "Loading content for {Count} reading list items from '{Collection}'",
            items.Count,
            collection.Name);

        var results = new List<ExtractedArticle>();

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            progress?.Report((i + 1, items.Count, item.Title));

            try
            {
                var (article, fetchMethod) = await LoadAndExtractAsync(item, cancellationToken);
                if (article != null)
                {
                    results.Add(article);
                    _logger.LogDebug(
                        "Extracted article {Index}/{Total}: '{Title}' ({Words} words)",
                        i + 1,
                        items.Count,
                        article.Title,
                        article.WordCount);
                }
                else
                {
                    _logger.LogWarning(
                        "Skipping item {Index}/{Total}: '{Title}' - no readable content",
                        i + 1,
                        items.Count,
                        item.Title);
                }

                // Rate limit when fetching from network to respect robots.txt and avoid bot detection
                if (fetchMethod != FetchMethod.Cached && i < items.Count - 1)
                {
                    await Task.Delay(NetworkFetchDelayMs, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to load item {Index}/{Total}: '{Title}' - {Message}",
                    i + 1,
                    items.Count,
                    item.Title,
                    ex.Message);
            }
        }

        _logger.LogInformation(
            "Extracted {Extracted}/{Total} articles from '{Collection}'",
            results.Count,
            items.Count,
            collection.Name);

        return results;
    }

    private async Task<(ExtractedArticle? Article, FetchMethod FetchMethod)> LoadAndExtractAsync(
        CollectionItem item,
        CancellationToken cancellationToken)
    {
        // Load the page (CachingPageLoader will serve from cache if available)
        var loadResult = await _pageLoader.LoadAsync(
            new PageLoadRequest { Url = item.Url },
            cancellationToken);

        if (!loadResult.Success || string.IsNullOrEmpty(loadResult.Html))
        {
            _logger.LogDebug("Page load failed for {Url}: {Error}", item.Url, loadResult.ErrorMessage);
            return (null, loadResult.FetchMethod);
        }

        // Extract readable content
        var content = await _contentExtractor.ExtractAsync(loadResult.Html, loadResult.Url, cancellationToken);
        if (content == null || string.IsNullOrWhiteSpace(content.CleanedText))
        {
            return (null, loadResult.FetchMethod);
        }

        var article = new ExtractedArticle
        {
            Title = content.Title,
            CleanedText = content.CleanedText,
            Author = content.Author,
            Url = item.Url,
            WordCount = content.WordCount,
            PublishedDate = content.PublishedDate,
        };

        return (article, loadResult.FetchMethod);
    }
}
