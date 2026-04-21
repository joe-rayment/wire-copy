// Educational and personal use only.

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
}
