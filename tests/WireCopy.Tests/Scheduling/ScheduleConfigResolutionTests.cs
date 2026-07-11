// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-42q8.1 — schedule config resolution must honour the step's DURABLE
/// key (ConfigUrlPattern) before any URL-regex matching, follow the post-redirect
/// final URL, and report "site has configs" separately from "this URL matched",
/// so no caller can claim "has no saved layout" while one exists.
/// </summary>
[Trait("Category", "Unit")]
public class ScheduleConfigResolutionTests
{
    private const string SourceUrl = "https://nyt.example/section/todayspaper";

    private readonly IHierarchyConfigStore _store = Substitute.For<IHierarchyConfigStore>();

    public ScheduleConfigResolutionTests()
    {
        _store.GetConfigAsync(Arg.Any<string>()).Returns((SiteHierarchyConfig?)null);
        _store.GetConfigsForDomainAsync(Arg.Any<string>()).Returns(Array.Empty<SiteHierarchyConfig>());
    }

    private static SiteHierarchyConfig Config(string urlPattern) => new()
    {
        Domain = "nyt.example",
        UrlPattern = urlPattern,
        Sections = new List<HierarchySection> { new() { Name = "Top", SortOrder = 0 } },
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ModelVersion = "test",
    };

    private static RecipeStep Step(string configUrlPattern) =>
        RecipeStep.Create(SourceUrl, "nyt.example", configUrlPattern, "Top", required: true);

    [Fact]
    public async Task ForStep_ExactConfigUrlPattern_BeatsUrlRegexMatch()
    {
        // Two configs for the site; the URL-regex would pick the first, but the
        // step's durable key names the second — the durable key must win.
        var byRegex = Config("^https?://(www\\.)?nyt\\.example/");
        var pinned = Config("^https?://(www\\.)?nyt\\.example/section/todayspaper");
        _store.GetConfigsForDomainAsync(SourceUrl).Returns(new[] { byRegex, pinned });
        _store.GetConfigAsync(SourceUrl).Returns(byRegex);

        var lookup = await ScheduleConfigResolution.ForStepAsync(_store, Step(pinned.UrlPattern));

        lookup.Config.Should().BeSameAs(pinned);
        lookup.SiteHasAnyConfig.Should().BeTrue();
    }

    [Fact]
    public async Task ForStep_PatternGone_FallsBackToUrlMatch()
    {
        var current = Config("^https?://(www\\.)?nyt\\.example/section/todayspaper");
        _store.GetConfigsForDomainAsync(SourceUrl).Returns(new[] { current });
        _store.GetConfigAsync(SourceUrl).Returns(current);

        var lookup = await ScheduleConfigResolution.ForStepAsync(_store, Step("^some-old-pattern$"));

        lookup.Config.Should().BeSameAs(current);
    }

    [Fact]
    public async Task ForStep_SourceUrlMisses_FollowsFinalUrl()
    {
        // The bookmark URL now redirects; the layout was saved on the destination.
        var atFinal = Config("^https?://(www\\.)?nyt\\.example/edition/today");
        _store.GetConfigAsync("https://nyt.example/edition/today").Returns(atFinal);

        var lookup = await ScheduleConfigResolution.ForStepAsync(
            _store, Step("^some-old-pattern$"), finalUrl: "https://nyt.example/edition/today");

        lookup.Config.Should().BeSameAs(atFinal);
    }

    [Fact]
    public async Task ForStep_NothingSaved_ReportsNoSiteConfigs()
    {
        var lookup = await ScheduleConfigResolution.ForStepAsync(_store, Step("^gone$"));

        lookup.Config.Should().BeNull();
        lookup.SiteHasAnyConfig.Should().BeFalse();
    }

    [Fact]
    public async Task ForStep_SiteConfiguredElsewhere_ReportsSiteConfigsWithoutAMatch()
    {
        var other = Config("^https?://(www\\.)?nyt\\.example/other");
        _store.GetConfigsForDomainAsync(SourceUrl).Returns(new[] { other });

        var lookup = await ScheduleConfigResolution.ForStepAsync(_store, Step("^gone$"));

        lookup.Config.Should().BeNull();
        lookup.SiteHasAnyConfig.Should().BeTrue();
        lookup.SiteConfigs.Should().ContainSingle().Which.Should().BeSameAs(other);
    }

    [Fact]
    public async Task ForUrl_ReportsBothTheMatchAndTheSiteWideList()
    {
        var match = Config("^https?://(www\\.)?nyt\\.example/section/todayspaper");
        var other = Config("^https?://(www\\.)?nyt\\.example/other");
        _store.GetConfigAsync(SourceUrl).Returns(match);
        _store.GetConfigsForDomainAsync(SourceUrl).Returns(new[] { other, match });

        var lookup = await ScheduleConfigResolution.ForUrlAsync(_store, SourceUrl);

        lookup.Config.Should().BeSameAs(match);
        lookup.SiteConfigs.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("^https?://(www\\.)?nytimes\\.com/section/todayspaper", "/section/todayspaper")]
    [InlineData("^https?://(www\\.)?nytimes\\.com/?", "nytimes.com (home page)")]
    [InlineData("^https?://(www\\.)?127\\.0\\.0\\.1:8642/?", "127.0.0.1:8642 (home page)")]
    [InlineData("", "the site")]
    public void HumanizePattern_ProducesReadablePaths(string pattern, string expected)
    {
        ScheduleConfigResolution.HumanizePattern(pattern).Should().Be(expected);
    }

    [Fact]
    public void DescribeSitePatterns_SingleConfig_ShowsItsPath()
    {
        var configs = new[] { Config("^https?://(www\\.)?nyt\\.example/section/todayspaper") };

        ScheduleConfigResolution.DescribeSitePatterns(configs).Should().Be("/section/todayspaper");
    }

    [Fact]
    public void DescribeSitePatterns_ManyConfigs_ShowsACount()
    {
        var configs = new[] { Config("a"), Config("b") };

        ScheduleConfigResolution.DescribeSitePatterns(configs).Should().Be("2 saved layouts");
    }
}
