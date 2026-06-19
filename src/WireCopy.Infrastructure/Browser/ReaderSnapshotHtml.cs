// Licensed under the MIT License. See LICENSE in the repository root.

using System.Net;
using System.Text;
using WireCopy.Domain.Entities.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Builds a self-contained, sanitized HTML document from a page's extracted reader content for the
/// web pane's snapshot mode. Because the markup is entirely our own (escaped title + paragraphs in a
/// minimal dark theme), it renders in a sandboxed <c>&lt;iframe srcdoc&gt;</c> with no
/// <c>X-Frame-Options</c> issue, giving crisp, selectable article text instead of streamed pixels.
/// </summary>
public static class ReaderSnapshotHtml
{
    /// <summary>
    /// Renders <paramref name="page"/>'s reader content as a standalone HTML document. Falls back to a
    /// minimal placeholder when no readable content is present (callers should gate on
    /// <see cref="Page.HasReadableContent"/>, but this stays safe regardless).
    /// </summary>
    public static string Build(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var content = page.ReadableContent;
        var title = content?.Title ?? page.Metadata?.Title ?? page.Url;

        var sb = new StringBuilder(4096);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<base href=\"").Append(Escape(page.Url)).Append("\">");
        sb.Append("<style>");
        sb.Append("html{color-scheme:dark}");
        sb.Append("body{margin:0;background:#15151a;color:#e6e6e6;");
        sb.Append("font-family:Georgia,'Times New Roman',serif;line-height:1.6;");
        sb.Append("font-size:18px;padding:32px 0}");
        sb.Append("main{max-width:46rem;margin:0 auto;padding:0 24px}");
        sb.Append("h1{font-family:ui-sans-serif,system-ui,sans-serif;font-size:1.9rem;line-height:1.2;margin:0 0 .5rem}");
        sb.Append(".meta{font-family:ui-sans-serif,system-ui,sans-serif;font-size:.85rem;color:#9a9aa5;margin:0 0 1.5rem}");
        sb.Append("p{margin:0 0 1.1rem}");
        sb.Append("a{color:#8ab4f8}");
        sb.Append("</style></head><body><main>");

        sb.Append("<h1>").Append(Escape(title)).Append("</h1>");

        if (content is not null)
        {
            var meta = content.GetMetadataString();
            if (!string.IsNullOrWhiteSpace(meta))
            {
                sb.Append("<div class=\"meta\">").Append(Escape(meta)).Append("</div>");
            }

            foreach (var paragraph in content.Paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                sb.Append("<p>").Append(Escape(paragraph)).Append("</p>");
            }
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
