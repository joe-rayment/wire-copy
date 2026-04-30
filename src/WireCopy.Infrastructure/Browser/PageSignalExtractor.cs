// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using HtmlAgilityPack;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Extracts classification signals from HTML in a single DOM parse.
/// Replaces the multi-pass approach (IsArticlePage + CountArticleContainers)
/// with one structured extraction.
/// </summary>
internal static class PageSignalExtractor
{
    private static readonly HashSet<string> ArticleBodyClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "article-body",
        "article-content",
        "article-body-component",
        "entry-content",
        "post-content",
        "post-full-content",
        "wp-block-post-content",
        "td-post-content",
        "gh-content",
        "story-body",
        "field--name-body",
        "node-content",
        "markup",
    };

    private static readonly HashSet<string> BoilerplateTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nav", "footer", "aside", "header",
    };

    /// <summary>
    /// Extracts all classification-relevant signals from HTML in one parse pass.
    /// </summary>
    public static PageSignals Extract(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return new PageSignals();
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        return new PageSignals
        {
            OgType = ExtractOgType(doc),
            LdJsonType = ExtractLdJsonType(doc),
            ArticleContainerCount = CountElements(doc, "//article"),
            RoleArticleCount = CountElements(doc, "//*[@role='article']"),
            HasArticleBodyClass = HasAnyArticleBodyClass(doc),
            HasH1 = doc.DocumentNode.SelectSingleNode("//h1") != null,
            DeepParagraphCount = CountDeepParagraphs(doc),
            TimeElementCount = CountElements(doc, "//time"),
            HasMainElement = doc.DocumentNode.SelectSingleNode("//main") != null
                || doc.DocumentNode.SelectSingleNode("//*[@role='main']") != null,
        };
    }

    private static string? ExtractOgType(HtmlDocument doc)
    {
        // <meta property="og:type" content="article">
        var node = doc.DocumentNode.SelectSingleNode(
            "//meta[@property='og:type']");
        return node?.GetAttributeValue("content", null!);
    }

    private static string? ExtractLdJsonType(HtmlDocument doc)
    {
        var scripts = doc.DocumentNode.SelectNodes(
            "//script[@type='application/ld+json']");
        if (scripts == null)
        {
            return null;
        }

        foreach (var script in scripts)
        {
            var json = script.InnerText?.Trim();
            if (string.IsNullOrEmpty(json))
            {
                continue;
            }

            try
            {
                using var jsonDoc = JsonDocument.Parse(json);
                var root = jsonDoc.RootElement;

                // Handle both single object and array of objects
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        var type = GetLdJsonTypeFromElement(item);
                        if (type != null)
                        {
                            return type;
                        }
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    var type = GetLdJsonTypeFromElement(root);
                    if (type != null)
                    {
                        return type;
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed ld+json is common; skip
            }
        }

        return null;
    }

    private static string? GetLdJsonTypeFromElement(JsonElement element)
    {
        if (!element.TryGetProperty("@type", out var typeProp))
        {
            return null;
        }

        if (typeProp.ValueKind == JsonValueKind.String)
        {
            var val = typeProp.GetString();
            if (!string.IsNullOrEmpty(val) && !IsBoilerplateLdJsonType(val))
            {
                return val;
            }
        }
        else if (typeProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typeProp.EnumerateArray())
            {
                var val = item.GetString();
                if (!string.IsNullOrEmpty(val) && !IsBoilerplateLdJsonType(val))
                {
                    return val;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// ld+json types that are generic/boilerplate and don't indicate page type.
    /// </summary>
    private static bool IsBoilerplateLdJsonType(string type) =>
        type is "BreadcrumbList" or "Organization" or "NewsMediaOrganization"
            or "ImageObject" or "Person" or "SpeakableSpecification" or "Thing";

    private static bool HasAnyArticleBodyClass(HtmlDocument doc)
    {
        // Check all elements for any article-body class
        var allElements = doc.DocumentNode.SelectNodes("//*[@class]");
        if (allElements == null)
        {
            return false;
        }

        // Check each class against known article-body patterns
        // Split by space and check each class token, also check
        // if any class segment (split by -) contains the patterns
        return allElements
            .Where(e => !string.IsNullOrEmpty(e.GetAttributeValue("class", string.Empty)))
            .Any(e => ArticleBodyClasses.Any(known =>
                e.GetAttributeValue("class", string.Empty).Contains(known, StringComparison.OrdinalIgnoreCase)));
    }

    private static int CountDeepParagraphs(HtmlDocument doc)
    {
        // Prefer paragraphs inside <main> or <article> if present
        var scopeNode = doc.DocumentNode.SelectSingleNode("//main")
            ?? doc.DocumentNode.SelectSingleNode("//article")
            ?? doc.DocumentNode;

        var paragraphs = scopeNode.SelectNodes(".//p");
        if (paragraphs == null)
        {
            return 0;
        }

        var count = 0;
        foreach (var p in paragraphs)
        {
            // Skip paragraphs inside boilerplate regions
            if (IsInsideBoilerplate(p))
            {
                continue;
            }

            var text = p.InnerText?.Trim();
            if (text != null && text.Length > 200)
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsInsideBoilerplate(HtmlNode node)
    {
        var current = node.ParentNode;
        while (current != null)
        {
            if (BoilerplateTags.Contains(current.Name))
            {
                return true;
            }

            current = current.ParentNode;
        }

        return false;
    }

    private static int CountElements(HtmlDocument doc, string xpath)
    {
        return doc.DocumentNode.SelectNodes(xpath)?.Count ?? 0;
    }
}
