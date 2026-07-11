// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-frpl.3 — the resolution crux. The marquee proof: ONE durable step
/// resolves the NYT business section on a weekday ('Business Daily') AND on a
/// Sunday ('Sunday Business') via the SAME saved selector — the heading text
/// never carries the match.
/// </summary>
[Trait("Category", "Unit")]
public class SectionResolverTests
{
    private const string BusinessSelector = "section.business a";
    private const string TopSelector = "section.top a";

    private static SiteHierarchyConfig NytConfig() => new()
    {
        Domain = "nytimes.com",
        UrlPattern = "^https?://(www\\.)?nytimes\\.com/?$",
        Sections = new List<HierarchySection>
        {
            new() { Name = "Top Stories", SortOrder = 0, ParentSelectors = new List<string> { "section.top" } },
            new() { Name = "Business", SortOrder = 1, ParentSelectors = new List<string> { "section.business" } },
        },
        CreatedAt = DateTime.UtcNow,
        ModelVersion = "test",
    };

    private static LinkInfo Link(string url, string text, string parent, string? sectionTitle = null) => new()
    {
        Url = url, DisplayText = text, Type = LinkType.Content, ImportanceScore = 60,
        ParentSelector = parent, SectionTitle = sectionTitle,
    };

    private static RecipeStep BusinessStep(TakeMode mode = TakeMode.WholeSection, int? count = null, IEnumerable<string>? aliases = null) =>
        RecipeStep.Create("https://www.nytimes.com/", "nytimes.com", "^x$", "Business",
            takeMode: mode, takeCount: count, sortOrderFallback: 1, headingAliases: aliases);

    private readonly SectionResolver _resolver = new();

    [Fact]
    public void Weekday_BusinessDaily_ResolvesViaDurableSelector()
    {
        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/2026/05/29/business/a", "Markets dip", BusinessSelector, sectionTitle: "Business Daily"),
            Link("https://www.nytimes.com/2026/05/29/business/b", "Fed holds", BusinessSelector, sectionTitle: "Business Daily"),
        };

        var r = _resolver.Resolve(NytConfig(), links, BusinessStep());

        r.Status.Should().Be(ResolutionStatus.Resolved);
        r.Tier.Should().Be(SectionMatchTier.Selector, "the saved selector carries the match, not the heading");
        r.Items.Should().HaveCount(2);
    }

    [Fact]
    public void Sunday_SundayBusiness_ResolvesViaTheSameDurableSelector()
    {
        // SAME step, SAME selector — only the heading text differs on Sundays.
        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/2026/05/31/business/x", "A founder's tale", BusinessSelector, sectionTitle: "Sunday Business"),
        };

        var r = _resolver.Resolve(NytConfig(), links, BusinessStep());

        r.Status.Should().Be(ResolutionStatus.Resolved);
        r.Tier.Should().Be(SectionMatchTier.Selector, "durable path, not the alias, carries the marquee case");
        r.Items.Should().ContainSingle().Which.Title.Should().Be("A founder's tale");
    }

    [Fact]
    public void ArticleUrlRotation_SameSelectors_StillResolves()
    {
        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/2099/12/31/business/totally-new-url", "Future news", BusinessSelector, sectionTitle: "Business Daily"),
        };
        _resolver.Resolve(NytConfig(), links, BusinessStep()).Status.Should().Be(ResolutionStatus.Resolved);
    }

    [Fact]
    public void CrossSectionOverlap_DoesNotUnderCountTheTargetSection()
    {
        // An earlier (Top) section whose selector ALSO matches a business link
        // would, in the greedy whole-tree build, steal it. Resolving the target
        // in isolation must still count it under Business.
        var config = NytConfig();
        config.Sections[0].ParentSelectors.Add("section.business"); // Top now also matches business links

        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/b1", "Biz 1", BusinessSelector, sectionTitle: "Business Daily"),
            Link("https://www.nytimes.com/b2", "Biz 2", BusinessSelector, sectionTitle: "Business Daily"),
        };

        var r = _resolver.Resolve(config, links, BusinessStep());
        r.Status.Should().Be(ResolutionStatus.Resolved);
        r.MatchCount.Should().Be(2, "the business step counts its own links regardless of an overlapping earlier section");
    }

    [Fact]
    public void SingleTopStory_ReturnsFirstNonAd_InDocumentOrder()
    {
        var config = NytConfig() with { ExcludeSelectors = new List<string> { "aside.ad" } };
        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/ad", "Sponsored", "section.business aside.ad a", sectionTitle: "Business Daily"),
            Link("https://www.nytimes.com/lead", "The lead", BusinessSelector, sectionTitle: "Business Daily"),
            Link("https://www.nytimes.com/second", "Second", BusinessSelector, sectionTitle: "Business Daily"),
        };

        var r = _resolver.Resolve(config, links, BusinessStep(TakeMode.SingleTopStory));
        r.Items.Should().ContainSingle().Which.Title.Should().Be("The lead", "the co-located ad is excluded, then the first story wins");
    }

    [Fact]
    public void ZeroMatch_ReturnsExplicitStatus_EmptyItems_AndDiagnostic()
    {
        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/t1", "A top story", TopSelector, sectionTitle: "Top Stories"),
        };
        var r = _resolver.Resolve(NytConfig(), links, BusinessStep());

        r.Status.Should().Be(ResolutionStatus.ZeroMatch);
        r.Items.Should().BeEmpty();
        r.Diagnostic.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SectionNotFound_WhenConfigLacksTheSection()
    {
        var config = NytConfig();
        config.Sections.RemoveAll(s => s.Name == "Business");
        // SortOrderFallback 1 also absent now.
        var step = RecipeStep.Create("https://www.nytimes.com/", "nytimes.com", "^x$", "Business", sortOrderFallback: 9);
        var r = _resolver.Resolve(config, new List<LinkInfo>(), step);
        r.Status.Should().Be(ResolutionStatus.SectionNotFound);
        r.Diagnostic.Should().Contain("re-run AI setup");
    }

    [Fact]
    public void HeadingAliasFallback_ResolvesOnlyWithTheAlias()
    {
        // Selector/url DON'T match (different parent), but the live heading is a
        // known alias. With the alias → HeadingAlias tier; without → ZeroMatch.
        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/x", "Sunday biz", "div.unknown a", sectionTitle: "Sunday Business"),
        };

        var withAlias = _resolver.Resolve(NytConfig(), links, BusinessStep(aliases: new[] { "Sunday Business" }));
        withAlias.Status.Should().Be(ResolutionStatus.Resolved);
        withAlias.Tier.Should().Be(SectionMatchTier.HeadingAlias);

        var withoutAlias = _resolver.Resolve(NytConfig(), links, BusinessStep());
        withoutAlias.Status.Should().Be(ResolutionStatus.ZeroMatch, "no alias → no false positive");
    }

    // ---- workspace-42q8.2: whole-page steps (single-list sites) ----
    private static SiteHierarchyConfig FlatConfig() => new()
    {
        Domain = "text.npr.example",
        UrlPattern = "^https?://(www\\.)?text\\.npr\\.example/?",
        Sections = new List<HierarchySection>(),
        CreatedAt = DateTime.UtcNow,
        ModelVersion = "document-order",
        ExcludeUrlPatterns = new List<string> { "/sponsored/" },
    };

    private static RecipeStep WholePageStep(TakeMode mode = TakeMode.WholeSection, int? count = null) =>
        RecipeStep.Create("https://text.npr.example/", "text.npr.example", "^x$", string.Empty,
            takeMode: mode, takeCount: count, scope: StepScope.WholePage);

    [Fact]
    public void WholePage_FlatConfig_ResolvesEveryContentLinkInDocumentOrder_ApplyingExcludes()
    {
        var links = new List<LinkInfo>
        {
            Link("https://text.npr.example/a", "First", "ul li a"),
            Link("https://text.npr.example/sponsored/x", "Ad", "ul li a"),
            Link("https://text.npr.example/b", "Second", "ul li a"),
        };

        var r = _resolver.Resolve(FlatConfig(), links, WholePageStep());

        r.Status.Should().Be(ResolutionStatus.Resolved);
        r.Items.Select(i => i.Title).Should().Equal("First", "Second");
        r.MatchCount.Should().Be(2, "the excluded sponsor never counts");
        r.Tier.Should().BeNull("no section-matching tier applies to a whole page");
    }

    [Theory]
    [InlineData(TakeMode.SingleTopStory, null, 1)]
    [InlineData(TakeMode.TopN, 2, 2)]
    [InlineData(TakeMode.WholeSection, null, 3)]
    public void WholePage_AppliesEveryTakeMode(TakeMode mode, int? count, int expected)
    {
        var links = new List<LinkInfo>
        {
            Link("https://text.npr.example/a", "A", "ul li a"),
            Link("https://text.npr.example/b", "B", "ul li a"),
            Link("https://text.npr.example/c", "C", "ul li a"),
        };

        var r = _resolver.Resolve(FlatConfig(), links, WholePageStep(mode, count));

        r.Status.Should().Be(ResolutionStatus.Resolved);
        r.Items.Should().HaveCount(expected);
        r.Items[0].Title.Should().Be("A", "document order is preserved");
    }

    [Fact]
    public void WholePage_OnASectionedConfig_AlsoResolves()
    {
        // "All stories" is offered on sectioned sites too — it must not depend on
        // the config being flat.
        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/1", "Top", TopSelector),
            Link("https://www.nytimes.com/2", "Biz", BusinessSelector),
        };
        var step = RecipeStep.Create("https://www.nytimes.com/", "nytimes.com", "^x$", string.Empty, scope: StepScope.WholePage);

        var r = _resolver.Resolve(NytConfig(), links, step);

        r.Status.Should().Be(ResolutionStatus.Resolved);
        r.Items.Should().HaveCount(2);
    }

    [Fact]
    public void WholePage_EmptyPage_IsALoudZeroMatch()
    {
        var r = _resolver.Resolve(FlatConfig(), new List<LinkInfo>(), WholePageStep());

        r.Status.Should().Be(ResolutionStatus.ZeroMatch);
        r.Diagnostic.Should().NotBeNullOrEmpty("never an empty list dressed as success");
    }

    [Fact]
    public void PinnedSectionStep_AgainstFlatConfig_NamesTheRealCause()
    {
        // The layout never had sections — the diagnostic must say that (and point at
        // 'All stories'), not pretend a section was renamed.
        var step = RecipeStep.Create("https://text.npr.example/", "text.npr.example", "^x$", "Top Stories");

        var r = _resolver.Resolve(FlatConfig(), new List<LinkInfo>(), step);

        r.Status.Should().Be(ResolutionStatus.SectionNotFound);
        r.Diagnostic.Should().Contain("single list").And.Contain(RecipeStep.WholePageSectionName);
    }
}
