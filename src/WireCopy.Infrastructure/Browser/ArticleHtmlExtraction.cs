// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// The one place that turns page HTML into a classification and readable content
/// (workspace-m7aml). <see cref="PageLoadPipeline"/> and the browser-extension
/// reading-list capture both call it, so a page the extension hands over is parsed
/// EXACTLY as a page the app loads itself — including a saved per-site layout
/// (workspace-8qyo) — with no network.
/// </summary>
internal static class ArticleHtmlExtraction
{
    /// <summary>Source label: the saved per-domain article layout produced the content.</summary>
    public const string SourceSiteLayout = "site-layout";

    /// <summary>Source label: the generic readability heuristics produced the content.</summary>
    public const string SourceReadability = "readability";

    /// <summary>Source label: no readable article (link-list page, or nothing extractable).</summary>
    public const string SourceNone = "none";

    // Paywalled domains often serve a truncated preview; below this it is not cached.
    private const int PaywalledMinWords = 200;

    /// <summary>
    /// Classifies already-extracted links plus the HTML's signals into a page kind.
    /// Split from <see cref="ClassifyAsync"/> so a caller that needs the links for
    /// its own purpose (the navigation tree) extracts them only once.
    /// </summary>
    /// <param name="html">Page HTML.</param>
    /// <param name="url">Final page URL (post-redirect).</param>
    /// <param name="links">The links already extracted from <paramref name="html"/>.</param>
    /// <returns>The classification, its score, and the signals/counts behind it.</returns>
    public static PageClassificationResult Classify(string html, string url, IReadOnlyCollection<LinkInfo> links)
    {
        var signals = PageSignalExtractor.Extract(html);
        var contentLinkCount = links.Count(l => l.Type == LinkType.Content);
        var (classification, score) = PageClassifier.ClassifyScored(signals, contentLinkCount, url);
        return new PageClassificationResult(classification, score, signals, contentLinkCount);
    }

    /// <summary>Extracts links, then classifies — the full classify step for callers that do not need the links.</summary>
    /// <param name="html">Page HTML.</param>
    /// <param name="url">Page URL (final, post-redirect).</param>
    /// <param name="linkExtractor">The app's link extractor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The classification result and the links it was computed from.</returns>
    public static async Task<(PageClassificationResult Result, List<LinkInfo> Links)> ClassifyAsync(
        string html,
        string url,
        ILinkExtractor linkExtractor,
        CancellationToken cancellationToken)
    {
        var links = await linkExtractor.ExtractLinksAsync(html, url, cancellationToken).ConfigureAwait(false);
        return (Classify(html, url, links), links);
    }

    /// <summary>
    /// Extracts readable content: the saved per-domain layout first (authoritative —
    /// a tuned site never regresses to the heuristics it was tuned to fix), then the
    /// generic readability extractor. Does NOT apply the AI escalation (pipeline-only).
    /// </summary>
    /// <param name="html">Page HTML.</param>
    /// <param name="url">Page URL (final, post-redirect).</param>
    /// <param name="contentExtractor">The generic readability extractor.</param>
    /// <param name="layoutStore">Saved per-domain layouts, or null when unavailable.</param>
    /// <param name="selectorExtractor">Applies a saved layout to HTML, or null when unavailable.</param>
    /// <param name="logger">Logger for the saved-layout hit line.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The content (null when none) and which stage produced it.</returns>
    public static async Task<(ReadableContent? Readable, string Source)> ExtractReadableAsync(
        string html,
        string url,
        IReadableContentExtractor contentExtractor,
        IArticleLayoutStore? layoutStore,
        ISelectorBasedArticleExtractor? selectorExtractor,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // workspace-8qyo: a SAVED per-domain layout (user-tuned via 'L' or an AI config
        // that passed its self-test) is AUTHORITATIVE — apply it before generic
        // readability. Misses fall through unchanged.
        var saved = await TrySavedSelectorConfigAsync(url, html, layoutStore, selectorExtractor, logger).ConfigureAwait(false);
        if (saved != null)
        {
            return (saved, SourceSiteLayout);
        }

        var heuristic = await contentExtractor.ExtractAsync(html, url, cancellationToken).ConfigureAwait(false);
        return heuristic != null ? (heuristic, SourceReadability) : (null, SourceNone);
    }

    /// <summary>
    /// Classifies the HTML and, unless it is a link list, extracts its readable
    /// content — the whole no-network "parse this page" path used by the extension capture.
    /// </summary>
    /// <param name="html">Page HTML.</param>
    /// <param name="url">Page URL.</param>
    /// <param name="linkExtractor">The app's link extractor.</param>
    /// <param name="contentExtractor">The generic readability extractor.</param>
    /// <param name="layoutStore">Saved per-domain layouts, or null when unavailable.</param>
    /// <param name="selectorExtractor">Applies a saved layout to HTML, or null when unavailable.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Classification, readable content (null when none) and its source label.</returns>
    public static async Task<ArticleExtractionResult> ExtractAsync(
        string html,
        string url,
        ILinkExtractor linkExtractor,
        IReadableContentExtractor contentExtractor,
        IArticleLayoutStore? layoutStore,
        ISelectorBasedArticleExtractor? selectorExtractor,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var (classified, _) = await ClassifyAsync(html, url, linkExtractor, cancellationToken).ConfigureAwait(false);

        // Link-list pages carry only a link tree, no article body.
        if (classified.Classification == PageClassification.LinkList)
        {
            return new ArticleExtractionResult(classified.Classification, null, SourceNone);
        }

        var (readable, source) = await ExtractReadableAsync(
            html, url, contentExtractor, layoutStore, selectorExtractor, logger, cancellationToken).ConfigureAwait(false);
        return new ArticleExtractionResult(classified.Classification, readable, source);
    }

    /// <summary>Maps extracted readable content to the podcast article-cache record.</summary>
    /// <param name="content">The extracted content.</param>
    /// <param name="url">The URL to record as the article's source.</param>
    /// <returns>The cacheable article.</returns>
    public static ExtractedArticle ToExtractedArticle(ReadableContent content, string url) => new()
    {
        Title = content.Title,
        CleanedText = content.CleanedText,
        Author = content.Author,
        Url = url,
        WordCount = content.WordCount,
        PublishedDate = content.PublishedDate,
    };

    /// <summary>
    /// The article-cache rule: skip paywalled-domain articles with a low word count
    /// (likely truncated previews).
    /// </summary>
    /// <param name="config">Browser configuration naming the paywalled domains.</param>
    /// <param name="url">The article URL.</param>
    /// <param name="readable">The extracted content.</param>
    /// <returns>True when the content should be written to the article cache.</returns>
    public static bool ShouldCache(BrowserConfiguration config, string url, ReadableContent readable) =>
        !config.IsPaywalledDomain(url) || readable.WordCount >= PaywalledMinWords;

    private static async Task<ReadableContent?> TrySavedSelectorConfigAsync(
        string url,
        string? html,
        IArticleLayoutStore? store,
        ISelectorBasedArticleExtractor? selectorExtractor,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var domain = ArticleLayoutDomains.FromUrl(url);
        if (store is null || selectorExtractor is null || domain is null)
        {
            return null;
        }

        var savedConfig = await store.LoadAsync(domain).ConfigureAwait(false);
        if (savedConfig == null)
        {
            return null;
        }

        var extracted = selectorExtractor.Extract(savedConfig, url, html);
        if (extracted != null)
        {
            logger.LogInformation(
                "Article extracted via saved selector config for {Url} ({Words} words)",
                url,
                extracted.WordCount);
        }

        return extracted;
    }
}
