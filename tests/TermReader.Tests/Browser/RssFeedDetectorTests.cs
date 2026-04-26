// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class RssFeedDetectorTests
{
    private readonly RssFeedDetector _detector;

    public RssFeedDetectorTests()
    {
        _detector = new RssFeedDetector(Substitute.For<ILogger<RssFeedDetector>>());
    }

    [Fact]
    public void DetectFeeds_RssLink_ReturnsFeed()
    {
        var html = """
            <html><head>
                <link rel="alternate" type="application/rss+xml" title="RSS Feed" href="/feed.xml">
            </head><body></body></html>
            """;

        var feeds = _detector.DetectFeeds(html, "https://example.com/");

        feeds.Should().HaveCount(1);
        feeds[0].Url.Should().Be("https://example.com/feed.xml");
        feeds[0].Title.Should().Be("RSS Feed");
        feeds[0].Type.Should().Be(FeedType.Rss);
    }

    [Fact]
    public void DetectFeeds_AtomLink_ReturnsFeed()
    {
        var html = """
            <html><head>
                <link rel="alternate" type="application/atom+xml" title="Atom" href="https://example.com/atom.xml">
            </head><body></body></html>
            """;

        var feeds = _detector.DetectFeeds(html, "https://example.com/");

        feeds.Should().HaveCount(1);
        feeds[0].Url.Should().Be("https://example.com/atom.xml");
        feeds[0].Type.Should().Be(FeedType.Atom);
    }

    [Fact]
    public void DetectFeeds_MultipleFeeds_ReturnsAll()
    {
        var html = """
            <html><head>
                <link rel="alternate" type="application/rss+xml" title="Main RSS" href="/rss">
                <link rel="alternate" type="application/atom+xml" title="Atom Feed" href="/atom">
                <link rel="alternate" type="application/rss+xml" title="Comments" href="/comments/feed">
            </head><body></body></html>
            """;

        var feeds = _detector.DetectFeeds(html, "https://example.com/");

        feeds.Should().HaveCount(3);
    }

    [Fact]
    public void DetectFeeds_RelativeUrl_ResolvesCorrectly()
    {
        var html = """
            <html><head>
                <link rel="alternate" type="application/rss+xml" href="../feed">
            </head><body></body></html>
            """;

        var feeds = _detector.DetectFeeds(html, "https://example.com/blog/page");

        feeds.Should().HaveCount(1);
        feeds[0].Url.Should().Be("https://example.com/feed");
    }

    [Fact]
    public void DetectFeeds_NoFeedLinks_ReturnsEmpty()
    {
        var html = """
            <html><head>
                <link rel="stylesheet" href="/style.css">
                <link rel="canonical" href="https://example.com/">
            </head><body></body></html>
            """;

        var feeds = _detector.DetectFeeds(html, "https://example.com/");

        feeds.Should().BeEmpty();
    }

    [Fact]
    public void DetectFeeds_EmptyHtml_ReturnsEmpty()
    {
        _detector.DetectFeeds("", "https://example.com/").Should().BeEmpty();
        _detector.DetectFeeds(null!, "https://example.com/").Should().BeEmpty();
    }

    [Fact]
    public void DetectFeeds_NoHref_SkipsLink()
    {
        var html = """
            <html><head>
                <link rel="alternate" type="application/rss+xml" title="Broken">
            </head><body></body></html>
            """;

        var feeds = _detector.DetectFeeds(html, "https://example.com/");

        feeds.Should().BeEmpty();
    }

    [Fact]
    public void DetectFeeds_RdfXml_DetectedAsRss()
    {
        var html = """
            <html><head>
                <link rel="alternate" type="application/rdf+xml" href="/feed.rdf">
            </head><body></body></html>
            """;

        var feeds = _detector.DetectFeeds(html, "https://example.com/");

        feeds.Should().HaveCount(1);
        feeds[0].Type.Should().Be(FeedType.Rss);
    }

    [Fact]
    public void DetectFeeds_RealWorldNytHtml_DetectsFeeds()
    {
        // Simulates NYT-style feed discovery
        var html = """
            <html><head>
                <link rel="alternate" type="application/rss+xml" title="NYT > Home Page" href="https://rss.nytimes.com/services/xml/rss/nyt/HomePage.xml">
                <link rel="alternate" type="application/rss+xml" title="NYT > World" href="https://rss.nytimes.com/services/xml/rss/nyt/World.xml">
            </head><body><main>Content here</main></body></html>
            """;

        var feeds = _detector.DetectFeeds(html, "https://www.nytimes.com/");

        feeds.Should().HaveCount(2);
        feeds[0].Url.Should().Contain("nytimes.com");
        feeds[0].Title.Should().Contain("NYT");
    }

    [Fact]
    public void DetectFeeds_NoTitleAttribute_TitleIsNull()
    {
        var html = """
            <html><head>
                <link rel="alternate" type="application/rss+xml" href="/feed.xml">
            </head><body></body></html>
            """;

        var feeds = _detector.DetectFeeds(html, "https://example.com/");

        feeds.Should().HaveCount(1);
        feeds[0].Title.Should().BeNull();
    }

    #region ParseFeedAsync

    [Fact]
    public async Task ParseFeedAsync_ValidRss_ReturnsLinkInfoItems()
    {
        var rssXml = "<rss version=\"2.0\"><channel><title>Test Feed</title>" +
            "<item><title>First Article</title><link>https://example.com/article-1</link>" +
            "<pubDate>Mon, 15 Jan 2024 12:00:00 GMT</pubDate><author>test@example.com</author></item>" +
            "<item><title>Second Article</title><link>https://example.com/article-2</link>" +
            "<pubDate>Sun, 14 Jan 2024 12:00:00 GMT</pubDate></item>" +
            "<item><title>Third Article</title><link>https://example.com/article-3</link>" +
            "<pubDate>Sat, 13 Jan 2024 12:00:00 GMT</pubDate></item>" +
            "</channel></rss>";

        var handler = new MockHttpMessageHandler(rssXml, "text/xml");
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        // Test through our detector — manually verify the HTTP mock works
        var logger = Substitute.For<ILogger<RssFeedDetector>>();
        var detector = new RssFeedDetector(logger, httpClient);

        // Call ParseFeedAsync which should fetch, parse, and return items
        var items = await detector.ParseFeedAsync("https://example.com/feed.xml");

        items.Should().HaveCount(3);
        items[0].DisplayText.Should().Be("First Article");
        items[0].Url.Should().Be("https://example.com/article-1");
        items[0].Type.Should().Be(Domain.Enums.Browser.LinkType.Content);
        items[0].PublishedDate.Should().NotBeNull();

        // Verify chronological order (newest first)
        items[0].PublishedDate.Should().BeAfter(items[1].PublishedDate!.Value);
        items[1].PublishedDate.Should().BeAfter(items[2].PublishedDate!.Value);
    }

    [Fact]
    public async Task ParseFeedAsync_AtomFeed_ReturnsLinkInfoItems()
    {
        var atomXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
                <title>Test Atom Feed</title>
                <entry>
                    <title>Atom Entry</title>
                    <link href="https://example.com/atom-1"/>
                    <updated>2026-04-21T12:00:00Z</updated>
                    <author><name>Test Author</name></author>
                </entry>
            </feed>
            """;

        var handler = new MockHttpMessageHandler(atomXml, "application/atom+xml");
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var detector = new RssFeedDetector(
            Substitute.For<ILogger<RssFeedDetector>>(),
            httpClient);

        var items = await detector.ParseFeedAsync("https://example.com/atom.xml");

        items.Should().HaveCount(1);
        items[0].DisplayText.Should().Be("Atom Entry");
        items[0].Url.Should().Be("https://example.com/atom-1");
        items[0].Author.Should().Be("Test Author");
    }

    [Fact]
    public async Task ParseFeedAsync_NetworkError_ReturnsEmpty()
    {
        var handler = new MockHttpMessageHandler(
            System.Net.HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var detector = new RssFeedDetector(
            Substitute.For<ILogger<RssFeedDetector>>(),
            httpClient);

        var items = await detector.ParseFeedAsync("https://example.com/broken-feed");

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseFeedAsync_MalformedXml_ReturnsEmpty()
    {
        var handler = new MockHttpMessageHandler("not xml at all", "text/plain");
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var detector = new RssFeedDetector(
            Substitute.For<ILogger<RssFeedDetector>>(),
            httpClient);

        var items = await detector.ParseFeedAsync("https://example.com/bad-feed");

        items.Should().BeEmpty();
    }

    #endregion

    /// <summary>
    /// Test helper to mock HTTP responses for feed parsing tests.
    /// </summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string? _content;
        private readonly string _contentType;
        private readonly System.Net.HttpStatusCode _statusCode;

        public MockHttpMessageHandler(string content, string contentType)
        {
            _content = content;
            _contentType = contentType;
            _statusCode = System.Net.HttpStatusCode.OK;
        }

        public MockHttpMessageHandler(System.Net.HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
            _contentType = "text/plain";
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode);
            if (_content != null)
            {
                // Use ByteArrayContent to avoid UTF-8 BOM that StringContent adds,
                // which can conflict with XML encoding declarations.
                var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    .GetBytes(_content);
                response.Content = new ByteArrayContent(bytes);
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            }

            return Task.FromResult(response);
        }
    }
}
