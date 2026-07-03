// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class SelectorDerivationTests
{
    private static LinkInfo Link(string url, string? parentSelector) => new()
    {
        Url = url,
        DisplayText = "Link",
        Type = LinkType.Content,
        ImportanceScore = 60,
        ParentSelector = parentSelector,
    };

    [Fact]
    public void DeriveParentSelectors_SharedClassFragment_ReturnsDiscriminatingFragmentNotBareTag()
    {
        var links = new[]
        {
            Link("https://x.com/a1", "main section.lead > h1 > a"),
            Link("https://x.com/a2", "main section.lead > h2 > a"),
        };

        var result = SelectorDerivation.DeriveParentSelectors(links);

        result.Should().Contain("section.lead");
        result.Should().NotContain("a");
        result.Should().NotContain("main"); // bare tag, non-discriminating
    }

    [Fact]
    public void DeriveParentSelectors_OnlyGenericTags_ReturnsEmpty()
    {
        var links = new[]
        {
            Link("https://x.com/a1", "article > a"),
            Link("https://x.com/a2", "article > a"),
        };

        SelectorDerivation.DeriveParentSelectors(links).Should().BeEmpty();
    }

    [Fact]
    public void DeriveUrlPatterns_SharedPathSegment_ReturnsSlashWrappedSegment()
    {
        var links = new[]
        {
            Link("https://x.com/opinion/2026/05/30/a1", null),
            Link("https://x.com/opinion/2026/05/31/a2", null),
        };

        var result = SelectorDerivation.DeriveUrlPatterns(links);

        result.Should().Contain("/opinion/");
        // The shared numeric date segments must be discarded.
        result.Should().NotContain("/2026/");
    }

    [Theory]
    [InlineData("/us-releases-powerful-anthropic-model-mythos-to-some-us-companies/")] // headline slug
    [InlineData("/this-is-a-very-long-single-story-path-segment/")] // long single segment
    [InlineData("/a-b-c-d/")] // 3+ hyphens
    [InlineData("")] // blank
    public void IsVolatileUrlPattern_SingleArticleSlugs_ReturnTrue(string pattern)
    {
        SelectorDerivation.IsVolatileUrlPattern(pattern).Should().BeTrue();
    }

    [Theory]
    [InlineData("/opinion/")]
    [InlineData("/tech/")]
    [InlineData("/personal-finance/")] // multi-word hub, 1 hyphen, short
    [InlineData("/article/")]
    public void IsVolatileUrlPattern_ShortHubSegments_ReturnFalse(string pattern)
    {
        SelectorDerivation.IsVolatileUrlPattern(pattern).Should().BeFalse();
    }

    [Fact]
    public void StripVolatileUrlPatterns_KeepsHubsDropsSlugs()
    {
        var input = new[]
        {
            "/opinion/",
            "/us-releases-powerful-anthropic-model-mythos-to-some-us-companies/",
            "/tech/",
        };

        SelectorDerivation.StripVolatileUrlPatterns(input)
            .Should().BeEquivalentTo(new[] { "/opinion/", "/tech/" });
    }

    [Fact]
    public void DeriveUrlPatterns_NoSharedSegment_ReturnsEmpty()
    {
        var links = new[]
        {
            Link("https://x.com/opinion/a1", null),
            Link("https://x.com/sports/b2", null),
        };

        SelectorDerivation.DeriveUrlPatterns(links).Should().BeEmpty();
    }

    [Fact]
    public void DeriveParentSelectors_NoCommonToken_ReturnsEmpty()
    {
        var links = new[]
        {
            Link("https://x.com/a1", "section.lead a"),
            Link("https://x.com/a2", "section.feed a"),
        };

        SelectorDerivation.DeriveParentSelectors(links).Should().BeEmpty();
    }

    [Fact]
    public async Task DerivedSelectors_FedBackIntoConfig_ReproduceTheSamePartition()
    {
        // Self-consistency: derive identifiers from two buckets, build a config
        // from them, and confirm BuildWithHierarchyConfig re-partitions the same
        // links into the same buckets.
        var opinion = new[]
        {
            Link("https://x.com/opinion/a1", "main section.opinion > h3 > a"),
            Link("https://x.com/opinion/a2", "main section.opinion > h3 > a"),
        };
        var news = new[]
        {
            Link("https://x.com/news/b1", "main section.news > h3 > a"),
            Link("https://x.com/news/b2", "main section.news > h3 > a"),
        };

        var opinionSel = SelectorDerivation.DeriveParentSelectors(opinion);
        var opinionUrl = SelectorDerivation.DeriveUrlPatterns(opinion);
        var newsSel = SelectorDerivation.DeriveParentSelectors(news);
        var newsUrl = SelectorDerivation.DeriveUrlPatterns(news);

        opinionSel.Should().Contain("section.opinion");
        newsSel.Should().Contain("section.news");

        var config = new SiteHierarchyConfig
        {
            Domain = "x.com",
            UrlPattern = "^https?://x\\.com/?$",
            Sections = new List<HierarchySection>
            {
                new() { Name = "Opinion", SortOrder = 0, ParentSelectors = opinionSel, UrlPatterns = opinionUrl },
                new() { Name = "News", SortOrder = 1, ParentSelectors = newsSel, UrlPatterns = newsUrl },
            },
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "test-model",
        };

        var builder = new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>());
        var tree = await builder.BuildTreeAsync(opinion.Concat(news).ToList(), config);

        var opinionHeader = tree.Root.Children.First(n => n.IsGroupHeader && n.Link.DisplayText == "Opinion");
        var newsHeader = tree.Root.Children.First(n => n.IsGroupHeader && n.Link.DisplayText == "News");

        opinionHeader.Children.Select(c => c.Link.Url)
            .Should().BeEquivalentTo(new[] { "https://x.com/opinion/a1", "https://x.com/opinion/a2" });
        newsHeader.Children.Select(c => c.Link.Url)
            .Should().BeEquivalentTo(new[] { "https://x.com/news/b1", "https://x.com/news/b2" });
    }

    // ---- workspace-9k27.5: volatile-pattern stripping hardening ----

    [Theory]
    [InlineData("div.item > .css-1x2y3z > a", "div.item > a")]
    [InlineData("main .sc-bdVaJa strong", "main strong")]
    [InlineData("div.jss123 > div.story", "div.story")]
    [InlineData("section.svelte-1a2b3c a.headline", "section a.headline")]
    public void StripVolatileIds_RemovesBuildHashClasses(string input, string expectedContains)
    {
        var stripped = SelectorDerivation.StripVolatileIds(input);

        stripped.Should().NotContain(".css-").And.NotContain(".sc-")
            .And.NotContain(".jss").And.NotContain(".svelte-");
        foreach (var token in expectedContains.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token is ">") { continue; }
            stripped.Should().Contain(token.TrimStart('>').Trim());
        }
    }

    [Theory]
    [InlineData("div.headline > strong.L5")] // real durable class survives
    [InlineData("div.topcol2 a")]            // digit-bearing but structural id-like class survives? topcol2 has 1 digit at end, len<5 hash body — keep
    public void StripVolatileIds_KeepsDurableClasses(string selector)
    {
        SelectorDerivation.StripVolatileIds(selector).Should().Be(selector);
    }

    [Theory]
    [InlineData("/chatgpt-atlas-review/", true)]   // 2 hyphens + slug-length body
    [InlineData("/us-releases-powerful-anthropic-model/", true)] // 3+ hyphens
    [InlineData("/opinion/", false)]
    [InlineData("/top-stories/", false)]
    [InlineData("/personal-finance/", false)]
    public void IsVolatileUrlPattern_ClassifiesSlugsVsHubs(string pattern, bool volatile_)
    {
        SelectorDerivation.IsVolatileUrlPattern(pattern).Should().Be(volatile_);
    }

    [Fact]
    public void MeaningfulPathSegments_DropsSlugSegments()
    {
        // workspace-9k27.5: the exclude fallback unions these — a headline slug
        // must never become a durable exclude segment.
        var segments = SelectorDerivation.MeaningfulPathSegments(
            "https://x.com/news/2026/07/some-very-long-headline-slug-goes-here").ToList();

        segments.Should().Contain("news");
        segments.Should().NotContain(s => s.Contains("headline"));
    }
}
