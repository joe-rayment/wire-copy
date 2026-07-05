// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-cn2g.5 — when a site makes it easy (URLs that encode article rank),
/// use that as the default order.
/// </summary>
[Trait("Category", "Unit")]
public class UrlRankOrderingTests
{
    private static LinkInfo Link(string url, string text = "An article headline long enough here") => new()
    {
        Url = url, DisplayText = text, Type = LinkType.Content, ImportanceScore = 70,
    };

    [Fact]
    public void OrdersByNumericPathSegment_OutOfOrderInput()
    {
        var links = new List<LinkInfo>
        {
            Link("https://s.com/news/3", "Third"),
            Link("https://s.com/news/1", "First"),
            Link("https://s.com/news/2", "Second"),
        };

        var ordered = NavigationTreeBuilder.TryOrderByUrlRank(links);

        ordered.Should().NotBeNull();
        ordered!.Select(l => l.DisplayText).Should().Equal("First", "Second", "Third");
    }

    [Fact]
    public void OrdersByRankQueryParam()
    {
        var links = new List<LinkInfo>
        {
            Link("https://s.com/a?rank=2", "B"),
            Link("https://s.com/c?rank=3", "C"),
            Link("https://s.com/d?rank=1", "A"),
        };

        var ordered = NavigationTreeBuilder.TryOrderByUrlRank(links);

        ordered!.Select(l => l.DisplayText).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void ReturnsNull_ForSluggyUrls_WithNoRank()
    {
        var links = new List<LinkInfo>
        {
            Link("https://s.com/story/how-ai-changes-everything"),
            Link("https://s.com/opinion/the-future-of-work"),
            Link("https://s.com/tech/quantum-computing-explained"),
        };

        NavigationTreeBuilder.TryOrderByUrlRank(links).Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_ForScatteredIds_NotRanks()
    {
        // Big scattered numeric ids are article ids, not a 1..N rank.
        var links = new List<LinkInfo>
        {
            Link("https://s.com/article/938211"),
            Link("https://s.com/article/104883"),
            Link("https://s.com/article/771290"),
        };

        NavigationTreeBuilder.TryOrderByUrlRank(links).Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_ForDateSegments_NotRanks()
    {
        // /YYYY/MM/DD -> the day-of-month looks small+distinct but never a 1..N rank.
        var links = new List<LinkInfo>
        {
            Link("https://s.com/2026/06/11/story-one"),
            Link("https://s.com/2026/06/12/story-two"),
            Link("https://s.com/2026/06/15/story-three"),
        };

        NavigationTreeBuilder.TryOrderByUrlRank(links).Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_WhenRanksAreMostlyDuplicated()
    {
        var links = new List<LinkInfo>
        {
            Link("https://s.com/cat/2/a"),
            Link("https://s.com/cat/2/b"),
            Link("https://s.com/cat/2/c"),
        };

        NavigationTreeBuilder.TryOrderByUrlRank(links).Should().BeNull();
    }

    [Theory]
    [InlineData("https://s.com/news/5", 5)]
    [InlineData("https://s.com/a?position=7", 7)]
    [InlineData("https://s.com/story/slug-only", null)]
    [InlineData("https://s.com/article/500000", null)] // out of the 1..999 path band
    public void ExtractUrlRank_Cases(string url, int? expected)
    {
        NavigationTreeBuilder.ExtractUrlRank(url).Should().Be(expected);
    }
}
