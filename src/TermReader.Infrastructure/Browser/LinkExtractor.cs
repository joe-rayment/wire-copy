// Educational and personal use only.

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
    private readonly ILogger<LinkExtractor> _logger;

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
        "nav", "navigation", "menu", "sidebar", "header", "toolbar"
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

    public LinkExtractor(ILogger<LinkExtractor> logger)
    {
        _logger = logger;
    }

    public Task<List<LinkInfo>> ExtractLinksAsync(string html, string baseUrl, CancellationToken cancellationToken = default)
    {
        var links = new List<LinkInfo>();

        _logger.LogDebug("ExtractLinksAsync: html length = {Length}, baseUrl = {BaseUrl}", html?.Length ?? 0, baseUrl);

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
                    var ariaLabel = anchor.GetAttributeValue("aria-label", null);
                    if (!string.IsNullOrWhiteSpace(ariaLabel))
                    {
                        linkInfo = linkInfo with { AriaLabel = ariaLabel };
                    }

                    // Track whether this came from image alt text
                    linkInfo = linkInfo with { IsFromImageAlt = isFromImage };

                    links.Add(linkInfo);
                }
                catch (UriFormatException ex)
                {
                    _logger.LogDebug(ex, "Invalid URL: {Href}", href);
                }
            }

            // Group same-URL links within the same parent, then deduplicate across parents
            var deduplicatedLinks = GroupLinksByUrl(links);

            _logger.LogInformation("Extracted {Count} links ({Deduped} unique)",
                links.Count, deduplicatedLinks.Count);

            links = deduplicatedLinks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting links from HTML");
        }

        return Task.FromResult(links);
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
                    continue;

                if (link.IsFromImageAlt)
                    imageTexts.Add(text);
                else
                    realTexts.Add(text);
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
                    if (i == j) continue;
                    if (allTexts[j].Contains(allTexts[i], StringComparison.OrdinalIgnoreCase) &&
                        !allTexts[i].Equals(allTexts[j], StringComparison.OrdinalIgnoreCase))
                    {
                        isSubstring = true;
                        break;
                    }
                }
                if (!isSubstring)
                    filtered.Add(allTexts[i]);
            }

            // Deduplicate identical texts (case-insensitive)
            var distinct = new List<string>();
            foreach (var text in filtered)
            {
                if (!distinct.Any(d => d.Equals(text, StringComparison.OrdinalIgnoreCase)))
                    distinct.Add(text);
            }

            var mergedText = string.Join(": ", distinct);
            if (string.IsNullOrWhiteSpace(mergedText))
                mergedText = group[0].Link.DisplayText;

            var maxImportance = group.Max(g => g.Link.ImportanceScore);
            var firstLink = group[0].Link;

            // Use non-image-alt status if any real text was found
            var isFromImage = realTexts.Count == 0 && imageTexts.Count > 0;

            var mergedLink = firstLink with
            {
                DisplayText = mergedText,
                ImportanceScore = maxImportance,
                IsFromImageAlt = isFromImage
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

                // Replace if new entry has higher importance, or same importance but longer text
                if (link.ImportanceScore > existing.ImportanceScore ||
                    (link.ImportanceScore == existing.ImportanceScore &&
                     link.DisplayText.Length > existing.DisplayText.Length))
                {
                    result[existingIndex] = link;
                }
                // Also replace if existing is image alt but new one is real text
                else if (existing.IsFromImageAlt && !link.IsFromImageAlt)
                {
                    result[existingIndex] = link;
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
        if (displayText.Length < 30)
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

    private string? ResolveUrl(string href, Uri baseUri)
    {
        try
        {
            // First try to parse as an absolute HTTP/HTTPS URL
            if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
            {
                // Only return if it's actually an http/https URL
                // Otherwise fall through to try resolving as relative
                if (absoluteUri.Scheme == "http" || absoluteUri.Scheme == "https")
                {
                    return absoluteUri.ToString();
                }

                // If it's a file:// or other scheme, it was probably a relative path
                // that got misinterpreted as absolute (e.g., "/path" -> "file:///path")
                // Fall through to try relative resolution
            }

            // Try to resolve as a relative URL
            if (Uri.TryCreate(baseUri, href, out var resolvedUri))
            {
                // Only accept http/https results
                if (resolvedUri.Scheme == "http" || resolvedUri.Scheme == "https")
                {
                    return resolvedUri.ToString();
                }
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

    private static (string text, bool isFromImage) GetDisplayTextWithSource(HtmlNode anchor)
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

    private static string GetDisplayText(HtmlNode anchor)
    {
        return GetDisplayTextWithSource(anchor).text;
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
}
