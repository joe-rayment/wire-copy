// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI.Components;
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

    // ---- workspace-5vqk.2: a pick teaches ONE repeating pattern ----

    [Fact]
    public void Derive_UnionStaysCleanNewsRiver_NoRailContamination()
    {
        // The precision-floored union captures the co-equal NEWS river (div.ii)
        // but NOT the podcast/event rails — pulling those in would sink precision
        // below UnionPrecisionFloor. Assert the extracted set, not a log line.
        var links = LoadTechmeme();
        var lead = Lead(links);

        var result = LeadOverrideDerivation.Derive(lead, links)!;
        var section = new HierarchySection { Name = "Stories", SortOrder = 0, ParentSelectors = result.RiverSelectors };
        var matched = links.Where(l => !l.IsGroupHeader && NavigationTreeBuilder.MatchesSection(l, section)).ToList();

        matched.Count.Should().BeInRange(20, 28, "the clean techmeme news river is ~24 headlines");
        matched.Should().OnlyContain(l => l.Type == LinkType.Content && !l.IsSponsored);
        matched.Should().OnlyContain(
            l => l.ParentSelector!.Contains("div.ii", System.StringComparison.Ordinal),
            "the union must not reach into the podcast/event rails (those are separate picks)");
        matched.Should().NotContain(
            l => l.ParentSelector!.Contains("div.podcast", System.StringComparison.Ordinal)
                 || l.ParentSelector!.Contains("div.ne", System.StringComparison.Ordinal),
            "rail clusters stay out of the single-pick 'Stories' river");
    }

    [Fact]
    public void BuildDeterministicLeadOverride_NamesNeutrally_NeverPinsMaxLinks()
    {
        var links = LoadTechmeme();
        var lead = Lead(links);

        var pattern = SetupWizard.DeriveLeadOverrideForTest(lead, links, EmptyConfig());
        pattern.Should().NotBeNull();

        pattern!.Config.Sections[0].Name.Should().Be("Stories", "a pick seeds co-equal 'Stories', never a single 'Top stories'");
        pattern.Config.Sections.Should().OnlyContain(s => s.MaxLinks == null, "no section is force-collapsed to one link");

        var covered = links.Count(l => l.Type == LinkType.Content && !l.IsGroupHeader
            && pattern.Config.Sections.Any(s => NavigationTreeBuilder.MatchesSection(l, s)));
        covered.Should().BeInRange(20, 28, "the whole news river shows, not one story");
    }

    [Fact]
    public void BuildDeterministicLeadOverride_MergesRiver_PreservesDistinctModelSections()
    {
        // The model already found a Podcasts cluster (div.podcast). Pasting a NEWS
        // url must MERGE the derived news river with it — not blanket-replace it —
        // so both co-equal clusters survive and combined coverage exceeds the
        // news-only count (the "collapses to one section" fix).
        var links = LoadTechmeme();
        var lead = Lead(links);
        var podcasts = new HierarchySection { Name = "Podcasts", SortOrder = 0, ParentSelectors = new() { "div.podcast" } };
        var current = EmptyConfig() with { Sections = new List<HierarchySection> { podcasts } };

        var pattern = SetupWizard.DeriveLeadOverrideForTest(lead, links, current)!;

        pattern.Config.Sections[0].Name.Should().Be("Stories");
        pattern.Config.Sections.Should().Contain(s => s.Name == "Podcasts", "a distinct model cluster is preserved, not wiped");

        int Covered(HierarchySection s) =>
            links.Count(l => l.Type == LinkType.Content && !l.IsGroupHeader && NavigationTreeBuilder.MatchesSection(l, s));
        var newsOnly = Covered(pattern.Config.Sections.First(s => s.Name == "Stories"));
        var combined = links.Count(l => l.Type == LinkType.Content && !l.IsGroupHeader
            && pattern.Config.Sections.Any(s => NavigationTreeBuilder.MatchesSection(l, s)));
        combined.Should().BeGreaterThan(newsOnly, "merging the podcast cluster covers more than the news river alone");
    }

    // ---- workspace-5vqk.3: the preview SHOWS the extracted headlines ----

    [Fact]
    public void BuildPreviewCard_RendersRealHeadlines_RowCountEqualsMatchedLinks()
    {
        // A multi-section aggregator layout (news river + podcast + event clusters):
        // the preview must render EVERY matched link's real headline text, one row
        // each — assert the rendered rows are the extracted DisplayText, not a count.
        var links = LoadTechmeme();
        var lead = Lead(links);
        var derived = SetupWizard.DeriveLeadOverrideForTest(lead, links, EmptyConfig())!;
        var config = derived.Config with
        {
            Sections = derived.Config.Sections.Concat(new[]
            {
                new HierarchySection { Name = "Podcasts", SortOrder = 1, ParentSelectors = new() { "div.podcast" } },
                new HierarchySection { Name = "Events", SortOrder = 2, ParentSelectors = new() { "div.ne" } },
            }).ToList(),
        };

        var contentLinks = links.Where(l => l.Type == LinkType.Content && !l.IsGroupHeader).ToList();
        var matched = contentLinks
            .Where(l => config.Sections.Any(s => NavigationTreeBuilder.MatchesSection(l, s)))
            .ToList();

        var card = SetupWizard.BuildPreviewCard(config, links);
        var rendered = SetupWizardOverlay.DescribeCard(card, maxContentLines: int.MaxValue);

        // Every rendered headline row is a "• {DisplayText}" line; assert the SET.
        var headlineRows = rendered.Where(l => l.TrimStart().StartsWith("•", System.StringComparison.Ordinal)).ToList();
        headlineRows.Count.Should().Be(matched.Count, "one rendered row per matched link — the real extraction, not a count");
        headlineRows.Count.Should().BeGreaterThanOrEqualTo(30, "news + podcast + event clusters together list 30+ real headlines");

        // The rows carry the ACTUAL headline strings, not a placeholder.
        foreach (var link in matched.Take(8))
        {
            var needle = link.DisplayText.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries)[0];
            headlineRows.Should().Contain(r => r.Contains(needle, System.StringComparison.Ordinal));
        }
    }

    [Fact]
    public void BuildPreviewCard_UnderExtrapolation_RendersVisiblyEmptyList()
    {
        // A layout matching ~one story renders a near-empty headline list, so the
        // user can SEE the under-extrapolation before saving (row count == matches).
        var links = LoadTechmeme();
        var oneStory = new HierarchySection
        {
            Name = "Stories", SortOrder = 0,
            UrlPatterns = new() { Lead(links).Url },
        };
        var config = EmptyConfig() with { Sections = new List<HierarchySection> { oneStory } };

        var card = SetupWizard.BuildPreviewCard(config, links);
        var rendered = SetupWizardOverlay.DescribeCard(card, maxContentLines: int.MaxValue);
        var headlineRows = rendered.Where(l => l.TrimStart().StartsWith("•", System.StringComparison.Ordinal)).ToList();

        headlineRows.Should().HaveCount(1, "a one-story layout renders exactly one headline row — visibly empty");
    }

    // ---- workspace-5vqk.6: multiple co-equal seed picks ----

    [Fact]
    public void SecondPick_AppendsCoEqualSection_CoveringMoreThanEitherAlone()
    {
        var links = LoadTechmeme();

        // First pick: the news river (24 headlines under div.ii).
        var first = SetupWizard.DeriveLeadOverrideForTest(Lead(links), links, EmptyConfig())!;
        int Covered(SiteHierarchyConfig cfg) => links.Count(l =>
            l.Type == LinkType.Content && !l.IsGroupHeader
            && cfg.Sections.Any(s => NavigationTreeBuilder.MatchesSection(l, s)));
        var singlePickCount = Covered(first.Config);

        // Second pick: a PODCAST item (a genuinely separate co-equal cluster).
        var podcastLead = links.First(l =>
            l.Type == LinkType.Content
            && l.ParentSelector != null
            && l.ParentSelector.Contains("div.podcast", System.StringComparison.Ordinal));
        var second = SetupWizard.DeriveLeadOverrideForTest(podcastLead, links, first.Config)!;

        // Two co-equal pick-derived sections; the first still leads.
        second.Config.Sections.Should().HaveCountGreaterThanOrEqualTo(2);
        second.Config.Sections[0].Name.Should().Be("Stories");
        second.Config.Sections.Should().Contain(s => s.Name == "Stories 2");
        // Together they cover MORE cluster leads than the single news pick alone.
        Covered(second.Config).Should().BeGreaterThan(singlePickCount,
            "the appended podcast cluster adds coverage the news river never had");
    }

    [Fact]
    public void RepeatingTheSamePick_AddsNoDuplicateSection()
    {
        var links = LoadTechmeme();
        var first = SetupWizard.DeriveLeadOverrideForTest(Lead(links), links, EmptyConfig())!;

        // Re-picking a story already in the news river is a no-op — no "Stories 2"
        // duplicate of the same cluster.
        var again = SetupWizard.DeriveLeadOverrideForTest(Lead(links), links, first.Config)!;

        again.Config.Sections.Should().HaveCount(first.Config.Sections.Count);
        again.Config.Sections.Should().NotContain(s => s.Name == "Stories 2");
    }

    // ---- workspace-5vqk.5: cluster-aware verify gate ----

    [Fact]
    public void LeadIsAmbiguous_TechmemeFlatRiver_IsFalse_NoVisionTiebreak()
    {
        // Techmeme's news river is a broad flat top band (21 of 24 within margin) —
        // NOT a small lead contest — so the vision tiebreak must be gated OFF.
        var links = LoadTechmeme();
        var derived = SetupWizard.DeriveLeadOverrideForTest(Lead(links), links, EmptyConfig())!;

        SetupWizard.LeadIsAmbiguous(derived.Config, links).Should().BeFalse(
            "a flat co-equal aggregator has no single lead to break with a vision call");
    }

    [Fact]
    public void BuildPreviewCard_TechmemeNewsOnly_SurfacesUncoveredClusters()
    {
        // A news-only pick leaves the podcast/event clusters uncovered — the preview
        // must SURFACE that (prompt for another pattern), not silently pass as sound.
        var links = LoadTechmeme();
        var config = SetupWizard.DeriveLeadOverrideForTest(Lead(links), links, EmptyConfig())!.Config;

        var card = SetupWizard.BuildPreviewCard(config, links);

        card.Footnote.Should().Contain("uncovered");
        card.Footnote.Should().Contain("add another pattern");
    }

    private static SiteHierarchyConfig EmptyConfig() => new()
    {
        Domain = "techmeme.com",
        UrlPattern = "techmeme.com",
        Sections = new List<HierarchySection>(),
        CreatedAt = System.DateTime.UtcNow,
        ModelVersion = "test",
    };
}
