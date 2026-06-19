// <copyright file="ReaderSnapshotHtmlTests.cs" company="Wire Copy">
// Licensed under the MIT License. See LICENSE in the repository root.
// </copyright>

using FluentAssertions;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class ReaderSnapshotHtmlTests
{
    private static Page ArticlePage(string url, string title, params string[] paragraphs)
    {
        var page = Page.Create(url, "<html></html>", new PageMetadata { Title = title });
        page.SetReadableContent(ReadableContent.Create(title, string.Join(" ", paragraphs), paragraphs.ToList()));
        return page;
    }

    [Fact]
    public void Build_IncludesTitleParagraphsAndBaseHref()
    {
        var page = ArticlePage("https://example.com/story", "Hello World", "First para.", "Second para.");

        var html = ReaderSnapshotHtml.Build(page);

        html.Should().Contain("<base href=\"https://example.com/story\">");
        html.Should().Contain("<h1>Hello World</h1>");
        html.Should().Contain("<p>First para.</p>");
        html.Should().Contain("<p>Second para.</p>");
        html.Should().StartWith("<!doctype html>");
    }

    [Fact]
    public void Build_EscapesHtmlInContent()
    {
        var page = ArticlePage("https://example.com", "A <b>bold</b> & risky title", "<script>alert('x')</script>");

        var html = ReaderSnapshotHtml.Build(page);

        html.Should().Contain("A &lt;b&gt;bold&lt;/b&gt; &amp; risky title");
        html.Should().Contain("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;");
        html.Should().NotContain("<script>alert");
    }

    [Fact]
    public void Build_SkipsBlankParagraphs()
    {
        var page = ArticlePage("https://example.com", "T", "real", "   ");

        var html = ReaderSnapshotHtml.Build(page);

        // Two paragraphs in, one blank skipped → exactly one <p>.
        System.Text.RegularExpressions.Regex.Matches(html, "<p>").Count.Should().Be(1);
    }
}
