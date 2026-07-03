// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-gyw5: the generic grouped tree must lead the Content group with the real top-story
/// links, not the low-value promo/menu chrome a publisher (NYT) places first in the DOM — while
/// leaving an aggregator river (Hacker News, Techmeme, whose DOM order IS the editorial rank and
/// whose importance score does NOT track it) in DOM order.
/// </summary>
[Trait("Category", "Unit")]
public class NavigationTreeBuilderOrderingTests
{
    private readonly NavigationTreeBuilder _builder;

    public NavigationTreeBuilderOrderingTests()
    {
        _builder = new NavigationTreeBuilder(Substitute.For<ILogger<NavigationTreeBuilder>>());
    }

    private static LinkInfo Content(string text, int importance, bool external, string? url = null) =>
        new()
        {
            Url = url ?? $"https://site.test/{text.Replace(' ', '-')}",
            DisplayText = text,
            Type = LinkType.Content,
            ImportanceScore = importance,
            IsExternal = external,
        };

    private static List<string> ContentOrder(NavigationTree tree) =>
        tree.Root.Children
            .Where(c => !c.IsGroupHeader && c.Link.Type == LinkType.Content)
            .Select(c => c.Link.DisplayText)
            .ToList();

    [Fact]
    public void Publisher_LeadsWithStories_DemotingThePromoChromeThatComesFirstInTheDom()
    {
        // The NYT shape: same-domain (internal) links where a block of promo/podcast/column chrome
        // (importance <= 85) precedes the real headlines (importance 88-100) in DOM order. The
        // chrome is demoted below the stories; both tiers keep their DOM order (stable partition).
        var links = new List<LinkInfo>
        {
            Content("The Daily podcast", 35, external: false),
            Content("Hard Fork podcast", 85, external: false),
            Content("Modern Love column", 85, external: false),
            Content("How the Heat Is Upending Plans", 100, external: false),
            Content("Calls for Aid in Washington Climb", 88, external: false),
            Content("National Guard Deployment Expands", 93, external: false),
        };

        var order = ContentOrder(_builder.BuildGroupedTree(links));

        // Lead-grade stories (>= 86) first, IN DOM ORDER (not re-sorted by importance):
        order.Should().StartWith(new[]
        {
            "How the Heat Is Upending Plans",
            "Calls for Aid in Washington Climb",
            "National Guard Deployment Expands",
        });
        // Sub-floor chrome demoted to the bottom, IN DOM ORDER:
        order.Skip(3).Should().Equal("The Daily podcast", "Hard Fork podcast", "Modern Love column");
    }

    [Fact]
    public void Publisher_CuratedOnDomainRiver_KeepsEditorialDomOrder_NoImportanceResort()
    {
        // Regression guard (review finding): a curated/reverse-chron on-domain list (danluu.com
        // shape) where every lead is >= the floor but importance VARIES by headline length must NOT
        // be re-sorted — the author's chosen lead (imp 88) must stay first, above a higher-scoring
        // (imp 93) later item.
        var links = new List<LinkInfo>
        {
            Content("Steve Ballmer was underrated (the chosen lead)", 88, external: false),
            Content("How good can you be at Codenames (scores higher)", 93, external: false),
            Content("A discussion of discussions", 88, external: false),
            Content("What the FTC got wrong", 93, external: false),
        };

        ContentOrder(_builder.BuildGroupedTree(links)).Should().Equal(
            "Steve Ballmer was underrated (the chosen lead)",
            "How good can you be at Codenames (scores higher)",
            "A discussion of discussions",
            "What the FTC got wrong");
    }

    [Fact]
    public void Aggregator_KeepsDomOrder_BecauseItsRankDoesNotTrackImportance()
    {
        // The Hacker News shape: off-domain (external) story links whose editorial rank (DOM order)
        // does NOT match the importance score — story #1 scores LOWER than a lower-ranked story.
        // Re-sorting by importance would scramble the rank, so aggregators must keep DOM order.
        var links = new List<LinkInfo>
        {
            Content("HN #1 top story", 88, external: true),
            Content("HN #2 story", 88, external: true),
            Content("HN #3 higher-scoring story", 93, external: true),
            Content("HN #4 story", 78, external: true),
        };

        var order = ContentOrder(_builder.BuildGroupedTree(links));

        order.Should().Equal(
            "HN #1 top story",
            "HN #2 story",
            "HN #3 higher-scoring story",
            "HN #4 story");
    }

    [Fact]
    public void Publisher_ImportanceSortIsStable_EqualScoresKeepDomOrder()
    {
        var links = new List<LinkInfo>
        {
            Content("story A (dom 0)", 90, external: false),
            Content("story B (dom 1)", 90, external: false),
            Content("story C (dom 2)", 90, external: false),
        };

        ContentOrder(_builder.BuildGroupedTree(links))
            .Should().Equal("story A (dom 0)", "story B (dom 1)", "story C (dom 2)");
    }

    [Fact]
    public void Publisher_Cap_KeepsTheTopStoriesByImportance_NotTheFirst100InDomOrder()
    {
        // 100 low-importance promos first, then a real high-importance story — the story must
        // survive the MaxContentLinks cap (which would have dropped it in DOM order).
        var links = Enumerable.Range(0, NavigationTreeBuilder.MaxContentLinks)
            .Select(i => Content($"promo {i}", 40, external: false))
            .ToList();
        links.Add(Content("THE top story", 100, external: false));

        var order = ContentOrder(_builder.BuildGroupedTree(links));

        order.Should().HaveCount(NavigationTreeBuilder.MaxContentLinks);
        order[0].Should().Be("THE top story", "the cap keeps the highest-importance story, not the DOM-first promos");
    }

    [Fact]
    public void MixedButMajorityExternal_IsTreatedAsAggregator_KeepsDomOrder()
    {
        // 3 external + 2 internal = 60% external >= threshold -> aggregator -> DOM order preserved,
        // so the internal chrome (a job link / Ask-HN) does NOT float above the external stories.
        var links = new List<LinkInfo>
        {
            Content("external story 1", 88, external: true),
            Content("internal job/ask chrome", 93, external: false),
            Content("external story 2", 88, external: true),
            Content("external story 3", 85, external: true),
            Content("internal ask chrome", 85, external: false),
        };

        ContentOrder(_builder.BuildGroupedTree(links))
            .Should().StartWith("external story 1", "an aggregator must not float internal chrome above its lead story");
    }
}
