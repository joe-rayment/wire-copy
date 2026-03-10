// Educational and personal use only.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.DTOs.Podcast;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast.Cache;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Fetches and extracts readable content from all items in a reading list collection,
/// ensuring pages are loaded and cached before podcast generation begins.
/// </summary>
internal sealed class ReadingListContentProvider
{
    private const int NetworkFetchDelayMs = 3000;
    private static readonly TimeSpan InFlightWaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PerArticleTimeout = TimeSpan.FromSeconds(60);

    private readonly IPageLoader _pageLoader;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly BrowserConfiguration _browserConfig;
    private readonly IPreloadService _preloadService;
    private readonly IPageCache _pageCache;
    private readonly IBrowserSession _browserSession;
    private readonly IArticleContentCache _articleCache;
    private readonly ILogger<ReadingListContentProvider> _logger;
    private List<ArticleFailure> _lastFailures = [];
    private bool _seleniumDriverCrashed;

    public ReadingListContentProvider(
        IPageLoader pageLoader,
        IReadableContentExtractor contentExtractor,
        IOptions<BrowserConfiguration> browserConfig,
        IPreloadService preloadService,
        IPageCache pageCache,
        IBrowserSession browserSession,
        IArticleContentCache articleCache,
        ILogger<ReadingListContentProvider> logger)
    {
        _pageLoader = pageLoader;
        _contentExtractor = contentExtractor;
        _browserConfig = browserConfig.Value;
        _preloadService = preloadService;
        _pageCache = pageCache;
        _browserSession = browserSession;
        _articleCache = articleCache;
        _logger = logger;
    }

    /// <summary>
    /// Gets article failures from the most recent extraction.
    /// Populated after each call to <see cref="GetAllArticleContentAsync"/>.
    /// </summary>
    public IReadOnlyList<ArticleFailure> LastExtractionFailures => _lastFailures;

    /// <summary>
    /// Loads and extracts readable content from all items in a collection.
    /// Skips items that fail to load or don't contain article content.
    /// Failures are available via <see cref="LastExtractionFailures"/> after this call.
    /// </summary>
    /// <param name="collection">The reading list collection.</param>
    /// <param name="progress">Optional progress callback reporting per-article extraction status.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered list of extracted article content.</returns>
    public async Task<IReadOnlyList<ExtractedArticle>> GetAllArticleContentAsync(
        Collection collection,
        IProgress<ContentExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var items = collection.Items;
        _lastFailures = [];
        _seleniumDriverCrashed = false;

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
            Action<string>? reportMethod = progress != null
                ? method => progress.Report(new ContentExtractionProgress
                {
                    Current = i + 1,
                    Total = items.Count,
                    Title = item.Title,
                    ExtractionMethod = method,
                })
                : null;

            progress?.Report(new ContentExtractionProgress
            {
                Current = i + 1,
                Total = items.Count,
                Title = item.Title,
            });

            try
            {
                using var articleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                articleCts.CancelAfter(PerArticleTimeout);
                var (article, fetchMethod) = await LoadAndExtractAsync(item, reportMethod, articleCts.Token);

                progress?.Report(new ContentExtractionProgress
                {
                    Current = i + 1,
                    Total = items.Count,
                    Title = item.Title,
                    IsCompleted = true,
                    IsSuccess = article != null,
                });

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
                    _lastFailures.Add(new ArticleFailure
                    {
                        Title = item.Title,
                        Url = item.Url,
                        Reason = "No readable content found after all extraction attempts",
                    });
                }

                // Rate limit when fetching from network to respect robots.txt and avoid bot detection
                if (fetchMethod != FetchMethod.Cached && i < items.Count - 1)
                {
                    await Task.Delay(NetworkFetchDelayMs, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-article timeout — log and continue to next article
                progress?.Report(new ContentExtractionProgress
                {
                    Current = i + 1,
                    Total = items.Count,
                    Title = item.Title,
                    IsCompleted = true,
                });

                _logger.LogWarning(
                    "Article extraction timed out for {Index}/{Total}: '{Title}'",
                    i + 1,
                    items.Count,
                    item.Title);
                _lastFailures.Add(new ArticleFailure
                {
                    Title = item.Title,
                    Url = item.Url,
                    Reason = $"Extraction timed out after {PerArticleTimeout.TotalSeconds}s",
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                progress?.Report(new ContentExtractionProgress
                {
                    Current = i + 1,
                    Total = items.Count,
                    Title = item.Title,
                    IsCompleted = true,
                });

                _logger.LogWarning(
                    ex,
                    "Failed to load item {Index}/{Total}: '{Title}' - {Message}",
                    i + 1,
                    items.Count,
                    item.Title,
                    ex.Message);
                _lastFailures.Add(new ArticleFailure
                {
                    Title = item.Title,
                    Url = item.Url,
                    Reason = ex.Message,
                });
            }
        }

        _logger.LogInformation(
            "Extracted {Extracted}/{Total} articles from '{Collection}'",
            results.Count,
            items.Count,
            collection.Name);

        return results;
    }

    private static bool IsDriverCrashFailure(PageLoadResult result)
    {
        if (result.Success)
        {
            return false;
        }

        var error = result.ErrorMessage;
        return error != null &&
            (error.Contains("session is no longer available", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("session not created", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("unable to connect", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("disconnected", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(ExtractedArticle? Article, FetchMethod FetchMethod)> LoadAndExtractAsync(
        CollectionItem item,
        Action<string>? reportMethod,
        CancellationToken cancellationToken)
    {
        // Check persistent article content cache before any network calls
        try
        {
            var cachedArticle = await _articleCache.TryGetAsync(item.Url, cancellationToken);
            if (cachedArticle != null)
            {
                reportMethod?.Invoke("content cache");
                _logger.LogDebug("Using cached article content for {Url}", item.Url);
                return (cachedArticle, FetchMethod.Cached);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Article content cache read failed for {Url}, falling through to extraction", item.Url);
        }

        // Wait for any in-flight preload before fetching
        try
        {
            if (!_pageCache.Contains(item.Url))
            {
                reportMethod?.Invoke("preload");
                var preloadResult = await _preloadService.WaitForInFlightAsync(
                    item.Url, InFlightWaitTimeout, cancellationToken);
                if (preloadResult is { Success: true } && !string.IsNullOrEmpty(preloadResult.Html))
                {
                    _logger.LogDebug("Using completed in-flight preload for {Url}", item.Url);
                    var preloadArticle = await TryExtractArticleAsync(preloadResult, item.Url, cancellationToken);
                    if (preloadArticle != null)
                    {
                        return (preloadArticle, FetchMethod.Cached);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Preload wait failed for {Url}, falling through to Layer 1", item.Url);
        }

        var lastFetchMethod = FetchMethod.Http;
        PageLoadResult? lastLoadResult = null;

        // Layer 1: Initial load (HTTP/cached)
        try
        {
            reportMethod?.Invoke(_pageCache.Contains(item.Url) ? "cache" : "HTTP");
            lastLoadResult = await _pageLoader.LoadAsync(
                new PageLoadRequest { Url = item.Url },
                cancellationToken);
            lastFetchMethod = lastLoadResult.FetchMethod;

            if (!lastLoadResult.Success || string.IsNullOrEmpty(lastLoadResult.Html))
            {
                _logger.LogDebug("Page load failed for {Url}: {Error}", item.Url, lastLoadResult.ErrorMessage);
            }
            else
            {
                var article = await TryExtractArticleAsync(lastLoadResult, item.Url, cancellationToken);
                if (article != null)
                {
                    return (article, lastLoadResult.FetchMethod);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Layer 1 (HTTP) failed for {Url}, falling through to Selenium", item.Url);
        }

        // Layer 2: If HTTP/cached returned no content (JS shell), retry with Selenium
        // Skip Selenium layers if the driver has already crashed during this extraction run
        if (lastFetchMethod != FetchMethod.Selenium && !_seleniumDriverCrashed)
        {
            try
            {
                reportMethod?.Invoke("Selenium");
                _logger.LogInformation(
                    "No readable content from {Method}, retrying with Selenium: {Url}",
                    lastFetchMethod,
                    item.Url);

                lastLoadResult = await _pageLoader.LoadAsync(
                    new PageLoadRequest
                    {
                        Url = item.Url,
                        Headless = _browserConfig.Headless,
                        ForceRefresh = true,
                    },
                    cancellationToken);
                lastFetchMethod = lastLoadResult.FetchMethod;

                if (IsDriverCrashFailure(lastLoadResult))
                {
                    _seleniumDriverCrashed = true;
                    _logger.LogWarning("Selenium driver crashed, skipping browser layers for remaining articles");
                }
                else if (lastLoadResult.Success && !string.IsNullOrEmpty(lastLoadResult.Html))
                {
                    var article = await TryExtractArticleAsync(lastLoadResult, item.Url, cancellationToken);
                    if (article != null)
                    {
                        return (article, lastLoadResult.FetchMethod);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Layer 2 (Selenium) failed for {Url}, falling through to Layer 3", item.Url);
            }
        }

        // Layer 3: If Selenium hit a bot challenge (resolved or not), retry headed with fresh navigation
        if (!_seleniumDriverCrashed && lastLoadResult != null)
        {
            var isBotChallengeFailure = !lastLoadResult.Success &&
                lastLoadResult.ErrorMessage?.Contains("Bot challenge", StringComparison.OrdinalIgnoreCase) == true;
            var isBotChallengeContent = lastLoadResult.Success &&
                !string.IsNullOrEmpty(lastLoadResult.Html) &&
                PageLoader.IsBotChallengePage(lastLoadResult.Html);

            if (isBotChallengeFailure || isBotChallengeContent)
            {
                try
                {
                    _browserSession.RestoreWindow();
                    reportMethod?.Invoke("bot challenge \u2014 check browser");
                    _logger.LogWarning(
                        "Bot challenge detected, restoring browser window for user intervention: {Url}",
                        item.Url);

                    lastLoadResult = await _pageLoader.LoadAsync(
                        new PageLoadRequest
                        {
                            Url = item.Url,
                            Headless = false,
                            ForceRefresh = true,
                        },
                        cancellationToken);
                    lastFetchMethod = lastLoadResult.FetchMethod;

                    if (IsDriverCrashFailure(lastLoadResult))
                    {
                        _seleniumDriverCrashed = true;
                        _logger.LogWarning("Selenium driver crashed during bot challenge retry");
                    }
                    else if (lastLoadResult.Success && !string.IsNullOrEmpty(lastLoadResult.Html))
                    {
                        var article = await TryExtractArticleAsync(lastLoadResult, item.Url, cancellationToken);
                        if (article != null)
                        {
                            return (article, lastLoadResult.FetchMethod);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Layer 3 (bot challenge) failed for {Url}", item.Url);
                }
            }
        }

        return (null, lastFetchMethod);
    }

    private async Task<ExtractedArticle?> TryExtractArticleAsync(
        PageLoadResult loadResult,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await _contentExtractor.ExtractAsync(loadResult.Html, loadResult.Url, cancellationToken);
            if (content == null || string.IsNullOrWhiteSpace(content.CleanedText))
            {
                return null;
            }

            var article = new ExtractedArticle
            {
                Title = content.Title,
                CleanedText = content.CleanedText,
                Author = content.Author,
                Url = url,
                WordCount = content.WordCount,
                PublishedDate = content.PublishedDate,
            };

            // Persist extracted content so future invocations skip re-extraction
            try
            {
                await _articleCache.PutAsync(url, article, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to cache article content for {Url}", url);
            }

            return article;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Content extraction threw for {Url}", url);
            return null;
        }
    }
}
