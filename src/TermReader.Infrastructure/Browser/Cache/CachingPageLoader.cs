// Educational and personal use only.

using System.Net;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser;

namespace TermReader.Infrastructure.Browser.Cache;

/// <summary>
/// Decorator around IPageLoader that checks the page cache before fetching from the network.
/// On cache miss, delegates to the inner loader and stores the result.
/// </summary>
public class CachingPageLoader : IPageLoader
{
    private readonly IPageLoader _inner;
    private readonly IPageCache _cache;
    private readonly ILogger<CachingPageLoader> _logger;

    public CachingPageLoader(
        IPageLoader inner,
        IPageCache cache,
        ILogger<CachingPageLoader> logger)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PageLoadResult> LoadAsync(
        PageLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.ForceRefresh)
        {
            var cached = _cache.TryGet(request.Url);
            if (cached != null)
            {
                if (HasSufficientContent(cached.Html))
                {
                    _logger.LogInformation("Serving from cache: {Url}", request.Url);
                    return cached with { FetchMethod = FetchMethod.Cached };
                }

                _logger.LogInformation(
                    "Cached content below quality threshold, falling through to loader: {Url}",
                    request.Url);
                _cache.Remove(request.Url);
            }
        }
        else
        {
            _logger.LogInformation("Force refresh requested, bypassing cache: {Url}", request.Url);
        }

        var result = await _inner.LoadAsync(request, cancellationToken);

        if (result.Success)
        {
            if (PageLoader.IsBotChallengePage(result.Html))
            {
                _logger.LogWarning("Bot challenge page detected, skipping cache: {Url}", request.Url);
            }
            else
            {
                _cache.Put(request.Url, result);
            }
        }

        return result;
    }

    public Task<string> GetPageSourceAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        return _inner.GetPageSourceAsync(url, cancellationToken);
    }

    /// <summary>
    /// Checks whether the cached HTML has enough visible text content to be a real article.
    /// Paywall pages, JS shells, and bot challenge pages typically have very few words.
    /// This serves as a read-side quality gate. BackgroundPreloadService also calls this
    /// before caching preloaded pages, so most low-quality entries are prevented at write time.
    /// This check remains as a safety net for entries cached before the write-side gate existed.
    /// </summary>
    internal static bool HasSufficientContent(string? html, int minWordCount = 50)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remove script/style nodes before extracting text
        var nodesToRemove = doc.DocumentNode.SelectNodes("//script|//style|//noscript");
        if (nodesToRemove != null)
        {
            foreach (var node in nodesToRemove)
            {
                node.Remove();
            }
        }

        var text = WebUtility.HtmlDecode(doc.DocumentNode.InnerText);
        var wordCount = CountWords(text);
        return wordCount >= minWordCount;
    }

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (!inWord)
                {
                    count++;
                    inWord = true;
                }
            }
            else
            {
                inWord = false;
            }
        }

        return count;
    }
}
