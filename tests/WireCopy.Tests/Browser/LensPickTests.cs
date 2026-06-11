// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-6yb7.5 — click-to-answer payload mapping: PickScript's JSON →
/// a LinkInfo grounded in the page's extracted links wherever possible.
/// </summary>
[Trait("Category", "Unit")]
public class LensPickTests
{
    [Fact]
    public void Parse_ValidPayload_RoundTrips()
    {
        var pick = LensPick.Parse(
            "{\"href\":\"https://pub.com/story\",\"text\":\"A Headline\",\"parent\":\"div.clus > div.ourh\"}");

        pick.Should().NotBeNull();
        pick!.Href.Should().Be("https://pub.com/story");
        pick.Text.Should().Be("A Headline");
        pick.Parent.Should().Be("div.clus > div.ourh");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"text\":\"no href\"}")]
    [InlineData("{\"href\":\"\"}")]
    public void Parse_EmptyOrGarbled_ReturnsNull(string? json)
    {
        LensPick.Parse(json).Should().BeNull();
    }

    [Fact]
    public void ToLinkInfo_UrlMatch_ReturnsTheRealExtractedLink()
    {
        // The extracted link carries the HtmlAgilityPack-derived ParentSelector —
        // a better durable-identifier source than the click payload's chain.
        var extracted = new LinkInfo
        {
            Url = "https://pub.com/story/",
            DisplayText = "A Headline",
            Type = LinkType.Content,
            ImportanceScore = 85,
            ParentSelector = "main > section.lead > h2",
        };
        var pick = new LensPick("https://PUB.com/story", "A Headline", "div.whatever");

        var link = pick.ToLinkInfo(new[] { extracted });

        link.Should().BeSameAs(extracted, "URL matching is case- and trailing-slash-insensitive");
    }

    [Fact]
    public void ToLinkInfo_NoMatch_ConstructsContentLinkFromPayload()
    {
        var pick = new LensPick("https://pub.com/other", "Some Other Headline", "div.clus > div.ourh");

        var link = pick.ToLinkInfo(Array.Empty<LinkInfo>());

        link.Type.Should().Be(LinkType.Content);
        link.Url.Should().Be("https://pub.com/other");
        link.DisplayText.Should().Be("Some Other Headline");
        link.ParentSelector.Should().Be("div.clus > div.ourh");
    }

    [Fact]
    public void ToLinkInfo_NeverMatchesGroupHeaders()
    {
        var header = LinkInfo.CreateGroupHeader(LinkType.Content); // Url is empty
        var pick = new LensPick("https://pub.com/x", "X", string.Empty);

        var link = pick.ToLinkInfo(new[] { header });

        link.IsGroupHeader.Should().BeFalse();
        link.Url.Should().Be("https://pub.com/x");
    }

    [Fact]
    public void ConstructedPick_FeedsSectionDerivation()
    {
        // End-to-end with the wizard's SectionFromPickedLink: a clicked Techmeme
        // headline (off-site link, LinkExtractor-format parent chain) must yield
        // a durable section.
        var pick = new LensPick(
            "https://publisher.com/big-story",
            "Big Story Headline Long Enough",
            "div#topcol1 > div.clus > div.ourh");

        var link = pick.ToLinkInfo(Array.Empty<LinkInfo>());
        var section = SetupWizard.SectionFromPickedLink(link, "Top Story");

        section.Should().NotBeNull();
        section!.ParentSelectors.Should().NotBeEmpty();
    }
}
