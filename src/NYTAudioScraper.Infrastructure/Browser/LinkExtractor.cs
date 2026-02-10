// Educational and personal use only.

using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces.Browser;
using NYTAudioScraper.Domain.Enums.Browser;
using NYTAudioScraper.Domain.ValueObjects.Browser;

namespace NYTAudioScraper.Infrastructure.Browser;

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

            // Deduplicate links by URL, keeping the best display text
            // Prefer actual text over image alt text, keep first occurrence for ordering
            var deduplicatedLinks = DeduplicateLinks(links);

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
    /// Deduplicates links by URL, preserving document order but preferring actual text over image alt text.
    /// </summary>
    private static List<LinkInfo> DeduplicateLinks(List<LinkInfo> links)
    {
        var result = new List<LinkInfo>();
        var seenUrls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // URL -> index in result

        foreach (var link in links)
        {
            if (seenUrls.TryGetValue(link.Url, out var existingIndex))
            {
                var existing = result[existingIndex];

                // If existing is from image alt but new one is actual text, replace it
                if (existing.IsFromImageAlt && !link.IsFromImageAlt)
                {
                    result[existingIndex] = link;
                }

                // Otherwise keep the first one (preserves document order)
            }
            else
            {
                // First occurrence of this URL - add it
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
        // First try to get direct text content (not from nested elements like img)
        var text = anchor.InnerText?.Trim() ?? string.Empty;

        // Clean up whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        // If we have actual text content, use it
        if (!string.IsNullOrWhiteSpace(text))
        {
            return (text, false);
        }

        // If no text, try aria-label
        text = anchor.GetAttributeValue("aria-label", string.Empty);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return (text, false);
        }

        // If still no text, try title attribute
        text = anchor.GetAttributeValue("title", string.Empty);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return (text, false);
        }

        // If still no text, try img alt text (mark as from image)
        var img = anchor.SelectSingleNode(".//img[@alt]");
        if (img != null)
        {
            text = img.GetAttributeValue("alt", string.Empty);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return (text, true);
            }
        }

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
