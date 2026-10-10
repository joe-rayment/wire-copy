// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-vk4nf — the ad/sponsor and non-article-URL rules matched bare
/// substrings, so real headlines ("Week ahead: …", "State-sponsored hackers …")
/// and real article URLs (".../login-with-passkeys-explained") were demoted or
/// dropped as chrome.
/// </summary>
[Trait("Category", "Unit")]
public class LinkExtractorFalsePositiveTests
{
    private readonly LinkExtractor _sut = new(Substitute.For<ILogger<LinkExtractor>>());

    // Guards:   real headlines that merely CONTAIN "ad:", "sponsored" or "promoted" are not treated as ads
    // Fails if: the ad/sponsor text rule goes back to a plain substring test
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~IsAdOrSponsoredLink_RealHeadline"
    [Theory]
    [InlineData("Week ahead: Fed decision")]
    [InlineData("Looking ahead: markets and earnings")]
    [InlineData("Full steam ahead: the rail plan returns")]
    [InlineData("State-sponsored hackers breach utility")]
    [InlineData("Senator promoted to committee chair")]
    [InlineData("Why Dove's advertisement was banned in the UK")]
    public void IsAdOrSponsoredLink_RealHeadline_IsNotAnAd(string headline)
    {
        LinkExtractor.IsAdOrSponsoredLink(headline, parentSelector: null)
            .Should().BeFalse($"\"{headline}\" is a story, not an ad label");
    }

    // Guards:   genuine ad/sponsor labels (leading label or promo phrase) are still caught
    // Fails if: the label rule is tightened so far that "Ad: …", "Sponsored by …" or "Paid content …" slip through
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~IsAdOrSponsoredLink_AdLabel"
    [Theory]
    [InlineData("Ad: Try our app free")]
    [InlineData("Sponsored: Best VPN deals")]
    [InlineData("Sponsored by Acme")]
    [InlineData("Advertisement")]
    [InlineData("Promoted: The best laptops")]
    [InlineData("Promoted · The best laptops")]
    [InlineData("[Ad: Try our app free]")]
    [InlineData("Paid content from Acme")]
    [InlineData("Sign up for our morning newsletter")]
    public void IsAdOrSponsoredLink_AdLabel_IsAnAd(string text)
    {
        LinkExtractor.IsAdOrSponsoredLink(text, parentSelector: null)
            .Should().BeTrue($"\"{text}\" is an ad / promo label");
    }

    // Guards:   article URLs whose slug merely contains "login" / "subscription" are not classed as navigation
    // Fails if: the non-article URL rule goes back to substring matching, or a real utility path (/login, /account/…, /newsletters/) stops matching
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~IsNonArticleUrl_"
    [Theory]
    [InlineData("https://example.com/tech/2026/10/login-with-passkeys-explained", false)]
    [InlineData("https://example.com/business/subscription-fatigue-hits-streaming", false)]
    [InlineData("https://example.com/world/2026/10/newsletter-boom-reshapes-media", false)]
    [InlineData("https://example.com/login", true)]
    [InlineData("https://example.com/login?next=%2Ffoo", true)]
    [InlineData("https://example.com/signup", true)]
    [InlineData("https://example.com/account/settings", true)]
    [InlineData("https://example.com/newsletters/", true)]
    [InlineData("https://example.com/newsletter/morning-briefing", true)]
    [InlineData("https://example.com/subscriptions", true)]
    [InlineData("https://example.com/auth/callback", true)]
    [InlineData("https://example.com/spotlight/podcasts/episode-4", true)]
    public void IsNonArticleUrl_MatchesWholeSegmentsOnly(string url, bool expected)
    {
        LinkExtractor.IsNonArticleUrl(url).Should().Be(expected);
    }

    // Guards:   through the real extractor, a "Week ahead: …" style headline comes out as an ordinary un-flagged story
    // Fails if: ExtractLinksAsync drops the headline or flags it IsSponsored because of a substring match
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ExtractLinksAsync_RealHeadlineContainingAdLabelWord"
    [Theory]
    [InlineData("Week ahead: Fed decision")]
    [InlineData("Looking ahead: markets and earnings")]
    [InlineData("State-sponsored hackers breach utility")]
    [InlineData("Senator promoted to committee chair")]
    public async Task ExtractLinksAsync_RealHeadlineContainingAdLabelWord_IsKeptAndNotSponsored(string headline)
    {
        var html = $"<html><body><article><a href=\"/news/2026/10/12/story\">{headline}</a></article></body></html>";

        var links = await _sut.ExtractLinksAsync(html, "https://example.com/");

        var link = links.Should().ContainSingle(l => l.DisplayText == headline,
            "the headline must survive extraction").Subject;
        link.IsSponsored.Should().BeFalse();
    }

    // Guards:   through the real extractor, a headline link to a "…/login-…" slug stays an article, while /login stays navigation
    // Fails if: the URL rule flags the dated article as Navigation, or stops flagging /login
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~ExtractLinksAsync_ArticleSlugContainingLogin"
    [Fact]
    public async Task ExtractLinksAsync_ArticleSlugContainingLogin_IsNotNavigation()
    {
        const string slugHeadline = "Login with passkeys explained for everyone";
        const string loginHeadline = "Log in to read your saved stories here";
        var html = "<html><body><article>" +
                   $"<a href=\"/tech/2026/10/login-with-passkeys-explained\">{slugHeadline}</a>" +
                   $"<a href=\"/login\">{loginHeadline}</a>" +
                   "</article></body></html>";

        var links = await _sut.ExtractLinksAsync(html, "https://example.com/");

        links.Single(l => l.DisplayText == slugHeadline).Type.Should().NotBe(LinkType.Navigation);
        links.Single(l => l.DisplayText == loginHeadline).Type.Should().Be(LinkType.Navigation);
    }
}
