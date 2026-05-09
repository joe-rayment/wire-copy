// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Applies a saved <see cref="ArticleSelectorConfig"/> to a loaded HTML page.
/// Picks the highest-priority <see cref="PageTypeEntry"/> whose
/// <see cref="PageTypeMatcher"/> matches, runs each <see cref="ArticleSelectors"/>
/// array in order, and gates on the entry's quality thresholds + the global
/// <see cref="ReadableContentExtractor.ValidateContentQuality"/> guard.
/// </summary>
public sealed partial class SelectorBasedArticleExtractor : ISelectorBasedArticleExtractor
{
    private readonly ILogger<SelectorBasedArticleExtractor> _logger;

    public SelectorBasedArticleExtractor(ILogger<SelectorBasedArticleExtractor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public PageTypeEntry? PickEntry(ArticleSelectorConfig config, string url, string html)
    {
        if (config == null || config.PageTypes.Count == 0)
        {
            return null;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html ?? string.Empty);

        // Highest priority wins; ties broken by declaration order (List preserves order, OrderByDescending is stable).
        var ordered = config.PageTypes
            .Select((entry, index) => (entry, index))
            .Where(p => MatcherApplies(p.entry.Matcher, url, doc))
            .OrderByDescending(p => p.entry.Priority)
            .ThenBy(p => p.index)
            .ToList();

        return ordered.Count == 0 ? null : ordered[0].entry;
    }

    /// <inheritdoc />
    public ReadableContent? Extract(ArticleSelectorConfig config, string url, string html)
    {
        var entry = PickEntry(config, url, html);
        if (entry == null)
        {
            _logger.LogDebug("No matching article-selector entry for {Url}", url);
            return null;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html ?? string.Empty);

        // Strip exclude regions before reading body / headline / etc.
        foreach (var excludeSelector in entry.Selectors.ExcludeRegions)
        {
            RemoveAll(doc, excludeSelector);
        }

        var headline = FirstNonEmptyText(doc, entry.Selectors.Headline) ?? "Untitled Article";
        var byline = FirstNonEmptyText(doc, entry.Selectors.Byline);
        var publishDateText = FirstNonEmptyAttributeOrText(doc, entry.Selectors.PublishDate, attr: "datetime");
        var publishedDate = TryParseDate(publishDateText);

        var paragraphs = ExtractParagraphs(doc, entry.Selectors.Body);
        if (paragraphs.Count < entry.Quality.MinParagraphs)
        {
            _logger.LogDebug(
                "Selector entry '{Entry}' produced {Paragraphs} paragraphs for {Url}; below MinParagraphs={Min}",
                entry.Name,
                paragraphs.Count,
                url,
                entry.Quality.MinParagraphs);
            return null;
        }

        var totalWords = paragraphs.Sum(p => p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        if (totalWords < entry.Quality.MinWords)
        {
            _logger.LogDebug(
                "Selector entry '{Entry}' produced {Words} words for {Url}; below MinWords={Min}",
                entry.Name,
                totalWords,
                url,
                entry.Quality.MinWords);
            return null;
        }

        // Defer to the global quality gate (workspace-d799). This catches
        // template / repeated-first-word garbage that escapes the entry-level
        // thresholds.
        if (!ReadableContentExtractor.ValidateContentQuality(paragraphs))
        {
            _logger.LogDebug(
                "Selector entry '{Entry}' for {Url} failed global ValidateContentQuality gate",
                entry.Name,
                url);
            return null;
        }

        var cleanedText = string.Join("\n\n", paragraphs);
        try
        {
            return ReadableContent.Create(
                title: headline,
                cleanedText: cleanedText,
                paragraphs: paragraphs.ToList(),
                author: byline,
                publishedDate: publishedDate,
                isPaywalled: false);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Selector extractor produced invalid ReadableContent for {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Decides whether all populated fields of <paramref name="matcher"/>
    /// agree with the page (URL + parsed HTML). Empty / null fields are
    /// treated as a wildcard.
    /// </summary>
    internal static bool MatcherApplies(PageTypeMatcher matcher, string url, HtmlDocument doc)
    {
        if (!string.IsNullOrEmpty(matcher.UrlPattern))
        {
            try
            {
                if (!Regex.IsMatch(url ?? string.Empty, matcher.UrlPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                {
                    return false;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                // Bad regex in the saved config — treat as non-matching.
                return false;
            }
        }

        if (matcher.MetaTags.Count > 0)
        {
            foreach (var (key, expected) in matcher.MetaTags)
            {
                var node = doc.DocumentNode.SelectSingleNode(
                    $"//meta[(@name='{XPathEscape(key)}' or @property='{XPathEscape(key)}')]");
                var content = node?.GetAttributeValue("content", string.Empty);
                if (string.IsNullOrEmpty(content) ||
                    content.IndexOf(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrEmpty(matcher.LdJsonType) && !HasLdJsonType(doc, matcher.LdJsonType))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(matcher.BodyClassContains))
        {
            var bodyClass = doc.DocumentNode.SelectSingleNode("//body")?.GetAttributeValue("class", string.Empty);
            if (string.IsNullOrEmpty(bodyClass) ||
                bodyClass.IndexOf(matcher.BodyClassContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasLdJsonType(HtmlDocument doc, string expectedType)
    {
        var scripts = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scripts == null)
        {
            return false;
        }

        foreach (var script in scripts)
        {
            var raw = HtmlEntity.DeEntitize(script.InnerText);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                using var parsed = JsonDocument.Parse(raw);
                if (LdJsonContainsType(parsed.RootElement, expectedType))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Skip malformed ld+json blocks.
            }
        }

        return false;
    }

    private static bool LdJsonContainsType(JsonElement element, string expectedType)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("@type", out var typeProp) && TypeMatches(typeProp, expectedType))
            {
                return true;
            }

            if (element.EnumerateObject().Any(prop => LdJsonContainsType(prop.Value, expectedType)))
            {
                return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array
            && element.EnumerateArray().Any(item => LdJsonContainsType(item, expectedType)))
        {
            return true;
        }

        return false;
    }

    private static bool TypeMatches(JsonElement typeProp, string expectedType)
    {
        if (typeProp.ValueKind == JsonValueKind.String)
        {
            return string.Equals(typeProp.GetString(), expectedType, StringComparison.OrdinalIgnoreCase);
        }

        if (typeProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeProp.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    string.Equals(item.GetString(), expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<string> ExtractParagraphs(HtmlDocument doc, IReadOnlyList<string> bodySelectors)
    {
        foreach (var selector in bodySelectors)
        {
            var nodes = SelectAll(doc, selector);
            if (nodes == null || nodes.Count == 0)
            {
                continue;
            }

            var paragraphs = new List<string>();
            foreach (var node in nodes)
            {
                CollectParagraphs(node, paragraphs);
            }

            if (paragraphs.Count > 0)
            {
                return paragraphs;
            }
        }

        return new List<string>();
    }

    private static void CollectParagraphs(HtmlNode root, List<string> output)
    {
        // If the body selector itself is a paragraph-like element, take its text directly.
        if (IsParagraphLike(root))
        {
            var text = NormalizeWhitespace(HtmlEntity.DeEntitize(root.InnerText));
            if (!string.IsNullOrWhiteSpace(text))
            {
                output.Add(text);
            }

            return;
        }

        var paragraphNodes = root.SelectNodes(".//p|.//h2|.//h3|.//li|.//blockquote");
        if (paragraphNodes == null)
        {
            // Fallback: take the whole node's text.
            var text = NormalizeWhitespace(HtmlEntity.DeEntitize(root.InnerText));
            if (!string.IsNullOrWhiteSpace(text))
            {
                output.Add(text);
            }

            return;
        }

        foreach (var node in paragraphNodes)
        {
            var text = NormalizeWhitespace(HtmlEntity.DeEntitize(node.InnerText));
            if (!string.IsNullOrWhiteSpace(text) && text.Length >= 20)
            {
                output.Add(text);
            }
        }
    }

    private static bool IsParagraphLike(HtmlNode node)
    {
        var name = node.Name?.ToLowerInvariant();
        return name is "p" or "h2" or "h3" or "li" or "blockquote";
    }

    private static string? FirstNonEmptyText(HtmlDocument doc, IReadOnlyList<string> selectors)
    {
        foreach (var selector in selectors)
        {
            var node = SelectFirst(doc, selector);
            if (node == null)
            {
                continue;
            }

            var text = NormalizeWhitespace(HtmlEntity.DeEntitize(node.InnerText));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string? FirstNonEmptyAttributeOrText(HtmlDocument doc, IReadOnlyList<string> selectors, string attr)
    {
        foreach (var selector in selectors)
        {
            var node = SelectFirst(doc, selector);
            if (node == null)
            {
                continue;
            }

            var attrValue = node.GetAttributeValue(attr, string.Empty);
            if (!string.IsNullOrWhiteSpace(attrValue))
            {
                return attrValue;
            }

            var text = NormalizeWhitespace(HtmlEntity.DeEntitize(node.InnerText));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static void RemoveAll(HtmlDocument doc, string selector)
    {
        var nodes = SelectAll(doc, selector);
        if (nodes == null)
        {
            return;
        }

        foreach (var node in nodes)
        {
            node.Remove();
        }
    }

    private static HtmlNodeCollection? SelectAll(HtmlDocument doc, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        try
        {
            return doc.DocumentNode.SelectNodes(selector);
        }
        catch (System.Xml.XPath.XPathException)
        {
            return null;
        }
    }

    private static HtmlNode? SelectFirst(HtmlDocument doc, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        try
        {
            return doc.DocumentNode.SelectSingleNode(selector);
        }
        catch (System.Xml.XPath.XPathException)
        {
            return null;
        }
    }

    private static string XPathEscape(string s) => s?.Replace("'", "&apos;", StringComparison.Ordinal) ?? string.Empty;

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private static DateTime? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
