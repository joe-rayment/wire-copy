// Educational and personal use only.

using System.Net;
using HtmlAgilityPack;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// Shared HTML metadata extraction utilities used by PageLoader,
/// BackgroundPreloadService, and other HTML processing components.
/// </summary>
internal static class HtmlMetadataExtractor
{
    /// <summary>
    /// Extracts content from a meta tag by name or property attribute.
    /// </summary>
    public static string? ExtractMetaContent(HtmlDocument doc, string name)
    {
        var node = doc.DocumentNode.SelectSingleNode($"//meta[@name='{name}']") ??
                   doc.DocumentNode.SelectSingleNode($"//meta[@property='{name}']");

        var value = node?.GetAttributeValue("content", null!);
        return value != null ? WebUtility.HtmlDecode(value) : null;
    }

    /// <summary>
    /// Extracts published date from common meta tags.
    /// </summary>
    public static DateTime? ExtractPublishedDate(HtmlDocument doc)
    {
        var dateString = ExtractMetaContent(doc, "article:published_time") ??
                        ExtractMetaContent(doc, "datePublished") ??
                        ExtractMetaContent(doc, "date");

        if (DateTime.TryParse(dateString, out var date))
        {
            return date;
        }

        return null;
    }
}
