// Educational and personal use only.

using System.Text.Json;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Extracts and classifies links from HTML content.
/// </summary>
public class LinkExtractor : ILinkExtractor
{
    private static readonly HashSet<string> NavigationParentTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nav", "header", "aside"
    };

    private static readonly HashSet<string> FooterParentTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "footer"
    };

    private static readonly HashSet<string> NavigationClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "nav", "navigation", "menu", "sidebar", "header", "toolbar",
        "related", "widget", "recommended", "trending", "popular", "aside"
    };

    private static readonly HashSet<string> FooterClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "footer", "foot", "bottom"
    };

    private static readonly HashSet<string> ContentParentTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "article", "main"
    };

    private static readonly HashSet<string> ContentClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "content", "article", "story", "post", "entry", "main", "card", "headline", "featured"
    };

    private static readonly HashSet<string> AdSponsorPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "sponsored", "advertisement", "ad:", "created for", "created by",
        "promoted", "partner content", "paid content", "special advertising"
    };

    private static readonly HashSet<string> AdSponsorClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ad", "ads", "advert", "advertisement", "sponsor", "sponsored",
        "promo", "promotion", "partner", "paid"
    };

    private static readonly HashSet<string> ContainerTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "article", "div", "li", "section"
    };

    private static readonly HashSet<string> SectionContainerTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "section", "article"
    };

    private static readonly HashSet<string> HeadingTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "h1", "h2", "h3", "h4", "h5", "h6"
    };

    private static readonly HashSet<string> GenericSectionTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "more", "also", "trending", "most read", "most popular",
        "recommended", "related", "see also", "latest", "you might like"
    };

    private readonly ILogger<LinkExtractor> _logger;

    public LinkExtractor(ILogger<LinkExtractor> logger)
    {
        _logger = logger;
    }

    public Task<List<LinkInfo>> ExtractLinksAsync(string html, string baseUrl, CancellationToken cancellationToken = default)
    {
        var links = new List<LinkInfo>();

        _logger.LogDebug("ExtractLinksAsync: html length = {Length}, baseUrl = {BaseUrl}", html?.Length ?? 0, baseUrl);

        if (string.IsNullOrEmpty(html))
        {
            return Task.FromResult(links);
        }

        try
        {
            var doc = new HtmlDocument();

            // Enable options to better handle malformed HTML
            doc.OptionFixNestedTags = true;
            doc.OptionAutoCloseOnEnd = true;
            doc.LoadHtml(html);

            var anchorNodes = doc.DocumentNode.SelectNodes("//a[@href]");
            _logger.LogDebug("Found {Count} anchor nodes", anchorNodes?.Count ?? 0);

            if (anchorNodes == null)
            {
                _logger.LogDebug("No anchor tags found in HTML");
                return Task.FromResult(links);
            }

            var baseUri = new Uri(baseUrl);
            _logger.LogInformation("Base URI for link resolution: {BaseUri}", baseUri);

            var skippedNoHref = 0;
            var skippedNoUrl = 0;
            var skippedNoText = 0;
            var skippedAd = 0;

            foreach (var anchor in anchorNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var href = anchor.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                {
                    skippedNoHref++;
                    continue;
                }

                // Skip category label links (CMS metadata, not meaningful as standalone links)
                var dataLinkType = anchor.GetAttributeValue("data-link-type", string.Empty);
                if (dataLinkType.Equals("article category label", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var absoluteUrl = ResolveUrl(href, baseUri);
                    if (string.IsNullOrEmpty(absoluteUrl))
                    {
                        skippedNoUrl++;
                        continue;
                    }

                    var (displayText, isFromImage) = GetDisplayTextWithSource(anchor);
                    if (string.IsNullOrWhiteSpace(displayText))
                    {
                        skippedNoText++;
                        continue;
                    }

                    var parentSelector = GetParentSelector(anchor);

                    // Filter out ads and sponsored content
                    if (IsAdOrSponsoredLink(displayText, parentSelector))
                    {
                        _logger.LogDebug("Filtered ad/sponsor link: {Text}", displayText);
                        skippedAd++;
                        continue;
                    }

                    var linkInfo = ClassifyLink(absoluteUrl, displayText, parentSelector, baseUrl);

                    // Set ARIA label if present
                    var ariaLabel = anchor.GetAttributeValue("aria-label", null!);
                    if (!string.IsNullOrWhiteSpace(ariaLabel))
                    {
                        linkInfo = linkInfo with { AriaLabel = ariaLabel };
                    }

                    // Track whether this came from image alt text
                    linkInfo = linkInfo with { IsFromImageAlt = isFromImage };

                    // Extract per-link metadata (author, date) from container
                    var (author, pubDate) = ExtractLinkMetadata(anchor);
                    if (author != null || pubDate != null)
                    {
                        linkInfo = linkInfo with { Author = author, PublishedDate = pubDate };
                    }

                    // Extract section title for content links only
                    if (linkInfo.Type == LinkType.Content)
                    {
                        var sectionTitle = ExtractSectionTitle(anchor);
                        if (sectionTitle != null)
                        {
                            linkInfo = linkInfo with { SectionTitle = sectionTitle };
                        }
                    }

                    links.Add(linkInfo);
                }
                catch (UriFormatException ex)
                {
                    _logger.LogDebug(ex, "Invalid URL: {Href}", href);
                }
            }

            // Apply page-level metadata fallback for links without per-container metadata
            var (pageAuthor, pageDate) = ExtractPageLevelMetadata(doc);
            for (var i = 0; i < links.Count; i++)
            {
                var link = links[i];
                if (link.Type == LinkType.Content && link.Author == null && link.PublishedDate == null &&
                    (pageAuthor != null || pageDate != null))
                {
                    links[i] = link with
                    {
                        Author = link.Author ?? pageAuthor,
                        PublishedDate = link.PublishedDate ?? pageDate
                    };
                }
            }

            // Group same-URL links within the same parent, then deduplicate across parents
            var deduplicatedLinks = GroupLinksByUrl(links);

            _logger.LogInformation(
                "Extracted {Count} links ({Deduped} unique)",
                links.Count,
                deduplicatedLinks.Count);

            links = deduplicatedLinks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting links from HTML");
        }

        return Task.FromResult(links);
    }

    public LinkInfo ClassifyLink(string url, string displayText, string? parentSelector, string baseUrl)
    {
        var linkType = DetermineLinkType(url, parentSelector, displayText, baseUrl);
        var importance = CalculateImportance(linkType, displayText, parentSelector);

        return new LinkInfo
        {
            Url = url,
            DisplayText = displayText.Trim(),
            Type = linkType,
            ImportanceScore = importance,
            ParentSelector = parentSelector
        };
    }

    /// <summary>
    /// Groups links that share the same URL and parent element, merging their display text.
    /// Then deduplicates across different parents, keeping the best representative per URL.
    /// </summary>
    internal static List<LinkInfo> GroupLinksByUrl(List<LinkInfo> links)
    {
        // Phase 1: Build adjacency runs — only merge links that are immediately adjacent
        // in document order AND share the same URL (case-insensitive) + ParentSelector (exact).
        // This prevents incorrect merging of same-URL links in different DOM cards that happen
        // to produce identical ParentSelector strings.
        var runs = new List<List<(LinkInfo Link, int OriginalIndex)>>();

        if (links.Count > 0)
        {
            var currentRun = new List<(LinkInfo Link, int OriginalIndex)> { (links[0], 0) };

            for (var i = 1; i < links.Count; i++)
            {
                var prev = links[i - 1];
                var curr = links[i];

                if (string.Equals(curr.Url, prev.Url, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(curr.ParentSelector, prev.ParentSelector, StringComparison.Ordinal))
                {
                    currentRun.Add((curr, i));
                }
                else
                {
                    runs.Add(currentRun);
                    currentRun = new List<(LinkInfo Link, int OriginalIndex)> { (curr, i) };
                }
            }

            runs.Add(currentRun);
        }

        // Merge each run's display text, preserving the same logic as before
        var merged = new List<(LinkInfo Link, int FirstIndex)>();

        foreach (var group in runs)
        {
            var firstIndex = group[0].OriginalIndex;

            if (group.Count == 1)
            {
                merged.Add((group[0].Link, firstIndex));
                continue;
            }

            // Collect display texts in document order, separating real text from image alt
            var realTexts = new List<string>();
            var imageTexts = new List<string>();

            foreach (var (link, _) in group)
            {
                var text = link.DisplayText.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (link.IsFromImageAlt)
                {
                    imageTexts.Add(text);
                }
                else
                {
                    realTexts.Add(text);
                }
            }

            // Merge: prefer real text; only use image alt as fallback when no real text exists
            var allTexts = new List<string>(realTexts);
            if (realTexts.Count == 0)
            {
                allTexts.AddRange(imageTexts);
            }

            // Remove texts that are substrings of other texts in the group
            var filtered = new List<string>();
            for (var i = 0; i < allTexts.Count; i++)
            {
                var isSubstring = false;
                for (var j = 0; j < allTexts.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    if (allTexts[j].Contains(allTexts[i], StringComparison.OrdinalIgnoreCase) &&
                        !allTexts[i].Equals(allTexts[j], StringComparison.OrdinalIgnoreCase))
                    {
                        isSubstring = true;
                        break;
                    }
                }

                if (!isSubstring)
                {
                    filtered.Add(allTexts[i]);
                }
            }

            // Deduplicate identical texts (case-insensitive)
            var distinct = filtered
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var mergedText = string.Join(": ", distinct);
            if (string.IsNullOrWhiteSpace(mergedText))
            {
                mergedText = group[0].Link.DisplayText;
            }

            var maxImportance = group.Max(g => g.Link.ImportanceScore);
            var firstLink = group[0].Link;

            // Use non-image-alt status if any real text was found
            var isFromImage = realTexts.Count == 0 && imageTexts.Count > 0;

            // Propagate author/date/sectionTitle from first link that has them
            var author = group.Select(g => g.Link.Author).FirstOrDefault(a => a != null);
            var pubDate = group.Select(g => g.Link.PublishedDate).FirstOrDefault(d => d != null);
            var sectionTitle = group.Select(g => g.Link.SectionTitle).FirstOrDefault(s => s != null);

            var mergedLink = firstLink with
            {
                DisplayText = mergedText,
                ImportanceScore = maxImportance,
                IsFromImageAlt = isFromImage,
                Author = author,
                PublishedDate = pubDate,
                SectionTitle = sectionTitle
            };

            merged.Add((mergedLink, firstIndex));
        }

        // Phase 2: Cross-parent URL dedup — keep the best representative per URL
        var result = new List<LinkInfo>();
        var seenUrls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (link, _) in merged)
        {
            if (seenUrls.TryGetValue(link.Url, out var existingIndex))
            {
                var existing = result[existingIndex];

                // Replace if new entry has higher importance, same importance but longer text,
                // or existing is image alt but new one is real text
                if (link.ImportanceScore > existing.ImportanceScore ||
                    (link.ImportanceScore == existing.ImportanceScore &&
                     link.DisplayText.Length > existing.DisplayText.Length) ||
                    (existing.IsFromImageAlt && !link.IsFromImageAlt))
                {
                    // Preserve metadata from existing if replacement doesn't have it
                    result[existingIndex] = link with
                    {
                        Author = link.Author ?? existing.Author,
                        PublishedDate = link.PublishedDate ?? existing.PublishedDate,
                        SectionTitle = link.SectionTitle ?? existing.SectionTitle
                    };
                }
                else if ((existing.Author == null && link.Author != null) ||
                         (existing.PublishedDate == null && link.PublishedDate != null) ||
                         (existing.SectionTitle == null && link.SectionTitle != null))
                {
                    // Propagate metadata to existing entry if it's missing
                    result[existingIndex] = existing with
                    {
                        Author = existing.Author ?? link.Author,
                        PublishedDate = existing.PublishedDate ?? link.PublishedDate,
                        SectionTitle = existing.SectionTitle ?? link.SectionTitle
                    };
                }
            }
            else
            {
                seenUrls[link.Url] = result.Count;
                result.Add(link);
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts author and publication date metadata from the container element surrounding a link.
    /// Walks up to 3 ancestor levels looking for article/div/li/section containers, then searches
    /// for &lt;time datetime&gt; elements and author indicators.
    /// </summary>
    internal static (string? Author, DateTime? PublishedDate) ExtractLinkMetadata(HtmlNode anchor)
    {
        // Find a meaningful container by walking up to 3 ancestor levels
        var container = FindContainer(anchor);
        if (container == null)
        {
            return (null, null);
        }

        var author = ExtractAuthor(container);
        var pubDate = ExtractPublishedDate(container);

        return (author, pubDate);
    }

    /// <summary>
    /// Extracts the nearest section heading for a content link by walking up the DOM.
    /// Checks section/article/div[role=region] containers for aria-label or direct child headings.
    /// </summary>
    internal static string? ExtractSectionTitle(HtmlNode anchor)
    {
        var current = anchor.ParentNode;
        var depth = 0;

        while (current != null && depth < 5 && current.Name != "#document")
        {
            if (IsSectionContainer(current))
            {
                // Check aria-label first
                var ariaLabel = current.GetAttributeValue("aria-label", null!);
                if (!string.IsNullOrWhiteSpace(ariaLabel) && IsValidSectionTitle(ariaLabel))
                {
                    return ariaLabel.Trim();
                }

                // Check direct child headings (h1-h6)
                var heading = FindDirectChildHeading(current);
                if (heading != null)
                {
                    return heading;
                }
            }

            current = current.ParentNode;
            depth++;
        }

        return null;
    }

    /// <summary>
    /// Extracts page-level author and date from JSON-LD, meta tags, and other page-wide indicators.
    /// Used as fallback when per-link container extraction doesn't find metadata.
    /// </summary>
    internal static (string? Author, DateTime? PublishedDate) ExtractPageLevelMetadata(HtmlDocument doc)
    {
        var author = ExtractPageAuthorFromJsonLd(doc);
        var pubDate = ExtractPageDateFromMeta(doc);

        if (author == null)
        {
            // Fallback to meta[name="author"]
            var authorMeta = doc.DocumentNode.SelectSingleNode("//meta[@name='author']");
            var content = authorMeta?.GetAttributeValue("content", null!);
            if (!string.IsNullOrWhiteSpace(content) && !content.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                author = content.Trim();
            }
        }

        return (author, pubDate);
    }

    private static bool IsSectionContainer(HtmlNode node)
    {
        if (SectionContainerTags.Contains(node.Name))
        {
            return true;
        }

        // div with role="region"
        if (node.Name.Equals("div", StringComparison.OrdinalIgnoreCase))
        {
            var role = node.GetAttributeValue("role", string.Empty);
            if (role.Equals("region", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FindDirectChildHeading(HtmlNode container)
    {
        foreach (var child in container.ChildNodes.Where(child => HeadingTags.Contains(child.Name)))
        {
            var text = child.InnerText?.Trim();
            text = System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

            if (IsValidSectionTitle(text))
            {
                return text;
            }
        }

        return null;
    }

    private static bool IsValidSectionTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var trimmed = title.Trim();

        if (trimmed.Length < 3 || trimmed.Length > 80)
        {
            return false;
        }

        if (GenericSectionTitles.Contains(trimmed))
        {
            return false;
        }

        return true;
    }

    private static string? ExtractPageAuthorFromJsonLd(HtmlDocument doc)
    {
        var scripts = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scripts == null)
        {
            return null;
        }

        foreach (var script in scripts)
        {
            try
            {
                var json = script.InnerText.Trim();
                using var jsonDoc = JsonDocument.Parse(json);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("author", out var authorElement))
                {
                    return ParseJsonLdAuthor(authorElement);
                }
            }
            catch (JsonException)
            {
                // Malformed JSON-LD, skip
            }
        }

        return null;
    }

    private static string? ParseJsonLdAuthor(JsonElement authorElement)
    {
        if (authorElement.ValueKind == JsonValueKind.String)
        {
            return authorElement.GetString()?.Trim();
        }

        if (authorElement.ValueKind == JsonValueKind.Object &&
            authorElement.TryGetProperty("name", out var nameEl))
        {
            return nameEl.GetString()?.Trim();
        }

        if (authorElement.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var item in authorElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("name", out var n))
                {
                    var name = n.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
                else if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
            }

            return names.Count > 0 ? string.Join(", ", names) : null;
        }

        return null;
    }

    private static DateTime? ExtractPageDateFromMeta(HtmlDocument doc)
    {
        // Try common meta date properties
        var dateSelectors = new[]
        {
            "//meta[@property='article:published_time']",
            "//meta[@property='og:article:published_time']",
            "//meta[@name='publish-date']",
            "//meta[@name='date']",
            "//meta[@name='DC.date.issued']"
        };

        foreach (var selector in dateSelectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            var content = node?.GetAttributeValue("content", null!);
            if (!string.IsNullOrWhiteSpace(content) &&
                DateTimeOffset.TryParse(
                    content,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var dto))
            {
                return dto.LocalDateTime;
            }
        }

        return null;
    }

    private static HtmlNode? FindContainer(HtmlNode node)
    {
        var current = node.ParentNode;
        var depth = 0;

        while (current != null && depth < 3)
        {
            if (ContainerTags.Contains(current.Name))
            {
                return current;
            }

            current = current.ParentNode;
            depth++;
        }

        return null;
    }

    private static string? ExtractAuthor(HtmlNode container)
    {
        // Look for elements with author-related classes
        var authorClasses = new[] { "author", "byline", "writer" };

        foreach (var className in authorClasses)
        {
            var authorNode = container.SelectSingleNode(
                $".//*[contains(translate(@class, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), '{className}')]");
            if (authorNode != null)
            {
                var text = authorNode.InnerText?.Trim();
                text = System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= 100)
                {
                    return text;
                }
            }
        }

        // Check for rel="author" attribute
        var relAuthor = container.SelectSingleNode(".//*[@rel='author']");
        if (relAuthor != null)
        {
            var text = relAuthor.InnerText?.Trim();
            text = System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= 100)
            {
                return text;
            }
        }

        // NYT-style: look for spans with "By" prefix inside the container
        var spans = container.SelectNodes(".//span | .//p");
        if (spans != null)
        {
            foreach (var span in spans)
            {
                var text = span.InnerText?.Trim();
                if (text != null && text.StartsWith("By ", StringComparison.OrdinalIgnoreCase) && text.Length <= 100)
                {
                    return text[3..].Trim();
                }
            }
        }

        return null;
    }

    private static DateTime? ExtractPublishedDate(HtmlNode container)
    {
        // Look for <time datetime="..."> elements
        var timeNodes = container.SelectNodes(".//time[@datetime]");
        if (timeNodes != null)
        {
            foreach (var timeNode in timeNodes)
            {
                var datetime = timeNode.GetAttributeValue("datetime", string.Empty);
                if (!string.IsNullOrWhiteSpace(datetime) &&
                    DateTimeOffset.TryParse(
                        datetime,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var dto))
                {
                    return dto.LocalDateTime;
                }
            }
        }

        return null;
    }

    private static LinkType DetermineLinkType(string url, string? parentSelector, string displayText, string baseUrl)
    {
        // Check if external link
        if (IsExternalLink(url, baseUrl))
        {
            return LinkType.External;
        }

        // Check parent selector for classification
        if (!string.IsNullOrEmpty(parentSelector))
        {
            var selectorLower = parentSelector.ToLowerInvariant();

            // Extract tag names and class names separately for better matching
            // Format: "tag.class1.class2 > tag#id.class3"
            var tagMatches = System.Text.RegularExpressions.Regex.Matches(selectorLower, @"(?:^|\s|>)([a-z]+)(?:\.|#|\s|>|$)");
            var tagNames = tagMatches.Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var classMatches = System.Text.RegularExpressions.Regex.Matches(selectorLower, @"\.([a-z0-9_-]+)");
            var classNames = classMatches.Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Check for content elements FIRST (article, main are most specific)
            // This prevents "home-header" from incorrectly matching navigation
            if (tagNames.Any(tag => ContentParentTags.Contains(tag)) ||
                classNames.Any(cls => ContentClasses.Contains(cls)))
            {
                return LinkType.Content;
            }

            // Check for footer elements
            if (tagNames.Any(tag => FooterParentTags.Contains(tag)) ||
                classNames.Any(cls => FooterClasses.Contains(cls)))
            {
                return LinkType.Footer;
            }

            // Check for navigation elements (nav, header tags - NOT class names containing "header")
            if (tagNames.Any(tag => NavigationParentTags.Contains(tag)) ||
                classNames.Any(cls => NavigationClasses.Contains(cls)))
            {
                return LinkType.Navigation;
            }
        }

        // Heuristic based on display text length
        // Short text (< 50 chars) is more likely to be navigation
        // Longer text is more likely to be content (article titles)
        if (displayText.Length < 50)
        {
            return LinkType.Navigation;
        }

        return LinkType.Content;
    }

    private static int CalculateImportance(LinkType type, string displayText, string? parentSelector)
    {
        var importance = type switch
        {
            LinkType.Content => 70,
            LinkType.Navigation => 30,
            LinkType.Footer => 10,
            LinkType.External => 20,
            _ => 50
        };

        // Boost importance for longer, more descriptive text
        if (displayText.Length > 50)
        {
            importance += 15;
        }
        else if (displayText.Length > 30)
        {
            importance += 10;
        }

        // Boost for content area links
        if (!string.IsNullOrEmpty(parentSelector))
        {
            var selectorLower = parentSelector.ToLowerInvariant();
            if (selectorLower.Contains("article") || selectorLower.Contains("main"))
            {
                importance += 10;
            }
        }

        return Math.Min(100, importance);
    }

    private static bool IsAdOrSponsoredLink(string displayText, string? parentSelector)
    {
        var textLower = displayText.ToLowerInvariant();

        // Check display text for ad/sponsor patterns
        if (AdSponsorPatterns.Any(pattern => textLower.Contains(pattern.ToLowerInvariant())))
        {
            return true;
        }

        // Check parent selector for ad-related classes
        // Use word boundary matching to avoid false positives (e.g., "head" matching "ad")
        if (!string.IsNullOrEmpty(parentSelector))
        {
            var selectorLower = parentSelector.ToLowerInvariant();

            // Extract class names from selector (classes are prefixed with .)
            // Format: "tag.class1.class2 > tag#id.class3"
            var classMatches = System.Text.RegularExpressions.Regex.Matches(selectorLower, @"\.([a-z0-9_-]+)");
            var classNames = classMatches.Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (classNames.Any(cls => AdSponsorClasses.Contains(cls)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExternalLink(string url, string baseUrl)
    {
        try
        {
            var linkUri = new Uri(url);
            var baseUri = new Uri(baseUrl);

            // Get base domain (handle subdomains)
            var linkHost = linkUri.Host.ToLowerInvariant();
            var baseHost = baseUri.Host.ToLowerInvariant();

            // Exact match
            if (linkHost.Equals(baseHost, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Check if link is a subdomain of base or vice versa
            var linkDomain = GetBaseDomain(linkHost);
            var baseDomain = GetBaseDomain(baseHost);

            return !linkDomain.Equals(baseDomain, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetBaseDomain(string host)
    {
        // Simple extraction of base domain (last two parts for most TLDs)
        var parts = host.Split('.');
        if (parts.Length <= 2)
        {
            return host;
        }

        // Handle common multi-part TLDs like .co.uk, .com.au
        var lastPart = parts[^1];
        var secondLastPart = parts[^2];

        if ((lastPart.Length == 2 && secondLastPart.Length <= 3) ||
            secondLastPart is "co" or "com" or "net" or "org" or "gov" or "edu")
        {
            // Multi-part TLD, take last 3 parts
            return parts.Length >= 3
                ? string.Join(".", parts[^3], parts[^2], parts[^1])
                : host;
        }

        // Standard TLD, take last 2 parts
        return string.Join(".", parts[^2], parts[^1]);
    }

    private static (string Text, bool IsFromImage) GetDisplayTextWithSource(HtmlNode anchor)
    {
        // First try to get direct text content (visible on the page)
        var text = anchor.InnerText?.Trim() ?? string.Empty;

        // Clean up whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        // If we have actual visible text content, use it
        if (!string.IsNullOrWhiteSpace(text))
        {
            return (text, false);
        }

        // If no visible text, try img alt text (mark as from image)
        // This is the only non-visible fallback we use, since alt text
        // typically describes the image content meaningfully.
        var img = anchor.SelectSingleNode(".//img[@alt]");
        if (img != null)
        {
            text = img.GetAttributeValue("alt", string.Empty);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return (text, true);
            }
        }

        // Skip aria-label, title, and other non-visible attributes as display text.
        // These are accessibility/tooltip metadata (e.g., "article link"), not meaningful
        // page content. aria-label is stored separately in LinkInfo.AriaLabel if needed.
        return (string.Empty, false);
    }

    private static string GetParentSelector(HtmlNode node)
    {
        var parts = new List<string>();
        var current = node.ParentNode;
        var depth = 0;
        const int maxDepth = 5;

        while (current != null && depth < maxDepth && current.Name != "#document")
        {
            var part = current.Name;

            // Add class names if present
            var classAttr = current.GetAttributeValue("class", string.Empty);
            if (!string.IsNullOrWhiteSpace(classAttr))
            {
                var classes = classAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (classes.Length > 0)
                {
                    part += "." + string.Join(".", classes.Take(2)); // Limit to 2 classes
                }
            }

            // Add id if present
            var id = current.GetAttributeValue("id", string.Empty);
            if (!string.IsNullOrWhiteSpace(id))
            {
                part += "#" + id;
            }

            parts.Insert(0, part);
            current = current.ParentNode;
            depth++;
        }

        return string.Join(" > ", parts);
    }

    private string? ResolveUrl(string href, Uri baseUri)
    {
        try
        {
            // First try to parse as an absolute HTTP/HTTPS URL
            if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri) &&
                (absoluteUri.Scheme == "http" || absoluteUri.Scheme == "https"))
            {
                return absoluteUri.ToString();
            }

            // Try to resolve as a relative URL
            if (Uri.TryCreate(baseUri, href, out var resolvedUri) &&
                (resolvedUri.Scheme == "http" || resolvedUri.Scheme == "https"))
            {
                return resolvedUri.ToString();
            }

            _logger.LogWarning("Failed to resolve URL: href='{Href}' with baseUri='{BaseUri}'", href, baseUri);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception resolving URL: href='{Href}' with baseUri='{BaseUri}'", href, baseUri);
            return null;
        }
    }
}
