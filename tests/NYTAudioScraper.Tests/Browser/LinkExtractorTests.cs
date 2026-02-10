// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NYTAudioScraper.Infrastructure.Browser;
using Xunit;

namespace NYTAudioScraper.Tests.Browser;

public class LinkExtractorTests
{
    private readonly LinkExtractor _sut;
    private readonly ILogger<LinkExtractor> _logger;

    public LinkExtractorTests()
    {
        _logger = Substitute.For<ILogger<LinkExtractor>>();
        _sut = new LinkExtractor(_logger);
    }

    [Fact]
    public async Task ExtractLinksAsync_WithMinifiedHtml_ShouldExtractLinks()
    {
        // Arrange - This is the exact HTML from example.com (minified, unclosed <p> tags)
        // Note: The issue is that unclosed <p> tags can confuse some parsers
        var html = @"<!doctype html><html lang=""en""><head><title>Example Domain</title><meta name=""viewport"" content=""width=device-width, initial-scale=1""><style>body{background:#eee}</style></head><body><div><h1>Example Domain</h1><p>This domain is for use in documentation examples without needing permission.</p><p><a href=""https://iana.org/domains/example"">Learn more</a></p></div></body></html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().NotBeEmpty("example.com HTML contains a link to iana.org");
        links.Should().HaveCount(1);
        links[0].DisplayText.Should().Be("Learn more");
        links[0].Url.Should().Contain("iana.org");
    }

    [Fact]
    public async Task ExtractLinksAsync_WithUnclosedTags_ShouldStillExtractLinks()
    {
        // Arrange - HTML with unclosed <p> tags (like real example.com)
        var html = @"<!doctype html><html><body><p>Text<p><a href=""https://iana.org/domains/example"">Learn more</a></body></html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert - HtmlAgilityPack should still find the link
        links.Should().NotBeEmpty("HtmlAgilityPack should handle unclosed tags");
    }

    [Fact]
    public async Task ExtractLinksAsync_WithRealExampleComHtml_ShouldExtractLinks()
    {
        // Arrange - This is the EXACT HTML from example.com (with missing </head>)
        var html = @"<!doctype html><html lang=""en""><head><title>Example Domain</title><meta name=""viewport"" content=""width=device-width, initial-scale=1""><style>body{background:#eee;width:60vw;margin:15vh auto;font-family:system-ui,sans-serif}h1{font-size:1.5em}div{opacity:0.8}a:link,a:visited{color:#348}</style><body><div><h1>Example Domain</h1><p>This domain is for use in documentation examples without needing permission. Avoid use in operations.<p><a href=""https://iana.org/domains/example"">Learn more</a></div></body></html>";
        var baseUrl = "https://example.com";

        // Debug - verify HtmlAgilityPack finds the link
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.OptionFixNestedTags = true;
        doc.OptionAutoCloseOnEnd = true;
        doc.LoadHtml(html);
        var allAnchors = doc.DocumentNode.SelectNodes("//a[@href]");
        var anchorCount = allAnchors?.Count ?? 0;

        // Debug more - check what's in the anchor and its parent path
        string? anchorText = null;
        string? anchorHref = null;
        string? parentPath = null;
        string? resolvedUrl = null;
        string? displayText = null;
        bool? isAdLink = null;

        if (allAnchors != null && allAnchors.Count > 0)
        {
            var anchor = allAnchors[0];
            anchorText = anchor.InnerText;
            anchorHref = anchor.GetAttributeValue("href", "");

            // Test ResolveUrl manually
            var baseUri = new Uri(baseUrl);
            if (Uri.TryCreate(anchorHref, UriKind.Absolute, out var absoluteUri))
            {
                resolvedUrl = absoluteUri.ToString();
            }
            else if (Uri.TryCreate(baseUri, anchorHref, out var resolved))
            {
                resolvedUrl = resolved.ToString();
            }

            // Test GetDisplayText manually (simplified)
            displayText = anchor.InnerText?.Trim() ?? string.Empty;
            displayText = System.Text.RegularExpressions.Regex.Replace(displayText, @"\s+", " ").Trim();

            // Build parent path like LinkExtractor does
            var parts = new System.Collections.Generic.List<string>();
            var current = anchor.ParentNode;
            int depth = 0;
            while (current != null && depth < 5 && current.Name != "#document")
            {
                var part = current.Name;
                var classAttr = current.GetAttributeValue("class", "");
                if (!string.IsNullOrEmpty(classAttr))
                {
                    part += "." + classAttr;
                }
                parts.Insert(0, part);
                current = current.ParentNode;
                depth++;
            }
            parentPath = string.Join(" > ", parts);

            // Test IsAdOrSponsoredLink manually
            var textLower = displayText.ToLowerInvariant();
            var adPatterns = new[] { "sponsored", "advertisement", "ad:", "created for", "created by", "promoted", "partner content", "paid content", "special advertising" };
            isAdLink = adPatterns.Any(p => textLower.Contains(p.ToLowerInvariant()));
        }

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert - This HTML has missing </head>, unclosed <p> tags
        // HtmlAgilityPack should still parse it correctly
        anchorCount.Should().Be(1, $"HtmlAgilityPack should find 1 anchor in the HTML. Found: {anchorCount}");
        anchorText.Should().Be("Learn more", $"Anchor text should be 'Learn more', was: '{anchorText}'");
        anchorHref.Should().Contain("iana.org", $"Anchor href should contain iana.org, was: '{anchorHref}'");
        resolvedUrl.Should().NotBeNullOrEmpty($"ResolveUrl failed for href='{anchorHref}'");
        displayText.Should().NotBeNullOrEmpty($"GetDisplayText returned empty for anchor with text='{anchorText}'");
        isAdLink.Should().BeFalse($"'{displayText}' should not be detected as ad/sponsor link");
        links.Should().NotBeEmpty($"Links should not be empty. resolvedUrl='{resolvedUrl}', displayText='{displayText}', parentPath='{parentPath}', isAdLink={isAdLink}");
        links.Should().ContainSingle(l => l.DisplayText == "Learn more");
    }

    [Fact]
    public async Task ExtractLinksAsync_WithNormalHtml_ShouldExtractLinks()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <a href=""https://example.com/page1"">Page One</a>
                <a href=""https://example.com/page2"">Page Two</a>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(2);
    }

    [Fact]
    public async Task ExtractLinksAsync_WithExternalLink_ShouldReturnIt()
    {
        // Arrange - simplest possible case with external link
        var html = @"<html><body><a href=""https://iana.org/domains/example"">Learn more</a></body></html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert - external links should be returned (just classified as External)
        links.Should().HaveCount(1, "External links should be extracted, just classified as LinkType.External");
        links[0].DisplayText.Should().Be("Learn more");
        links[0].Type.Should().Be(NYTAudioScraper.Domain.Enums.Browser.LinkType.External);
    }

    [Fact]
    public async Task ExtractLinksAsync_WithMalformedHtmlMissingHeadClose_ShouldExtractLinks()
    {
        // Arrange - HTML where </head> is missing, causing body to be nested inside head
        // This is exactly like example.com's actual HTML
        var html = @"<!doctype html><html><head><title>Test</title><style>body{}</style><body><div><p><a href=""https://iana.org/domains/example"">Learn more</a></div></body></html>";
        var baseUrl = "https://example.com";

        // Debug - print the DOM structure
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.OptionFixNestedTags = true;
        doc.OptionAutoCloseOnEnd = true;
        doc.LoadHtml(html);

        // Find anchor and trace its actual path
        var anchor = doc.DocumentNode.SelectSingleNode("//a[@href]");
        var pathParts = new System.Collections.Generic.List<string>();
        var node = anchor;
        while (node != null && node.Name != "#document")
        {
            pathParts.Insert(0, node.Name);
            node = node.ParentNode;
        }
        var actualPath = string.Join(" > ", pathParts);

        // Debug: Manually trace through what LinkExtractor does
        var href = anchor?.GetAttributeValue("href", "");
        var text = anchor?.InnerText?.Trim() ?? "";
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        // Check each condition that could filter out the link
        var hrefEmpty = string.IsNullOrWhiteSpace(href);
        var startsWithHash = href?.StartsWith('#') ?? false;
        var startsWithJs = href?.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ?? false;

        string? resolvedUrl = null;
        if (!string.IsNullOrWhiteSpace(href))
        {
            var baseUri = new Uri(baseUrl);
            if (Uri.TryCreate(href, UriKind.Absolute, out var absUri))
            {
                if (absUri.Scheme == "http" || absUri.Scheme == "https")
                    resolvedUrl = absUri.ToString();
            }
            else if (Uri.TryCreate(baseUri, href, out var relUri))
            {
                resolvedUrl = relUri.ToString();
            }
        }

        var textEmpty = string.IsNullOrWhiteSpace(text);

        // Build parent selector like LinkExtractor
        var parentParts = new System.Collections.Generic.List<string>();
        var current = anchor?.ParentNode;
        int depth = 0;
        while (current != null && depth < 5 && current.Name != "#document")
        {
            var part = current.Name;
            var classAttr = current.GetAttributeValue("class", "");
            if (!string.IsNullOrWhiteSpace(classAttr))
            {
                var classes = classAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (classes.Length > 0)
                    part += "." + string.Join(".", classes.Take(2));
            }
            var id = current.GetAttributeValue("id", "");
            if (!string.IsNullOrWhiteSpace(id))
                part += "#" + id;
            parentParts.Insert(0, part);
            current = current.ParentNode;
            depth++;
        }
        var parentSelector = string.Join(" > ", parentParts);

        // Check ad filtering (using word boundary matching like the real code)
        var adPatterns = new[] { "sponsored", "advertisement", "ad:", "created for", "created by", "promoted", "partner content", "paid content", "special advertising" };
        var adClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ad", "ads", "advert", "advertisement", "sponsor", "sponsored", "promo", "promotion", "partner", "paid" };
        var textLower = text.ToLowerInvariant();
        var selectorLower = parentSelector.ToLowerInvariant();
        // Extract actual class names from selector (classes are prefixed with .)
        var classMatches = System.Text.RegularExpressions.Regex.Matches(selectorLower, @"\.([a-z0-9_-]+)");
        var classNames = classMatches.Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isAd = adPatterns.Any(p => textLower.Contains(p.ToLowerInvariant())) ||
                   classNames.Any(c => adClasses.Contains(c));

        var debugInfo = $"href={href}, hrefEmpty={hrefEmpty}, startsWithHash={startsWithHash}, startsWithJs={startsWithJs}, " +
                        $"resolvedUrl={resolvedUrl}, text='{text}', textEmpty={textEmpty}, parentSelector={parentSelector}, isAd={isAd}";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        anchor.Should().NotBeNull("Anchor should be found by HtmlAgilityPack");
        hrefEmpty.Should().BeFalse($"href should not be empty. Debug: {debugInfo}");
        startsWithHash.Should().BeFalse($"href should not start with #. Debug: {debugInfo}");
        startsWithJs.Should().BeFalse($"href should not start with javascript:. Debug: {debugInfo}");
        resolvedUrl.Should().NotBeNullOrEmpty($"URL should resolve. Debug: {debugInfo}");
        textEmpty.Should().BeFalse($"Display text should not be empty. Debug: {debugInfo}");
        isAd.Should().BeFalse($"Should not be detected as ad. Debug: {debugInfo}");
        links.Should().NotBeEmpty($"Links should be extracted. Debug: {debugInfo}. Anchor actual DOM path: {actualPath}");
    }

    [Fact]
    public async Task ExtractLinksAsync_ShouldFilterAdLinks()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <a href=""https://example.com/article"">Real Article Title</a>
                <a href=""https://sponsor.com/promo"">Created for Some Company</a>
                <a href=""https://sponsor.com/ad"">Sponsored Content</a>
                <div class=""advertisement"">
                    <a href=""https://ad.com/link"">Click Here</a>
                </div>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(1, "only the real article should remain after filtering ads");
        links[0].DisplayText.Should().Be("Real Article Title");
    }

    [Fact]
    public async Task ExtractLinksAsync_WithHackerNewsHtml_ShouldExtractManyLinks()
    {
        // Arrange - Simplified HN-style HTML
        var html = @"
            <html>
            <body>
                <table>
                    <tr class=""athing""><td class=""title""><a href=""https://example.com/story1"">First Story About Technology and Innovation in Modern Computing</a></td></tr>
                    <tr class=""athing""><td class=""title""><a href=""https://example.com/story2"">Second Story About Science and Discovery</a></td></tr>
                    <tr><td><a href=""https://news.ycombinator.com"">Hacker News</a></td></tr>
                </table>
            </body>
            </html>";
        var baseUrl = "https://news.ycombinator.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCountGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task ExtractLinksAsync_ShouldHandleSubdomainsCorrectly()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <a href=""https://www.example.com/page"">Same Domain WWW</a>
                <a href=""https://blog.example.com/post"">Same Domain Subdomain</a>
                <a href=""https://other.com/page"">Different Domain</a>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(3);

        // www.example.com and blog.example.com should NOT be marked as external
        var wwwLink = links.First(l => l.Url.Contains("www.example.com"));
        var blogLink = links.First(l => l.Url.Contains("blog.example.com"));
        var otherLink = links.First(l => l.Url.Contains("other.com"));

        wwwLink.Type.Should().NotBe(NYTAudioScraper.Domain.Enums.Browser.LinkType.External);
        blogLink.Type.Should().NotBe(NYTAudioScraper.Domain.Enums.Browser.LinkType.External);
        otherLink.Type.Should().Be(NYTAudioScraper.Domain.Enums.Browser.LinkType.External);
    }

    [Fact]
    public async Task ExtractLinksAsync_WithEmptyHtml_ShouldReturnEmptyList()
    {
        // Arrange
        var html = "<html><body></body></html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractLinksAsync_ShouldSkipJavascriptLinks()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <a href=""javascript:void(0)"">JS Link</a>
                <a href=""#section"">Anchor Link</a>
                <a href=""https://example.com/real"">Real Link</a>
            </body>
            </html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().HaveCount(1);
        links[0].DisplayText.Should().Be("Real Link");
    }

    [Fact]
    public async Task ExtractLinksAsync_WithNoAnchors_ShouldReturnEmptyList()
    {
        // Arrange - HTML with content but zero <a> tags
        var html = @"<html><body><h1>Hello World</h1><p>Some paragraph text.</p></body></html>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractLinksAsync_WithSevereMalformedHtml_ShouldNotThrow()
    {
        // Arrange - severely malformed HTML with unclosed tags and nested issues
        var html = @"<html><body><div><p>Text<a href=""https://example.com/page1"">Link One<div><span>Nested</a></p></div></body>";
        var baseUrl = "https://example.com";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert - should not throw, may or may not find links depending on parser behavior
        links.Should().NotBeNull();
    }

    [Fact]
    public async Task ExtractLinksAsync_WithRelativeUrls_ShouldResolveCorrectly()
    {
        // Arrange
        var html = @"
            <html>
            <body>
                <a href=""/about"">About Us</a>
                <a href=""articles/latest"">Latest Articles From Our Publication</a>
                <a href=""../other"">Other Section With Long Description Text</a>
            </body>
            </html>";
        var baseUrl = "https://example.com/section/page";

        // Act
        var links = await _sut.ExtractLinksAsync(html, baseUrl);

        // Assert
        links.Should().NotBeEmpty();
        links.Should().Contain(l => l.Url == "https://example.com/about");
        links.Should().Contain(l => l.Url == "https://example.com/section/articles/latest");
        links.Should().Contain(l => l.Url == "https://example.com/other");
    }
}
