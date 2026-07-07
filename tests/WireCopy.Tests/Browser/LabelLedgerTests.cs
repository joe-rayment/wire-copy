// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-t1ok.3: the user-label ledger — persistence round-trip, session
/// merging, carry-forward across model rounds, More-menu routing precedence,
/// and the rank-order overlay (preview and tree must agree).
/// </summary>
[Trait("Category", "Unit")]
public class LabelLedgerTests : IDisposable
{
    private const string TestDomain = "test-t1ok3.example.com";
    private readonly string _storagePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WireCopy",
        "hierarchy");

    public void Dispose()
    {
        try
        {
            var path = Path.Combine(_storagePath, $"{TestDomain}.json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static UserLinkLabel Label(
        string url,
        LinkLabelKind kind,
        int? rank = null,
        DateTime? at = null) => new()
    {
        Url = url,
        Text = url,
        Kind = kind,
        Rank = rank,
        LabeledAt = at ?? new DateTime(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc),
    };

    private static SiteHierarchyConfig Config(
        List<HierarchySection>? sections = null,
        List<UserLinkLabel>? labels = null,
        List<string>? moreSelectors = null,
        List<string>? moreUrlPatterns = null,
        List<string>? instructions = null)
    {
        return new SiteHierarchyConfig
        {
            Domain = TestDomain,
            UrlPattern = $"^https?://{TestDomain.Replace(".", "\\.")}/?$",
            Sections = sections ?? new List<HierarchySection>(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "test-model",
            Kind = LayoutKind.AiCurated,
            Version = 3,
            UserLabels = labels ?? new List<UserLinkLabel>(),
            MoreSelectors = moreSelectors ?? new List<string>(),
            MoreUrlPatterns = moreUrlPatterns ?? new List<string>(),
            UserInstructions = instructions ?? new List<string>(),
        };
    }

    private static LinkInfo Content(string url, string text, string? parent = null, int importance = 85) => new()
    {
        Url = url,
        DisplayText = text,
        Type = LinkType.Content,
        ImportanceScore = importance,
        ParentSelector = parent,
    };

    // ---- persistence -------------------------------------------------------

    [Fact]
    public async Task Store_RoundTrips_LedgerAndMoreRules()
    {
        var store = new HierarchyConfigStore(Substitute.For<ILogger<HierarchyConfigStore>>());
        var config = Config(
            sections: new List<HierarchySection> { new() { Name = "Stories", SortOrder = 0, UrlPatterns = new List<string> { "/story" } } },
            labels: new List<UserLinkLabel>
            {
                Label($"https://{TestDomain}/story-a", LinkLabelKind.Article, rank: 1),
                Label($"https://{TestDomain}/ad-1", LinkLabelKind.Ad),
                Label($"https://{TestDomain}/menu-1", LinkLabelKind.Menu),
            },
            moreSelectors: new List<string> { "div.chrome" },
            moreUrlPatterns: new List<string> { "/menu-" },
            instructions: new List<string> { "hide the podcast links" });

        (await store.SaveConfigAsync(config)).Should().BeTrue();
        var loaded = await store.GetConfigAsync($"https://{TestDomain}/");

        loaded.Should().NotBeNull();
        loaded!.UserLabels.Should().HaveCount(3);
        loaded.UserLabels[0].Kind.Should().Be(LinkLabelKind.Article);
        loaded.UserLabels[0].Rank.Should().Be(1);
        loaded.UserLabels[1].Kind.Should().Be(LinkLabelKind.Ad);
        loaded.MoreSelectors.Should().ContainSingle().Which.Should().Be("div.chrome");
        loaded.MoreUrlPatterns.Should().ContainSingle().Which.Should().Be("/menu-");
        loaded.UserInstructions.Should().ContainSingle().Which.Should().Be("hide the podcast links");
    }

    // ---- MergeLabels -------------------------------------------------------

    [Fact]
    public void MergeLabels_LatestWinsByNormalizedUrl()
    {
        var prior = new List<UserLinkLabel> { Label("https://www.example.com/x?utm=1", LinkLabelKind.Ad) };
        var latest = new List<UserLinkLabel> { Label("http://example.com/x", LinkLabelKind.Article, rank: 1) };

        var merged = LabelDerivation.MergeLabels(prior, latest);

        merged.Should().ContainSingle();
        merged[0].Kind.Should().Be(LinkLabelKind.Article);
        merged[0].Rank.Should().Be(1);
    }

    [Fact]
    public void MergeLabels_ClearedOnPageLabelIsDropped_OffPageLabelIsKept()
    {
        var prior = new List<UserLinkLabel>
        {
            Label("https://example.com/seen-and-cleared", LinkLabelKind.Ad),
            Label("https://example.com/rotated-off-page", LinkLabelKind.Ad),
        };
        var latest = new List<UserLinkLabel>(); // user cleared everything she saw
        var seen = new[] { "https://example.com/seen-and-cleared" };

        var merged = LabelDerivation.MergeLabels(prior, latest, seen);

        merged.Should().ContainSingle()
            .Which.Url.Should().Be("https://example.com/rotated-off-page");
    }

    [Fact]
    public void MergeLabels_RecompactsArticleRanks_SessionOrderFirst()
    {
        var prior = new List<UserLinkLabel>
        {
            Label("https://example.com/old-1", LinkLabelKind.Article, rank: 1),
            Label("https://example.com/old-2", LinkLabelKind.Article, rank: 2),
        };
        var latest = new List<UserLinkLabel>
        {
            Label("https://example.com/new-a", LinkLabelKind.Article, rank: 1),
            Label("https://example.com/old-2", LinkLabelKind.Article, rank: 2),
        };

        var merged = LabelDerivation.MergeLabels(prior, latest);

        var ranks = merged.Where(l => l.Kind == LinkLabelKind.Article)
            .ToDictionary(l => l.Url, l => l.Rank);
        ranks["https://example.com/new-a"].Should().Be(1);
        ranks["https://example.com/old-2"].Should().Be(2);
        ranks["https://example.com/old-1"].Should().Be(3, "surviving prior articles order behind the session's");
    }

    [Fact]
    public void MergeLabels_CapsAtMax_ArticlesSurviveFirst()
    {
        var prior = Enumerable.Range(0, SiteHierarchyConfig.MaxUserLabels)
            .Select(i => Label($"https://example.com/ad-{i}", LinkLabelKind.Ad, at: DateTime.UtcNow.AddDays(-i)))
            .ToList();
        var latest = new List<UserLinkLabel> { Label("https://example.com/story", LinkLabelKind.Article, rank: 1) };

        var merged = LabelDerivation.MergeLabels(prior, latest);

        merged.Should().HaveCount(SiteHierarchyConfig.MaxUserLabels);
        merged.Should().Contain(l => l.Kind == LinkLabelKind.Article);
    }

    // ---- CarryUserState ----------------------------------------------------

    [Fact]
    public void CarryUserState_CopiesLedgerOntoFreshModelConfig()
    {
        var prior = Config(
            labels: new List<UserLinkLabel> { Label("https://example.com/ad", LinkLabelKind.Ad) },
            moreSelectors: new List<string> { "div.chrome" },
            moreUrlPatterns: new List<string> { "/menu-" },
            instructions: new List<string> { "merge the sections" });

        // A model round produces a brand-new config with none of the user state.
        var fresh = Config(sections: new List<HierarchySection>
        {
            new() { Name = "Rebuilt", SortOrder = 0, UrlPatterns = new List<string> { "/story" } },
        });

        var carried = LabelDerivation.CarryUserState(fresh, prior);

        carried.Sections.Should().ContainSingle().Which.Name.Should().Be("Rebuilt");
        carried.UserLabels.Should().BeEquivalentTo(prior.UserLabels);
        carried.UserInstructions.Should().BeEquivalentTo(prior.UserInstructions);
        carried.MoreSelectors.Should().BeEquivalentTo(prior.MoreSelectors);
        carried.MoreUrlPatterns.Should().BeEquivalentTo(prior.MoreUrlPatterns);
    }

    // ---- More routing precedence ------------------------------------------

    [Fact]
    public async Task BuildTree_MenuRoutedLink_BeatsBroadSectionSelector()
    {
        var logger = Substitute.For<ILogger<NavigationTreeBuilder>>();
        var builder = new NavigationTreeBuilder(logger);
        var links = new List<LinkInfo>
        {
            Content("https://example.com/story-1", "Story one", parent: "div.river a"),
            Content("https://example.com/story-2", "Story two", parent: "div.river a"),
            Content("https://example.com/menu-archive", "Archive", parent: "div.river a"),
        };
        var config = Config(
            sections: new List<HierarchySection>
            {
                new() { Name = "Stories", SortOrder = 0, ParentSelectors = new List<string> { "div.river" } },
            },
            moreUrlPatterns: new List<string> { "/menu-" });

        var tree = await builder.BuildTreeAsync(links, config);

        var stories = tree.Root.Children.First(c => c.Link.DisplayText == "Stories");
        stories.Children.Select(c => c.Link.DisplayText)
            .Should().BeEquivalentTo(new[] { "Story one", "Story two" }, "the menu link must not be claimed by the river");
        var more = tree.Root.Children.FirstOrDefault(c => c.Link.DisplayText == "More");
        more.Should().NotBeNull();
        more!.Children.Select(c => c.Link.DisplayText).Should().Contain("Archive");
    }

    // ---- Rank-order overlay -------------------------------------------------

    [Fact]
    public void OrderByLabeledRank_RankedFirst_UnrankedKeepDocumentOrder()
    {
        var links = new List<LinkInfo>
        {
            Content("https://example.com/c", "C"),
            Content("https://example.com/b", "B"),
            Content("https://example.com/a", "A"),
            Content("https://example.com/d", "D"),
        };
        var config = Config(labels: new List<UserLinkLabel>
        {
            Label("https://example.com/a", LinkLabelKind.Article, rank: 1),
            Label("https://example.com/b", LinkLabelKind.Article, rank: 2),
        });

        var ordered = NavigationTreeBuilder.OrderByLabeledRank(links, config);

        ordered.Select(l => l.DisplayText).Should().ContainInOrder("A", "B", "C", "D");
    }

    [Fact]
    public async Task BuildTree_SectionLinks_FollowLabeledRankOrder()
    {
        var logger = Substitute.For<ILogger<NavigationTreeBuilder>>();
        var builder = new NavigationTreeBuilder(logger);
        var links = new List<LinkInfo>
        {
            Content("https://example.com/story-x", "X", parent: "div.river a"),
            Content("https://example.com/story-y", "Y", parent: "div.river a"),
            Content("https://example.com/story-z", "Z", parent: "div.river a"),
        };
        var config = Config(
            sections: new List<HierarchySection>
            {
                new() { Name = "Stories", SortOrder = 0, ParentSelectors = new List<string> { "div.river" } },
            },
            labels: new List<UserLinkLabel>
            {
                Label("https://example.com/story-z", LinkLabelKind.Article, rank: 1),
                Label("https://example.com/story-x", LinkLabelKind.Article, rank: 2),
            });

        var tree = await builder.BuildTreeAsync(links, config);

        var stories = tree.Root.Children.First(c => c.Link.DisplayText == "Stories");
        stories.Children.Select(c => c.Link.DisplayText)
            .Should().ContainInOrder("Z", "X", "Y");
    }

    [Fact]
    public void BuildPreviewRows_MirrorsRankOrderAndMoreRouting()
    {
        var links = new List<LinkInfo>
        {
            Content("https://example.com/story-x", "X", parent: "div.river a"),
            Content("https://example.com/story-y", "Y", parent: "div.river a"),
            Content("https://example.com/menu-archive", "Archive", parent: "div.river a"),
        };
        var config = Config(
            sections: new List<HierarchySection>
            {
                new() { Name = "Stories", SortOrder = 0, ParentSelectors = new List<string> { "div.river" } },
            },
            labels: new List<UserLinkLabel>
            {
                Label("https://example.com/story-y", LinkLabelKind.Article, rank: 1),
            },
            moreUrlPatterns: new List<string> { "/menu-" });

        var rows = SetupWizard.BuildPreviewRows(config, links);

        var storyRows = rows.Where(r => r.Link != null).Select(r => r.Link!.DisplayText).ToList();
        storyRows.Should().ContainInOrder("Y", "X");
        storyRows.Should().NotContain("Archive", "menu-routed links leave the story sections");
    }
}
