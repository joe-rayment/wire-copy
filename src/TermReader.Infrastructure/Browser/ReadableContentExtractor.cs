// Educational and personal use only.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Extracts clean, readable article content from HTML.
/// Creates a "reader mode" view similar to browser reader views.
/// </summary>
public partial class ReadableContentExtractor : IReadableContentExtractor
{
    /// <summary>
    /// Minimum paragraph count to consider content non-truncated when paywall indicators are present.
    /// </summary>
    private const int PaywallTruncationThreshold = 3;

    /// <summary>
    /// Minimum total character count of paragraph text to consider content non-truncated.
    /// Short articles with substantive paragraphs (>= this length) are not flagged as paywalled
    /// even if the paragraph count is below the threshold.
    /// </summary>
    private const int PaywallMinContentLength = 500;

    private static readonly HashSet<string> ArticleIndicators = new(StringComparer.OrdinalIgnoreCase)
    {
        "article", "post", "entry", "story", "news", "blog"
    };

    private static readonly HashSet<string> BoilerplateClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "nav", "navigation", "menu", "sidebar", "footer", "header", "ad", "advertisement",
        "comment", "comments", "related", "share", "social", "promo", "newsletter",
        "sponsor", "promoted", "popup", "modal", "banner"
    };

    private static readonly string[] PaywallElementSelectors =
    {
        "//*[contains(concat(' ', @class, ' '), ' gateway ') or contains(concat(' ', @class, '-'), ' gateway-')]",
        "//*[contains(@class, 'expanded-dock')]",
        "//*[contains(@class, 'subscriber-gate')]",
        "//*[contains(@class, 'paywall')]",
        "//*[contains(@class, 'subscribe-wall')]",
        "//*[contains(@class, 'regwall')]",
        "//*[contains(@id, 'gateway')]",
        "//*[contains(@id, 'paywall')]",
        "//*[contains(@id, 'regwall')]",
        "//*[contains(@data-testid, 'paywall')]"
    };

    private static readonly string[] ContentAreaSelectors =
    {
        "//*[@itemprop='articleBody']",
        "//*[contains(@class, 'article-body')]",
        "//*[contains(@class, 'article-content')]",
        "//*[contains(@class, 'entry-content')]",
        "//*[contains(@class, 'post-content')]",
        "//*[contains(@class, 'story-body')]",
        "//*[contains(@class, 'story-content')]",
        "//*[contains(@class, 'article__body')]",
        "//*[contains(@class, 'article-text')]",
        "//*[contains(@class, 'body-content')]",
        "//*[contains(@class, 'main-content')]",
        "//*[contains(@class, 'single-content')]",
        "//*[contains(@class, 'post__content')]",
        "//*[contains(@class, 'page-content')]",
        "//*[contains(@class, 'field-body')]",
        "//*[contains(@class, 'text-content')]",

        // NYT / React SSR patterns
        "//*[contains(@class, 'StoryBodyCompanionColumn')]",
        "//*[contains(@class, 'story-body-supplemental')]",
        "//*[contains(@data-testid, 'article-body')]",
        "//*[@name='articleBody']",

        // WordPress / Gutenberg
        "//*[contains(@class, 'wp-block-post-content')]",
        "//*[contains(@class, 'td-post-content')]",
        "//*[contains(@class, 'single-post-content')]",

        // Substack / Ghost / Medium
        "//*[contains(@class, 'available-content')]",
        "//*[contains(@class, 'post-full-content')]",
        "//*[contains(@class, 'gh-content')]",
        "//*[contains(@class, 'markup')]",

        // Drupal
        "//*[contains(@class, 'field--name-body')]",
        "//*[contains(@class, 'node-content')]",

        // Data-attribute patterns (React/Vue SPA)
        "//*[@data-content-type='article-body']",
        "//*[@data-content-region='body']",

        // Generic semantic elements
        "//article",
        "//*[@role='article']",
        "//*[@role='main']",
        "//main",
        "//*[@id='content']",
        "//*[@class='content']"
    };

    private static readonly string[] PaywallTextPatterns =
    {
        "subscribe to the times",
        "subscribe to continue reading",
        "already a subscriber? log in",
        "subscribers can read",
        "this article is for subscribers",
        "to read the full article",
        "sign in to read",
        "members only",
        "create a free account",
        "start your free trial"
    };

    private readonly ILogger<ReadableContentExtractor> _logger;

    public ReadableContentExtractor(ILogger<ReadableContentExtractor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Detects JS shell pages: HTML with article indicators but no actual paragraph content.
    /// Returns true when the page looks like it should have article content but doesn't,
    /// suggesting it needs JavaScript to render. Non-article pages are never flagged.
    /// </summary>
    public static bool IsEmptyArticleShell(string html)
    {
        var lowerHtml = html.ToLowerInvariant();

        // Only check pages that have article indicators
        var hasArticleIndicators = lowerHtml.Contains("<article") ||
            OgTypeArticleRegex().IsMatch(lowerHtml) ||
            lowerHtml.Contains("article-body") ||
            lowerHtml.Contains("article-content") ||
            lowerHtml.Contains("entry-content") ||
            lowerHtml.Contains("post-content");

        if (!hasArticleIndicators)
        {
            return false;
        }

        // Has article markup — check for actual paragraph content
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var paragraphs = doc.DocumentNode.SelectNodes("//p");
        if (paragraphs == null)
        {
            return true;
        }

        var substantialCount = 0;
        foreach (var p in paragraphs)
        {
            var text = p.InnerText?.Trim();
            if (text != null && text.Length > 50)
            {
                substantialCount++;
                if (substantialCount >= 2)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether the HTML contains extractable article content.
    /// Unlike <see cref="IsEmptyArticleShell"/>, which only flags pages with article indicators
    /// but no paragraphs, this method attempts a lightweight content extraction to verify
    /// that the page has real readable article text. Returns false for JS shells that have
    /// boilerplate/navigation text (enough to pass word-count checks) but no article body.
    /// </summary>
    /// <param name="html">Raw HTML to check.</param>
    /// <returns>True if the HTML contains extractable article content; false otherwise.</returns>
    public static bool HasExtractableContent(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Remove boilerplate so we only check article-area content
        RemoveBoilerplate(doc);

        // Check for paragraphs in known content area selectors
        foreach (var selector in ContentAreaSelectors)
        {
            var area = doc.DocumentNode.SelectSingleNode(selector);
            if (area == null)
            {
                continue;
            }

            var paragraphs = ExtractSemanticParagraphs(area);
            if (paragraphs.Count >= 3)
            {
                return true;
            }
        }

        // Fallback: text density scoring
        var densityResult = ExtractByTextDensity(doc);
        if (densityResult.Count >= 3 && ValidateContentQuality(densityResult))
        {
            return true;
        }

        // Fallback: check for largest paragraph block in full document
        var largestBlock = FindLargestParagraphBlock(doc);
        if (largestBlock.Count >= 3 && ValidateContentQuality(largestBlock))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Static version of <see cref="IsArticle"/>. Determines if the HTML content appears
    /// to be an article page based on structural indicators (meta tags, semantic elements,
    /// content container classes, substantial paragraphs).
    /// </summary>
    /// <param name="html">Raw HTML content.</param>
    /// <returns>True if the page appears to be an article.</returns>
    public static bool IsArticlePage(string html)
    {
        return IsArticleCore(html);
    }

    public Task<ReadableContent?> ExtractAsync(string html, string url, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsArticle(html))
            {
                _logger.LogDebug("Page does not appear to be an article: {Url}", url);
                return Task.FromResult<ReadableContent?>(null);
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Detect paywall before boilerplate removal (which strips paywall elements)
            var paywallDoc = new HtmlDocument();
            paywallDoc.LoadHtml(html);

            var title = ExtractTitle(doc);
            var author = ExtractAuthor(doc);
            var publishedDate = ExtractPublishedDate(doc);
            var paragraphs = ExtractParagraphs(doc, url);

            if (paragraphs.Count == 0)
            {
                _logger.LogDebug("No content paragraphs found: {Url}", url);
                return Task.FromResult<ReadableContent?>(null);
            }

            var isPaywalled = DetectPaywall(paywallDoc, paragraphs);
            if (isPaywalled)
            {
                _logger.LogInformation("Paywall detected for {Url} ({ParagraphCount} paragraphs)", url, paragraphs.Count);
            }

            var cleanedText = string.Join("\n\n", paragraphs);

            var content = ReadableContent.Create(
                title ?? "Untitled Article",
                cleanedText,
                paragraphs,
                author,
                publishedDate,
                isPaywalled);

            _logger.LogInformation(
                "Extracted readable content: {Title} ({WordCount} words, {ReadTime} min read{Paywall})",
                content.Title,
                content.WordCount,
                content.EstimatedReadingMinutes,
                content.IsPaywalled ? ", paywalled" : string.Empty);

            return Task.FromResult<ReadableContent?>(content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting readable content from {Url}", url);
            return Task.FromResult<ReadableContent?>(null);
        }
    }

    public bool IsArticle(string html)
    {
        return IsArticleCore(html);
    }

    /// <summary>
    /// Detects whether a page is paywalled by checking for paywall indicator elements
    /// and text patterns, combined with content truncation heuristics.
    /// Only flags as paywalled when BOTH indicators are present AND content appears truncated
    /// (few paragraphs with little total text).
    /// </summary>
    internal static bool DetectPaywall(HtmlDocument doc, IReadOnlyList<string> paragraphs)
    {
        // Check for paywall indicator elements via XPath
        var hasPaywallElement = PaywallElementSelectors.Any(
            selector => doc.DocumentNode.SelectSingleNode(selector) != null);

        // Check for paywall text patterns in the full document text
        var hasPaywallText = false;
        if (!hasPaywallElement)
        {
            var fullText = doc.DocumentNode.InnerText;
            if (!string.IsNullOrEmpty(fullText))
            {
                var lowerText = fullText.ToLowerInvariant();
                hasPaywallText = PaywallTextPatterns.Any(
                    pattern => lowerText.Contains(pattern, StringComparison.Ordinal));
            }
        }

        // Only flag as paywalled if an indicator is found AND content looks truncated
        if (!hasPaywallElement && !hasPaywallText)
        {
            return false;
        }

        // Content with enough paragraphs is not truncated
        if (paragraphs.Count >= PaywallTruncationThreshold)
        {
            return false;
        }

        // Even with few paragraphs, if total content is substantial it's a real short article
        var totalContentLength = paragraphs.Sum(p => p.Length);
        return totalContentLength < PaywallMinContentLength;
    }

    /// <summary>
    /// Validates that extracted paragraphs represent coherent article content rather than
    /// garbage text from JS fragments, table cells, comment sections, or template boilerplate.
    /// </summary>
    internal static bool ValidateContentQuality(IReadOnlyList<string> paragraphs)
    {
        if (paragraphs.Count == 0)
        {
            return false;
        }

        // Reject if total word count < 100 (not enough content to be a real article)
        var totalWords = paragraphs.Sum(p => p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        if (totalWords < 100)
        {
            return false;
        }

        // Reject if average paragraph length < 50 chars (likely fragmented garbage)
        var averageLength = paragraphs.Average(p => p.Length);
        if (averageLength < 50)
        {
            return false;
        }

        // Reject if >50% of paragraphs start with the same word (repeated template text)
        var firstWords = paragraphs
            .Select(p => p.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant())
            .Where(w => w != null)
            .GroupBy(w => w)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (firstWords != null && firstWords.Count() > paragraphs.Count / 2)
        {
            return false;
        }

        // Reject if >30% of total text is non-alphabetic characters (JS/code content)
        var totalChars = paragraphs.Sum(p => p.Length);
        var alphabeticChars = paragraphs.Sum(p => p.Count(char.IsLetter));
        if (totalChars > 0 && (double)alphabeticChars / totalChars < 0.70)
        {
            return false;
        }

        return true;
    }

    private static bool IsArticleCore(string html)
    {
        var lowerHtml = html.ToLowerInvariant();

        // Check for article-related meta tags (actual <meta property="og:type" content="article"> tag)
        if (OgTypeArticleRegex().IsMatch(lowerHtml))
        {
            return true;
        }

        // Check for article tag
        if (lowerHtml.Contains("<article"))
        {
            return true;
        }

        // Check for ARIA role attributes
        if (lowerHtml.Contains("role=\"article\"") || lowerHtml.Contains("role=\"main\""))
        {
            return true;
        }

        // Check for common article indicators in classes or IDs (exact match via DOM)
        if (HasArticleIndicatorElements(html))
        {
            return true;
        }

        // Check for common content container classes
        if (lowerHtml.Contains("entry-content") ||
            lowerHtml.Contains("post-content") ||
            lowerHtml.Contains("article-body") ||
            lowerHtml.Contains("article-content") ||
            lowerHtml.Contains("storybodycompanioncolumn") ||
            lowerHtml.Contains("data-testid=\"article-body\"") ||
            lowerHtml.Contains("wp-block-post-content") ||
            lowerHtml.Contains("td-post-content") ||
            lowerHtml.Contains("gh-content") ||
            lowerHtml.Contains("post-full-content") ||
            lowerHtml.Contains("field--name-body") ||
            lowerHtml.Contains("data-content-type=\"article-body\"") ||
            lowerHtml.Contains("data-content-region="))
        {
            return true;
        }

        // Check for sufficient substantial paragraph content (>50 chars, outside boilerplate)
        if (HasSubstantialParagraphs(html))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether the HTML contains enough substantial paragraphs to indicate article content.
    /// Counts only paragraphs with >50 characters of text content, preferring those inside
    /// &lt;main&gt; or &lt;article&gt; elements when available. When falling back to the full
    /// document, paragraphs inside boilerplate regions (nav, aside, footer) are excluded.
    /// </summary>
    private static bool HasSubstantialParagraphs(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Prefer paragraphs inside <main> or <article> if present
        var scopeNode = doc.DocumentNode.SelectSingleNode("//main") ??
                        doc.DocumentNode.SelectSingleNode("//article");

        var usingFullDocument = scopeNode == null;
        scopeNode ??= doc.DocumentNode;

        var paragraphs = scopeNode.SelectNodes(".//p");
        if (paragraphs == null)
        {
            return false;
        }

        var substantialCount = 0;
        foreach (var p in paragraphs)
        {
            // When scoped to full document, skip paragraphs in boilerplate regions
            if (usingFullDocument && IsInsideBoilerplate(p))
            {
                continue;
            }

            var text = p.InnerText?.Trim();
            if (text != null && text.Length > 50)
            {
                substantialCount++;
                if (substantialCount >= 3)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the HTML contains elements whose class or id exactly matches
    /// one of the <see cref="ArticleIndicators"/>. Uses DOM parsing so that
    /// "article-list" or "navigation-article" do NOT match the indicator "article".
    /// </summary>
    private static bool HasArticleIndicatorElements(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var indicator in ArticleIndicators)
        {
            // XPath: element whose space-separated class list contains the exact indicator word
            var xpath = $".//*[contains(concat(' ', normalize-space(@class), ' '), ' {indicator} ') or translate(@id, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')='{indicator}']";
            if (doc.DocumentNode.SelectSingleNode(xpath) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractTitle(HtmlDocument doc)
    {
        // Try article-specific title selectors
        var titleSelectors = new[]
        {
            "//h1[@class='headline' or contains(@class, 'headline')]",
            "//h1[@itemprop='headline']",
            "//article//h1",
            "//h1[contains(@class, 'title')]",
            "//h1[contains(@class, 'entry-title')]",
            "//h1[contains(@class, 'post-title')]",
            "//h1"
        };

        foreach (var selector in titleSelectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            var text = CleanText(node?.InnerText);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        // Fall back to og:title or <title>
        var ogTitle = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", null!);
        if (!string.IsNullOrWhiteSpace(ogTitle))
        {
            return CleanText(ogTitle);
        }

        var titleTag = doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
        return CleanText(titleTag);
    }

    private static string? ExtractAuthor(HtmlDocument doc)
    {
        // 1. meta[name="author"] — most reliable when it's a real name (not URL)
        var metaNameAuthor = doc.DocumentNode.SelectSingleNode("//meta[@name='author']")?.GetAttributeValue("content", null!);
        if (!string.IsNullOrWhiteSpace(metaNameAuthor) && !IsUrl(metaNameAuthor))
        {
            return CleanAuthorText(metaNameAuthor);
        }

        // 2. JSON-LD structured data
        var jsonLdAuthor = ExtractAuthorFromJsonLd(doc);
        if (!string.IsNullOrWhiteSpace(jsonLdAuthor))
        {
            return CleanAuthorText(jsonLdAuthor);
        }

        // 3. itemprop="author" with nested itemprop="name"
        var itempropAuthor = doc.DocumentNode.SelectSingleNode("//*[@itemprop='author']");
        if (itempropAuthor != null)
        {
            var nestedName = itempropAuthor.SelectSingleNode(".//*[@itemprop='name']");
            var text = CleanText(nestedName?.InnerText ?? itempropAuthor.InnerText);
            text = CleanAuthorText(text);
            if (!string.IsNullOrWhiteSpace(text) && text.Length > 1 && text.Length < 100)
            {
                return text;
            }
        }

        // 4. Byline class/rel selectors
        var bylineSelectors = new[]
        {
            "//*[@class='author' or contains(@class, 'byline')]//a",
            "//*[@class='author' or contains(@class, 'byline')]",
            "//*[@rel='author']"
        };

        foreach (var selector in bylineSelectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            if (node != null)
            {
                var text = CleanText(node.InnerText);
                text = CleanAuthorText(text);
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 1 && text.Length < 100)
                {
                    return text;
                }
            }
        }

        // 5. article:author meta (OG spec — typically a URL)
        var articleAuthor = doc.DocumentNode.SelectSingleNode("//meta[@property='article:author']")?.GetAttributeValue("content", null!);
        if (!string.IsNullOrWhiteSpace(articleAuthor))
        {
            if (!IsUrl(articleAuthor))
            {
                return CleanAuthorText(articleAuthor);
            }

            var nameFromUrl = ExtractNameFromUrl(articleAuthor);
            if (nameFromUrl != null)
            {
                return nameFromUrl;
            }
        }

        // 6. meta[name="author"] URL fallback
        if (!string.IsNullOrWhiteSpace(metaNameAuthor) && IsUrl(metaNameAuthor))
        {
            var nameFromUrl = ExtractNameFromUrl(metaNameAuthor);
            if (nameFromUrl != null)
            {
                return nameFromUrl;
            }
        }

        return null;
    }

    private static string? ExtractAuthorFromJsonLd(HtmlDocument doc)
    {
        var scriptNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scriptNodes == null)
        {
            return null;
        }

        foreach (var script in scriptNodes)
        {
            try
            {
                var json = script.InnerText.Trim();
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                using var jsonDoc = JsonDocument.Parse(json);
                var root = jsonDoc.RootElement;

                // JSON-LD can be wrapped in an array
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        var name = ExtractAuthorFromJsonLdElement(item);
                        if (name != null)
                        {
                            return name;
                        }
                    }
                }
                else
                {
                    var name = ExtractAuthorFromJsonLdElement(root);
                    if (name != null)
                    {
                        return name;
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed JSON-LD, skip
            }
        }

        return null;
    }

    private static string? ExtractAuthorFromJsonLdElement(JsonElement element)
    {
        if (!element.TryGetProperty("author", out var authorElement))
        {
            return null;
        }

        return authorElement.ValueKind switch
        {
            JsonValueKind.String => authorElement.GetString(),
            JsonValueKind.Object => authorElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null,
            JsonValueKind.Array => ExtractAuthorNamesFromArray(authorElement),
            _ => null
        };
    }

    private static string? ExtractAuthorNamesFromArray(JsonElement array)
    {
        var names = new List<string>();

        foreach (var item in array.EnumerateArray())
        {
            string? name = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object => item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    private static bool IsUrl(string text)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string? ExtractNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return null;
        }

        var lastSegment = path.Split('/').LastOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (string.IsNullOrWhiteSpace(lastSegment))
        {
            return null;
        }

        // Skip segments that are numeric IDs or have file extensions
        if (long.TryParse(lastSegment, out _) || lastSegment.Contains('.'))
        {
            return null;
        }

        // Replace hyphens/underscores with spaces and title-case
        var name = lastSegment.Replace('-', ' ').Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
    }

    private static string? CleanAuthorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Reject URLs that slipped through
        if (IsUrl(text))
        {
            return null;
        }

        // Remove common prefixes
        text = Regex.Replace(text, @"^(by|written by|author:)\s*", string.Empty, RegexOptions.IgnoreCase);
        return text.Trim();
    }

    private static DateTime? ExtractPublishedDate(HtmlDocument doc)
    {
        // Try meta tags with date info
        var metaDate = doc.DocumentNode.SelectSingleNode("//meta[@property='article:published_time']")?.GetAttributeValue("content", null!) ??
                      doc.DocumentNode.SelectSingleNode("//meta[@name='pubdate']")?.GetAttributeValue("content", null!) ??
                      doc.DocumentNode.SelectSingleNode("//meta[@name='publishdate']")?.GetAttributeValue("content", null!);

        if (DateTime.TryParse(metaDate, out var date))
        {
            return date;
        }

        // Try time elements
        var timeNode = doc.DocumentNode.SelectSingleNode("//time[@datetime]") ??
                       doc.DocumentNode.SelectSingleNode("//time[@itemprop='datePublished']");
        var timeAttr = timeNode?.GetAttributeValue("datetime", null!);
        if (DateTime.TryParse(timeAttr, out date))
        {
            return date;
        }

        // Try itemprop datePublished
        var datePublished = doc.DocumentNode.SelectSingleNode("//*[@itemprop='datePublished']");
        var contentAttr = datePublished?.GetAttributeValue("content", null!) ?? CleanText(datePublished?.InnerText);
        if (DateTime.TryParse(contentAttr, out date))
        {
            return date;
        }

        // Try class-based date elements
        var dateElement = doc.DocumentNode.SelectSingleNode("//*[contains(@class, 'date') or contains(@class, 'publish')]");
        if (dateElement != null && DateTime.TryParse(CleanText(dateElement.InnerText), out date))
        {
            return date;
        }

        return null;
    }

    private static void RemoveBoilerplate(HtmlDocument doc)
    {
        var boilerplateSelectors = new[]
        {
            // Semantic elements (only top-level, not inside <article>)
            "//nav", "//footer", "//aside",

            // Top-level header only (not article-internal headers which contain title/summary)
            "//header[not(ancestor::article)]",

            // Navigation and layout
            "//*[contains(@class, 'sidebar')]",
            "//*[contains(@class, 'comment')]",
            "//*[contains(@class, 'share')]",
            "//*[contains(@class, 'social')]",

            // Ads and promotions — use word-boundary matching for short patterns like "ad"
            // to avoid matching classes like "heading", "loading", "padding", "breadcrumb"
            "//*[contains(concat(' ', @class, ' '), ' ad ') or contains(concat(' ', @class, '-'), ' ad-')]",
            "//*[contains(@class, 'advertisement')]",
            "//*[contains(@class, 'promo')]",
            "//*[contains(@class, 'sponsor')]",
            "//*[contains(@class, 'promoted')]",
            "//*[contains(@class, 'newsletter')]",

            // Use word-boundary matching for "nav" and "related" to avoid false positives
            "//*[contains(concat(' ', @class, ' '), ' nav ') or contains(concat(' ', @class, '-'), ' nav-')]",
            "//*[contains(concat(' ', @class, ' '), ' related ') or contains(concat(' ', @class, '-'), ' related-')]",

            // Cookie consent and overlays
            "//*[contains(@class, 'cookie')]",
            "//*[contains(@class, 'consent')]",
            "//*[contains(@id, 'cookie')]",
            "//*[contains(@id, 'consent')]",
            "//*[contains(@class, 'gdpr')]",
            "//*[contains(@id, 'gdpr')]",
            "//*[contains(@class, 'privacy-banner')]",
            "//*[contains(@class, 'cookie-banner')]",

            // Popups and modals
            "//*[contains(@class, 'popup')]",
            "//*[contains(@class, 'modal')]",
            "//*[contains(@class, 'overlay')]",
            "//*[contains(@class, 'paywall')]",

            // Paywall and subscription gates — word-boundary match for "gateway" to avoid
            // false positives on NYT's "vi-gateway-container" which wraps article content
            "//*[contains(concat(' ', @class, ' '), ' gateway ') or contains(concat(' ', @class, '-'), ' gateway-')]",
            "//*[contains(@class, 'expanded-dock')]",
            "//*[contains(@class, 'subscriber-gate')]",
            "//*[contains(@class, 'subscribe-wall')]",
            "//*[contains(@class, 'regwall')]",
            "//*[contains(@data-testid, 'paywall')]",

            // Scripts and styles
            "//script", "//style", "//noscript"
        };

        foreach (var selector in boilerplateSelectors)
        {
            var nodes = doc.DocumentNode.SelectNodes(selector);
            if (nodes != null)
            {
                foreach (var node in nodes.ToList())
                {
                    node.Remove();
                }
            }
        }
    }

    private static bool IsInsideBoilerplate(HtmlNode node)
    {
        var current = node.ParentNode;
        while (current != null)
        {
            var tagName = current.Name.ToLowerInvariant();
            if (tagName == "nav" || tagName == "footer" || tagName == "aside")
            {
                return true;
            }

            var classAttr = current.GetAttributeValue("class", string.Empty);
            if (!string.IsNullOrEmpty(classAttr) && HasBoilerplateClass(classAttr))
            {
                return true;
            }

            current = current.ParentNode;
        }

        return false;
    }

    /// <summary>
    /// Checks if a class attribute string contains any boilerplate class name.
    /// Uses word-boundary matching: splits the class attribute on whitespace and hyphens
    /// to avoid false positives (e.g., "heading" matching "ad", "loading" matching "ad").
    /// </summary>
    private static bool HasBoilerplateClass(string classAttr)
    {
        // Split class attribute into individual class tokens, then further split on hyphens
        // to check each segment. E.g., "main-navigation" → ["main", "navigation"]
        var classTokens = classAttr
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in classTokens)
        {
            var tokenLower = token.ToLowerInvariant();

            // Check exact match first (e.g., class="ad")
            if (BoilerplateClasses.Contains(tokenLower))
            {
                return true;
            }

            // Check hyphenated segments (e.g., "ad-container" → "ad" matches)
            var segments = tokenLower.Split('-');
            if (segments.Any(segment => BoilerplateClasses.Contains(segment)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the container element with the highest text density (text length / HTML length)
    /// and extracts paragraphs from it. This helps identify the article body on pages where
    /// no named content area selector matches, by preferring containers with high text-to-markup
    /// ratios over navigation-heavy containers.
    /// </summary>
    private static List<string> ExtractByTextDensity(HtmlDocument doc)
    {
        var candidates = doc.DocumentNode.SelectNodes("//div | //section | //article | //main");
        if (candidates == null)
        {
            return new List<string>();
        }

        HtmlNode? bestCandidate = null;
        var bestScore = 0.0;

        foreach (var candidate in candidates)
        {
            if (IsInsideBoilerplate(candidate))
            {
                continue;
            }

            var innerHtml = candidate.InnerHtml;
            var innerText = candidate.InnerText?.Trim();
            if (string.IsNullOrWhiteSpace(innerText) || innerHtml.Length < 200)
            {
                continue;
            }

            var textLength = innerText.Length;
            var htmlLength = innerHtml.Length;
            var density = (double)textLength / htmlLength;

            // Count substantial text blocks (p, blockquote, li, or divs/sections with direct text >50 chars)
            var substantialCount = CountSubstantialTextBlocks(candidate);
            if (substantialCount < 3)
            {
                continue;
            }

            // Score: density * sqrt(substantialCount) — rewards both density and content volume
            var score = density * Math.Sqrt(substantialCount);
            if (score > bestScore)
            {
                bestScore = score;
                bestCandidate = candidate;
            }
        }

        if (bestCandidate == null)
        {
            return new List<string>();
        }

        // Extract from the best candidate using semantic extraction first, then direct text
        var paragraphs = ExtractSemanticParagraphs(bestCandidate);
        if (paragraphs.Count >= 3)
        {
            return paragraphs;
        }

        // Also try direct text from divs/sections within the best candidate
        var seen = new HashSet<string>(paragraphs);
        var combined = new List<string>(paragraphs);
        var blocks = bestCandidate.SelectNodes(".//div | .//section") ?? Enumerable.Empty<HtmlNode>();
        foreach (var block in blocks)
        {
            var directText = GetDirectTextContent(block);
            if (!string.IsNullOrWhiteSpace(directText) && directText.Length > 50 && seen.Add(directText))
            {
                combined.Add(directText);
            }
        }

        return combined;
    }

    private static int CountSubstantialTextBlocks(HtmlNode container)
    {
        var count = 0;

        // Count semantic elements with >50 chars
        var semanticNodes = container.SelectNodes(".//p | .//blockquote | .//li");
        if (semanticNodes != null)
        {
            count += semanticNodes.Count(n => (n.InnerText?.Trim().Length ?? 0) > 50);
        }

        // Count divs/sections with direct text >50 chars
        var blocks = container.SelectNodes(".//div | .//section");
        if (blocks != null)
        {
            foreach (var block in blocks)
            {
                var directText = GetDirectTextContent(block);
                if (!string.IsNullOrWhiteSpace(directText) && directText.Length > 50)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static List<string> ExtractParagraphsAlternative(HtmlNode contentArea)
    {
        var paragraphs = new List<string>();

        // Get all text nodes with substantial content
        var textNodes = contentArea.DescendantsAndSelf()
            .Where(n => n.NodeType == HtmlNodeType.Text)
            .Select(n => CleanText(n.InnerText))
            .Where(t => !string.IsNullOrWhiteSpace(t) && t!.Length > 100)
            .Cast<string>()
            .ToList();

        foreach (var text in textNodes)
        {
            // Split by sentences and group into paragraphs
            var sentences = SentenceRegex().Split(text);
            var currentParagraph = new List<string>();

            foreach (var sentence in sentences)
            {
                var trimmed = sentence.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                currentParagraph.Add(trimmed);

                // Create paragraph every 3-5 sentences
                if (currentParagraph.Count >= 3)
                {
                    paragraphs.Add(string.Join(" ", currentParagraph));
                    currentParagraph.Clear();
                }
            }

            // Add remaining sentences
            if (currentParagraph.Count > 0)
            {
                paragraphs.Add(string.Join(" ", currentParagraph));
            }
        }

        return paragraphs;
    }

    private static List<string> ExtractSemanticParagraphs(HtmlNode contentArea)
    {
        var paragraphs = new List<string>();
        var seen = new HashSet<string>();
        var paragraphNodes = contentArea.SelectNodes(".//p | .//blockquote | .//li") ?? Enumerable.Empty<HtmlNode>();

        foreach (var node in paragraphNodes)
        {
            if (IsInsideBoilerplate(node))
            {
                continue;
            }

            var text = CleanText(node.InnerText);

            if (!string.IsNullOrWhiteSpace(text) && text.Length > 50 && seen.Add(text))
            {
                if (node.Name.Equals("blockquote", StringComparison.OrdinalIgnoreCase))
                {
                    paragraphs.Add($"\u201c{text}\u201d");
                }
                else
                {
                    paragraphs.Add(text);
                }
            }
        }

        return paragraphs;
    }

    private static List<string> FindLargestParagraphBlock(HtmlDocument doc)
    {
        var allParagraphs = doc.DocumentNode.SelectNodes("//p");
        if (allParagraphs == null || allParagraphs.Count == 0)
        {
            return new List<string>();
        }

        // Group consecutive <p> elements by their parent
        var groups = new List<List<HtmlNode>>();
        List<HtmlNode>? currentGroup = null;
        HtmlNode? lastParent = null;

        foreach (var p in allParagraphs)
        {
            if (IsInsideBoilerplate(p))
            {
                continue;
            }

            var parent = p.ParentNode;
            if (parent != lastParent)
            {
                currentGroup = new List<HtmlNode>();
                groups.Add(currentGroup);
                lastParent = parent;
            }

            currentGroup!.Add(p);
        }

        // Score each group: total text length of substantial paragraphs
        List<string>? bestBlock = null;
        var bestScore = 0;

        foreach (var group in groups)
        {
            var blockParagraphs = new List<string>();
            var totalLength = 0;

            foreach (var p in group)
            {
                var text = CleanText(p.InnerText);
                if (!string.IsNullOrWhiteSpace(text) && text.Length > 50)
                {
                    blockParagraphs.Add(text);
                    totalLength += text.Length;
                }
            }

            if (blockParagraphs.Count >= 3 && totalLength > bestScore)
            {
                bestScore = totalLength;
                bestBlock = blockParagraphs;
            }
        }

        return bestBlock ?? new List<string>();
    }

    private static string? GetDirectTextContent(HtmlNode node)
    {
        // Collect text from direct text nodes and inline elements only
        var textParts = new List<string>();
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text || IsInlineElement(child.Name))
            {
                var text = child.InnerText?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    textParts.Add(text);
                }
            }
        }

        if (textParts.Count == 0)
        {
            return null;
        }

        return CleanText(string.Join(" ", textParts));
    }

    private static bool IsInlineElement(string tagName)
    {
        return tagName.ToLowerInvariant() switch
        {
            "a" or "span" or "strong" or "b" or "em" or "i" or "u" or "small" or "mark" or "sub" or "sup" or "abbr" or "code" => true,
            _ => false
        };
    }

    private static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Decode HTML entities
        text = System.Net.WebUtility.HtmlDecode(text);

        // Normalize whitespace
        text = WhitespaceRegex().Replace(text, " ");

        return text.Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceRegex();

    /// <summary>
    /// Matches the actual og:type article meta tag: &lt;meta property="og:type" content="article"&gt;
    /// Handles both single and double quotes, with optional whitespace between attributes.
    /// </summary>
    [GeneratedRegex("""<meta\s[^>]*property\s*=\s*["']og:type["'][^>]*content\s*=\s*["']article["']""")]
    private static partial Regex OgTypeArticleRegex();

    /// <summary>
    /// Aggregates paragraphs from ALL elements matching a given XPath selector.
    /// Handles sites like NYT that split article content across multiple sibling elements
    /// (e.g., multiple StoryBodyCompanionColumn divs, each with 1-2 paragraphs).
    /// </summary>
    private static List<string> AggregateContentAreas(HtmlDocument doc, string selector)
    {
        var nodes = doc.DocumentNode.SelectNodes(selector);
        if (nodes == null || nodes.Count <= 1)
        {
            return new List<string>();
        }

        var paragraphs = new List<string>();
        var seen = new HashSet<string>();

        foreach (var node in nodes)
        {
            foreach (var p in ExtractSemanticParagraphs(node).Where(p => seen.Add(p)))
            {
                paragraphs.Add(p);
            }
        }

        return paragraphs;
    }

    private List<(HtmlNode Node, string Selector)> FindAllContentAreas(HtmlDocument doc, string? url = null)
    {
        var results = new List<(HtmlNode Node, string Selector)>();

        foreach (var selector in ContentAreaSelectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            if (node != null)
            {
                results.Add((node, selector));
            }
        }

        if (results.Count > 0)
        {
            _logger.LogDebug("Content area matched by selector: {Selector} for {Url}", results[0].Selector, url ?? "unknown");
        }
        else
        {
            _logger.LogDebug("No content area selector matched for {Url}", url ?? "unknown");
        }

        return results;
    }

    private List<string> ExtractParagraphs(HtmlDocument doc, string? url = null)
    {
        // Remove boilerplate content first
        RemoveBoilerplate(doc);

        // Try to find the main content areas (ordered by specificity)
        var contentAreas = FindAllContentAreas(doc, url);

        // Level 1.5 (before Level 1): Aggregate paragraphs across all elements matching the same selector.
        // Sites like NYT split articles across multiple StoryBodyCompanionColumn divs,
        // each with 1-2 paragraphs. Aggregating first ensures we get the full article
        // rather than returning early with just one fragment that happens to have >= 3 paragraphs.
        if (contentAreas.Count > 0)
        {
            var aggregated = AggregateContentAreas(doc, contentAreas[0].Selector);
            if (aggregated.Count >= 3)
            {
                _logger.LogDebug("Aggregated extraction succeeded with selector {Selector} ({Count} paragraphs)", contentAreas[0].Selector, aggregated.Count);
                return aggregated;
            }
        }

        // Level 1: Try semantic elements in each content area (fallback when aggregation didn't help)
        foreach (var (area, selector) in contentAreas)
        {
            var paragraphs = ExtractSemanticParagraphs(area);
            if (paragraphs.Count >= 3)
            {
                _logger.LogDebug("Level 1 extraction succeeded with selector {Selector} ({Count} paragraphs)", selector, paragraphs.Count);
                return paragraphs;
            }
        }

        // Use best content area or full document for remaining levels
        var contentArea = contentAreas.Count > 0 ? contentAreas[0].Node : doc.DocumentNode;
        if (contentAreas.Count == 0)
        {
            _logger.LogDebug("No content area found, using full document");
        }

        // Try Level 1 on the chosen area (may be full document if no areas matched)
        var level1Paragraphs = ExtractSemanticParagraphs(contentArea);
        if (level1Paragraphs.Count >= 3)
        {
            return level1Paragraphs;
        }

        // Level 2: Try divs and sections with direct text content
        var seen = new HashSet<string>(level1Paragraphs);
        var paragraphsWithDivs = new List<string>(level1Paragraphs);
        var blockNodes = contentArea.SelectNodes(".//div | .//section") ?? Enumerable.Empty<HtmlNode>();
        foreach (var block in blockNodes)
        {
            if (IsInsideBoilerplate(block))
            {
                continue;
            }

            var directText = GetDirectTextContent(block);
            if (!string.IsNullOrWhiteSpace(directText) && directText.Length > 50 && seen.Add(directText))
            {
                paragraphsWithDivs.Add(directText);
            }
        }

        if (paragraphsWithDivs.Count >= 3)
        {
            return paragraphsWithDivs;
        }

        // Level 2.5a: Text density scoring — find best container by text-to-HTML ratio
        var densityResult = ExtractByTextDensity(doc);
        if (densityResult.Count >= 3 && ValidateContentQuality(densityResult))
        {
            _logger.LogDebug("Text density scoring found {Count} paragraphs", densityResult.Count);
            return densityResult;
        }
        else if (densityResult.Count >= 3)
        {
            _logger.LogDebug("Text density result failed quality validation ({Count} paragraphs)", densityResult.Count);
        }

        // Level 2.5b: Largest paragraph block heuristic
        var largestBlock = FindLargestParagraphBlock(doc);
        if (largestBlock.Count >= 3 && ValidateContentQuality(largestBlock))
        {
            _logger.LogDebug("Largest paragraph block heuristic found {Count} paragraphs", largestBlock.Count);
            return largestBlock;
        }
        else if (largestBlock.Count >= 3)
        {
            _logger.LogDebug("Largest paragraph block failed quality validation ({Count} paragraphs)", largestBlock.Count);
        }

        // Level 3: Aggressive alternative extraction
        _logger.LogDebug("Few paragraphs found ({Count}), trying alternative extraction", paragraphsWithDivs.Count);
        var alternative = ExtractParagraphsAlternative(contentArea);
        if (alternative.Count > paragraphsWithDivs.Count && ValidateContentQuality(alternative))
        {
            return alternative;
        }
        else if (alternative.Count > paragraphsWithDivs.Count)
        {
            _logger.LogDebug("Alternative extraction failed quality validation ({Count} paragraphs)", alternative.Count);
        }

        // Return whatever we have from earlier levels (may be empty, handled by caller)
        return paragraphsWithDivs;
    }
}
