// Licensed under the MIT License. See LICENSE in the repository root.

using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Regression tests for workspace-jc2v: the generator must emit a UTF-8 XML declaration.
/// Apple Podcasts and most RSS parsers reject feeds that lie about their encoding.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastFeedGeneratorEncodingTests
{
    private readonly PodcastFeedGenerator _generator = new(NullLogger<PodcastFeedGenerator>.Instance);

    private static PodcastMetadata SamplePodcast() => new()
    {
        Title = "Encoding Test Podcast",
        Description = "A feed that must declare UTF-8 honestly",
        Author = "Test Author",
        Language = "en-us",
        ImageUrl = "https://example.com/cover.jpg",
        Category = "Technology",
        Explicit = false,
        FeedUrl = "https://example.com/feed.xml",
    };

    private static EpisodeMetadata SampleEpisode() => new()
    {
        Id = "ep1",
        Title = "Episode 1",
        Description = "First episode",
        PublishedAtUtc = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc),
        AudioUrl = "https://example.com/ep1.m4a",
        AudioSizeBytes = 1024000,
        Duration = TimeSpan.FromMinutes(15),
        AudioMimeType = "audio/x-m4a",
    };

    [Fact]
    public async Task FeedDeclaresUtf8WithEpisodes()
    {
        var xml = await _generator.GenerateFeedXmlAsync(SamplePodcast(), [SampleEpisode()]);

        var prologue = xml.TrimStart();
        prologue.Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"", because:
            "Apple Podcasts rejects feeds whose declared encoding does not match the bytes on the wire");
    }

    [Fact]
    public async Task FeedDeclaresUtf8WithNoEpisodes()
    {
        var xml = await _generator.GenerateFeedXmlAsync(SamplePodcast(), Array.Empty<EpisodeMetadata>());

        var prologue = xml.TrimStart();
        prologue.Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"", because:
            "Bootstrapped (empty) feeds go through the same generator and must also be UTF-8");
    }

    [Fact]
    public async Task FeedDoesNotMentionUtf16()
    {
        var xml = await _generator.GenerateFeedXmlAsync(SamplePodcast(), [SampleEpisode()]);

        xml.Should().NotContain("utf-16", because: "the document must never advertise UTF-16, even partially");
        xml.Should().NotContain("UTF-16", because: "the document must never advertise UTF-16 in any casing");
    }

    [Fact]
    public async Task FeedParsesAsXml()
    {
        var xml = await _generator.GenerateFeedXmlAsync(SamplePodcast(), [SampleEpisode()]);

        Action parse = () => XDocument.Parse(xml);
        parse.Should().NotThrow("a well-formed feed must round-trip through XDocument.Parse");
    }
}
