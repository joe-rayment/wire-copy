// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-zd96: the deterministic lead→river derivation, exercised against the
/// REAL 400-link techmeme snapshot (Fixtures/techmeme-links.json) so the outcome —
/// not a mock — is asserted with no OpenAI call. The lead is the bbc article the
/// user pasted; the derived river must capture it plus the sibling headlines and
/// none of the citation/ad noise.
/// </summary>
public class LeadOverrideDerivationTests
{
    private const string LeadUrl = "https://www.bbc.com/news/articles/cvgm4e0316zo";

    private sealed record FixtureLink(
        string url, string displayText, int type, int importanceScore,
        string parentSelector, bool isExternal, bool isSponsored, int headerType);

    private static List<LinkInfo> LoadTechmeme()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "techmeme-links.json");
        var raw = JsonSerializer.Deserialize<List<FixtureLink>>(File.ReadAllText(path))!;
        return raw.Select(f => new LinkInfo
        {
            Url = f.url,
            DisplayText = f.displayText,
            Type = (LinkType)f.type,
            ImportanceScore = f.importanceScore,
            ParentSelector = string.IsNullOrEmpty(f.parentSelector) ? null : f.parentSelector,
            IsExternal = f.isExternal,
            IsSponsored = f.isSponsored,
            HeaderType = (HeaderType)f.headerType,
        }).ToList();
    }

    private static LinkInfo Lead(List<LinkInfo> links) =>
        links.First(l => l.Url == LeadUrl);

    [Fact]
    public void Fixture_ContainsTheLeadAsAContentLink()
    {
        var links = LoadTechmeme();
        var lead = Lead(links);
        lead.Type.Should().Be(LinkType.Content, "the bbc article was promoted from External on this aggregator");
        lead.ParentSelector.Should().Contain("div.ii");
    }

    [Fact]
    public void Derive_CapturesTheLeadAndTheStoryRiver_WithoutNoise()
    {
        var links = LoadTechmeme();
        var lead = Lead(links);

        var result = LeadOverrideDerivation.Derive(lead, links);

        result.Should().NotBeNull();
        result!.RiverSelectors.Should().NotBeEmpty();

        var section = new HierarchySection { Name = "Top stories", SortOrder = 0, ParentSelectors = result.RiverSelectors };

        // The lead itself must match — a 0-link Top Story is the bug we are killing.
        NavigationTreeBuilder.MatchesSection(lead, section).Should().BeTrue("the derived river must match the pasted lead");

        // It generalizes to the sibling headlines (#2, #3, …).
        var matched = links.Where(l => !l.IsGroupHeader && NavigationTreeBuilder.MatchesSection(l, section)).ToList();
        matched.Count.Should().BeGreaterThanOrEqualTo(15, "the river must capture the main story headlines, not just the lead");
        result.StoryMatchCount.Should().BeGreaterThanOrEqualTo(15);

        // No ads / sponsored, and none of the source-citation noise.
        matched.Should().OnlyContain(l => !l.IsSponsored, "sponsored links must never enter the story river");
        matched.Should().OnlyContain(l => l.Type == LinkType.Content);
    }

    [Fact]
    public void Derive_DoesNotOverGeneralizeToTheItemContainer()
    {
        // 'div.item' matches the lead but ALSO every source-citation link — the
        // derivation must reject that greedy token in favour of a precise one.
        var links = LoadTechmeme();
        var lead = Lead(links);

        var result = LeadOverrideDerivation.Derive(lead, links)!;

        var section = new HierarchySection { Name = "x", SortOrder = 0, ParentSelectors = result.RiverSelectors };
        var matched = links.Count(l => !l.IsGroupHeader && NavigationTreeBuilder.MatchesSection(l, section));
        var divItemMatches = links.Count(l => l.ParentSelector?.Contains("div.item", StringComparison.Ordinal) == true);

        matched.Should().BeLessThan(divItemMatches,
            "the precise river must match far fewer links than the greedy 'div.item' container");
    }

    [Fact]
    public void Derive_LeadWithNoSelector_ReturnsNull()
    {
        var links = LoadTechmeme();
        var noSelector = new LinkInfo { Url = "https://x/y", DisplayText = "no selector story headline here", Type = LinkType.Content, ImportanceScore = 90 };
        LeadOverrideDerivation.Derive(noSelector, links).Should().BeNull();
    }
}
