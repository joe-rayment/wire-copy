// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast.Cache;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// Saves a page handed over by the browser extension to the Reading List (workspace-m7aml).
/// The HTML the browser already has is parsed by <see cref="ArticleHtmlExtraction"/> — the
/// same code path as a page the app loads itself, saved per-site layout included — and no
/// network request is made. The extracted article is written to the article cache so
/// opening or narrating the row later needs no re-fetch.
/// </summary>
internal sealed class ReadingListCapture : IReadingListCapture
{
    private readonly ILinkExtractor _linkExtractor;
    private readonly IReadableContentExtractor _contentExtractor;
    private readonly IArticleLayoutStore? _layoutStore;
    private readonly ISelectorBasedArticleExtractor? _selectorExtractor;
    private readonly IArticleContentCache _articleCache;
    private readonly BrowserConfiguration _browserConfig;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReadingListCapture> _logger;

    public ReadingListCapture(
        ILinkExtractor linkExtractor,
        IReadableContentExtractor contentExtractor,
        IArticleLayoutStore? layoutStore,
        ISelectorBasedArticleExtractor? selectorExtractor,
        IArticleContentCache articleCache,
        IOptions<BrowserConfiguration> browserConfig,
        IServiceScopeFactory scopeFactory,
        ILogger<ReadingListCapture> logger)
    {
        _linkExtractor = linkExtractor;
        _contentExtractor = contentExtractor;
        _layoutStore = layoutStore;
        _selectorExtractor = selectorExtractor;
        _articleCache = articleCache;
        _browserConfig = browserConfig.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Parses the page, caches its article, and saves (or moves to top) the Reading List row.</summary>
    /// <param name="url">The page's address; must be an absolute http or https URL.</param>
    /// <param name="pageTitle">The browser tab's title, used when no article title was extracted.</param>
    /// <param name="html">The page HTML the browser already has.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was saved.</returns>
    /// <exception cref="ArgumentException">The URL is not an absolute http or https address.</exception>
    public async Task<ReadingListCaptureResult> SaveAsync(
        string url,
        string? pageTitle,
        string? html,
        CancellationToken cancellationToken)
    {
        url = url?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only web pages (http or https addresses) can be saved to the Reading List.", nameof(url));
        }

        var extraction = await ArticleHtmlExtraction.ExtractAsync(
            html ?? string.Empty,
            url,
            _linkExtractor,
            _contentExtractor,
            _layoutStore,
            _selectorExtractor,
            _logger,
            cancellationToken).ConfigureAwait(false);

        var readable = extraction.Readable;
        if (readable != null && ArticleHtmlExtraction.ShouldCache(_browserConfig, url, readable))
        {
            await TryCacheArticleAsync(url, readable, cancellationToken).ConfigureAwait(false);
        }

        // Collection.AddOrMoveToTop throws on an empty title, so never produce one.
        var title = FirstNonBlank(readable?.Title, pageTitle) ?? url;

        using var scope = _scopeFactory.CreateScope();
        var collections = scope.ServiceProvider.GetRequiredService<ICollectionService>();
        var readingList = await collections.GetReadingListAsync(cancellationToken).ConfigureAwait(false);
        var alreadySaved = readingList.Items.Any(i => string.Equals(i.Url, url, StringComparison.OrdinalIgnoreCase));
        await collections.SaveToReadingListAsync(url, title, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Extension capture saved {Url} to the Reading List (source={Source}, words={Words}, alreadySaved={AlreadySaved})",
            url,
            extraction.Source,
            readable?.WordCount ?? 0,
            alreadySaved);

        return new ReadingListCaptureResult(title.Trim(), readable?.WordCount ?? 0, extraction.Source, alreadySaved);
    }

    /// <summary>How many items the Reading List shows (the same age cut-off as the launcher card).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current item count.</returns>
    public async Task<int> ReadingListCountAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var collections = scope.ServiceProvider.GetRequiredService<ICollectionService>();
        return await collections
            .CountReadingListItemsAsync(TimeSpan.FromHours(BrowserOrchestrator.ReadingListExpiryHours), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    private async Task TryCacheArticleAsync(string url, ReadableContent readable, CancellationToken cancellationToken)
    {
        try
        {
            await _articleCache.PutAsync(url, ArticleHtmlExtraction.ToExtractedArticle(readable, url), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache the extension-captured article for {Url}", url);
        }
    }
}
