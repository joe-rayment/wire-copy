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
/// workspace-frpl.9 (B9a) — deterministic run-time recovery. When the saved
/// selectors are over-specific (a hashed CSS class rotated) the resolver loosens
/// them to their stable discriminating tokens, re-matches, and flags the result
/// Recovered — with NO model call. When even that yields 0 it returns a loud
/// ZeroMatch (empty items + human diagnostic), never empty-as-success.
/// </summary>
[Trait("Category", "Unit")]
public class SectionResolverRecoveryTests
{
    private readonly SectionResolver _resolver = new();

    // The saved selector is over-specific: a stable token (section.business) plus a
    // hashed token (div.css-OLD123) that rotates between captures.
    private static SiteHierarchyConfig ConfigWithOverSpecificBusiness() => new()
    {
        Domain = "nytimes.com",
        UrlPattern = "^https?://(www\\.)?nytimes\\.com/?$",
        Sections = new List<HierarchySection>
        {
            new() { Name = "Top Stories", SortOrder = 0, ParentSelectors = new List<string> { "section.top" } },
            new() { Name = "Business", SortOrder = 1, ParentSelectors = new List<string> { "section.business div.css-OLD123" } },
        },
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ModelVersion = "test",
    };

    private static LinkInfo Link(string url, string text, string parent, string? sectionTitle = null) => new()
    {
        Url = url, DisplayText = text, Type = LinkType.Content, ImportanceScore = 60,
        ParentSelector = parent, SectionTitle = sectionTitle,
    };

    private static RecipeStep BusinessStep(bool required = true) =>
        RecipeStep.Create("https://www.nytimes.com/", "nytimes.com", "^x$", "Business",
            required: required, sortOrderFallback: 1);

    [Fact]
    public void OverSpecificSelector_RotatedHash_RecoversViaStableToken_NoModelCall()
    {
        // The hash rotated OLD123 -> NEW789, so the FULL saved selector no longer
        // substring-matches, but the stable token `section.business` still does.
        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/lead", "Biz lead", "section.business div.css-NEW789 a", sectionTitle: "Business Daily"),
            Link("https://www.nytimes.com/second", "Biz second", "section.business div.css-NEW789 a", sectionTitle: "Business Daily"),
        };

        var r = _resolver.Resolve(ConfigWithOverSpecificBusiness(), links, BusinessStep());

        r.Status.Should().Be(ResolutionStatus.Recovered, "the over-specific selector was loosened to its live token");
        r.Items.Should().HaveCount(2);
        r.Items.Select(i => i.Url).Should().Equal("https://www.nytimes.com/lead", "https://www.nytimes.com/second");
        r.Diagnostic.Should().Contain("section.business").And.Contain("ratify");
    }

    [Fact]
    public void Recovered_IsDistinguishableFromCleanResolved()
    {
        // Heading drifted to "Business Daily" so the heading-name primary can't match
        // either — only the loosened selector token recovers it.
        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/lead", "Biz lead", "section.business div.css-NEW789 a", sectionTitle: "Business Daily"),
        };

        var recovered = _resolver.Resolve(ConfigWithOverSpecificBusiness(), links, BusinessStep());

        // Same links, but a config whose FULL saved selector still matches → clean Resolved.
        var freshConfig = ConfigWithOverSpecificBusiness();
        freshConfig.Sections.Single(s => s.Name == "Business").ParentSelectors.Clear();
        freshConfig.Sections.Single(s => s.Name == "Business").ParentSelectors.Add("section.business");
        var clean = _resolver.Resolve(freshConfig, links, BusinessStep());

        recovered.Status.Should().Be(ResolutionStatus.Recovered);
        clean.Status.Should().Be(ResolutionStatus.Resolved);
        recovered.Status.Should().NotBe(clean.Status, "B11 must be able to tell a re-derived match from a clean one to ask for ratification");
    }

    [Fact]
    public void NoLiveAnchor_StillZeroMatch_WithDiagnostic_NotEmptyAsSuccess()
    {
        // Neither the full selector nor ANY of its tokens (section.business / div.css-OLD123)
        // appear in today's links → recovery yields nothing → loud ZeroMatch.
        var links = new List<LinkInfo>
        {
            Link("https://www.nytimes.com/x", "Unrelated", "aside.sidebar div.promo a", sectionTitle: "Newsletters"),
        };

        var r = _resolver.Resolve(ConfigWithOverSpecificBusiness(), links, BusinessStep());

        r.Status.Should().Be(ResolutionStatus.ZeroMatch);
        r.Items.Should().BeEmpty("a section with no live anchor is never dressed up as success");
        r.Diagnostic.Should().NotBeNullOrEmpty();
        r.Diagnostic.Should().Contain("re-run AI setup");
    }

    [Fact]
    public void SingleTokenSavedSelector_ThatMisses_DoesNotSpuriouslyRecover()
    {
        // A single-token selector (section.business) that already missed cannot recover —
        // the only token equals the full selector that just failed. Guards the existing
        // ZeroMatch contract against false positives.
        var config = ConfigWithOverSpecificBusiness();
        config.Sections.Single(s => s.Name == "Business").ParentSelectors.Clear();
        config.Sections.Single(s => s.Name == "Business").ParentSelectors.Add("section.business");

        var links = new List<LinkInfo> { Link("https://www.nytimes.com/t", "Top", "section.top a", sectionTitle: "Top Stories") };

        _resolver.Resolve(config, links, BusinessStep()).Status.Should().Be(ResolutionStatus.ZeroMatch);
    }
}
