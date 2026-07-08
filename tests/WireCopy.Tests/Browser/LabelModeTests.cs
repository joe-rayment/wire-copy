// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-t1ok.4/.5/.6 — label mode: the hand-categorizer UI (badges, rank
/// re-compaction, all-links rescue view), the deterministic label→config
/// derivation with its self-test, and the budget-guarded AI generalization
/// fallback whose result gets the label rules re-applied in code.
/// </summary>
[Trait("Category", "Unit")]
public class LabelModeTests
{
    // ---- scripted input helpers ---------------------------------------------

    private static NavigationCommand Key(char c) => new() { Type = CommandType.NoOp, RawKeyChar = c };

    private static NavigationCommand Cmd(CommandType type) => new() { Type = type };

    private static IInputHandler Input(params NavigationCommand[] commands)
    {
        var input = Substitute.For<IInputHandler>();
        input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(commands[0], commands.Skip(1).ToArray());
        return input;
    }

    // ---- fixtures ------------------------------------------------------------

    private static LinkInfo Story(string url, string text, string parent = "div.river a", string? sectionTitle = null) => new()
    {
        Url = url,
        DisplayText = text,
        Type = LinkType.Content,
        ImportanceScore = 85,
        ParentSelector = parent,
        SectionTitle = sectionTitle,
    };

    private static SiteHierarchyConfig ConfigWith(
        List<HierarchySection>? sections = null,
        List<string>? excludeUrlPatterns = null,
        List<UserLinkLabel>? labels = null,
        LayoutKind kind = LayoutKind.AiCurated) => new()
    {
        Domain = "x.com",
        UrlPattern = "^https?://x\\.com",
        Sections = sections ?? new List<HierarchySection>
        {
            new() { Name = "Headlines", SortOrder = 0, UrlPatterns = new List<string> { "/story" } },
        },
        CreatedAt = DateTime.UtcNow,
        ModelVersion = "test",
        Kind = kind,
        Version = 3,
        ExcludeUrlPatterns = excludeUrlPatterns ?? new List<string>(),
        UserLabels = labels ?? new List<UserLinkLabel>(),
    };

    private static List<LinkInfo> FourStories() => new()
    {
        Story("https://x.com/story/alpha", "Alpha headline about a long interesting thing"),
        Story("https://x.com/story/beta", "Beta headline about a long interesting thing"),
        Story("https://x.com/story/gamma", "Gamma headline about a long interesting thing"),
        Story("https://x.com/story/delta", "Delta headline about a long interesting thing"),
    };

    private static async Task<SetupWizard.LabelOutcome?> RunLabelMode(
        SiteHierarchyConfig config,
        List<LinkInfo> links,
        List<IReadOnlyList<string>>? capturedCards = null,
        params NavigationCommand[] commands)
    {
        var overlay = new SetupWizardOverlay.State();
        Task Render(CancellationToken _)
        {
            if (capturedCards != null && overlay.Card != null)
            {
                capturedCards.Add(SetupWizardOverlay.DescribeCard(overlay.Card));
            }

            return Task.CompletedTask;
        }

        return await SetupWizard.RunLabelModeAsync(
            Input(commands), Render, overlay, links, config, lens: null, CancellationToken.None);
    }

    // ---- bead 4: the label UI -------------------------------------------------

    [Fact]
    public async Task LabelKeys_AssignBadges_AndOutcomeCarriesRanksAndKinds()
    {
        var cards = new List<IReadOnlyList<string>>();
        var outcome = await RunLabelMode(
            ConfigWith(), FourStories(), cards,
            Key('a'), Cmd(CommandType.MoveDown), Key('a'),
            Cmd(CommandType.MoveDown), Key('x'),
            Cmd(CommandType.MoveDown), Key('m'),
            Cmd(CommandType.ActivateLink));

        outcome.Should().NotBeNull();
        var byUrl = outcome!.Labels.ToDictionary(l => l.Url);
        byUrl["https://x.com/story/alpha"].Kind.Should().Be(LinkLabelKind.Article);
        byUrl["https://x.com/story/alpha"].Rank.Should().Be(1);
        byUrl["https://x.com/story/beta"].Rank.Should().Be(2);
        byUrl["https://x.com/story/gamma"].Kind.Should().Be(LinkLabelKind.Ad);
        byUrl["https://x.com/story/delta"].Kind.Should().Be(LinkLabelKind.Menu);
        outcome.SeenUrls.Should().HaveCount(4);

        var allLines = cards.SelectMany(c => c).ToList();
        allLines.Should().Contain(l => l.Contains("[ 1]"), "the rank badge must render on the row");
        allLines.Should().Contain(l => l.Contains("[ad]"), "the ad badge must render on the row");
        allLines.Should().Contain(l => l.Contains("[menu]"));
    }

    [Fact]
    public async Task ArticleToggle_UnlabelsAndRecompactsRanks()
    {
        var outcome = await RunLabelMode(
            ConfigWith(), FourStories(), null,
            Key('a'), Cmd(CommandType.MoveDown), Key('a'),
            Cmd(CommandType.MoveUp), Key('a'), // toggles the first article OFF
            Cmd(CommandType.ActivateLink));

        outcome!.Labels.Should().ContainSingle();
        outcome.Labels[0].Url.Should().Be("https://x.com/story/beta");
        outcome.Labels[0].Rank.Should().Be(1, "removing rank-1 re-compacts the remaining article to 1");
    }

    [Fact]
    public async Task StartFocusUrl_ResumesOnThatRow()
    {
        // workspace-v2m8.2: entered from the preview, label mode resumes on the
        // row the user was focused on there — pressing 'a' immediately labels
        // GAMMA, not the top row.
        var outcome = await SetupWizard.RunLabelModeAsync(
            Input(Key('a'), Cmd(CommandType.ActivateLink)),
            _ => Task.CompletedTask,
            new SetupWizardOverlay.State(),
            FourStories(),
            ConfigWith(),
            lens: null,
            CancellationToken.None,
            startFocusUrl: "https://x.com/story/gamma");

        outcome.Should().NotBeNull();
        outcome!.Labels.Should().ContainSingle(l =>
            l.Kind == LinkLabelKind.Article && l.Url == "https://x.com/story/gamma");
    }

    [Fact]
    public async Task Escape_ReturnsNull()
    {
        var outcome = await RunLabelMode(ConfigWith(), FourStories(), null, Cmd(CommandType.GoBack));
        outcome.Should().BeNull();
    }

    [Fact]
    public async Task ClickOnLens_MovesCursorToThatRow_ThenLabelKeyLabelsIt()
    {
        // workspace-p2qo: a click on the docked page arrives as a poll hit on the
        // animation tick. The cursor jumps to the clicked story's row so the next
        // 'a' labels it — here GAMMA is clicked (not the top row), then labeled.
        var links = FourStories();
        var polled = false;
        var disarmed = false;
        var lens = new SetupWizard.Lens(
            HighlightCssAsync: (_, _) => Task.FromResult(0),
            ClearAsync: _ => Task.CompletedTask,
            ArmClickAsync: _ => Task.CompletedTask,
            PollClickAsync: _ =>
            {
                if (polled)
                {
                    return Task.FromResult<LinkInfo?>(null);
                }

                polled = true;
                return Task.FromResult<LinkInfo?>(links[2]); // GAMMA
            },
            DisarmClickAsync: _ =>
            {
                disarmed = true;
                return Task.CompletedTask;
            });

        var input = Substitute.For<IInputHandler>();
        input.AnimationController.AnimationState.Returns(new AnimationState());
        input.WaitForInputAsync(Arg.Any<CancellationToken>()).Returns(
            Cmd(CommandType.AnimationTick), // poll -> GAMMA -> cursor moves there
            Key('a'),                       // labels the clicked row
            Cmd(CommandType.ActivateLink)); // apply

        var outcome = await SetupWizard.RunLabelModeAsync(
            input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            links, ConfigWith(), lens, CancellationToken.None);

        outcome.Should().NotBeNull();
        outcome!.Labels.Should().ContainSingle(l =>
            l.Kind == LinkLabelKind.Article && l.Url == "https://x.com/story/gamma");
        disarmed.Should().BeTrue("the pick must be disarmed on exit so it stops swallowing clicks");
    }

    [Fact]
    public async Task ClickOnUnknownLink_SelectsNothing_LeavesCursorPut()
    {
        // A click on a link the extractor never saw must not jump the cursor to
        // a nonexistent row — the top row stays focused and 'a' labels IT.
        var links = FourStories();
        var polled = false;
        var lens = new SetupWizard.Lens(
            HighlightCssAsync: (_, _) => Task.FromResult(0),
            ClearAsync: _ => Task.CompletedTask,
            ArmClickAsync: _ => Task.CompletedTask,
            PollClickAsync: _ =>
            {
                if (polled)
                {
                    return Task.FromResult<LinkInfo?>(null);
                }

                polled = true;
                return Task.FromResult<LinkInfo?>(new LinkInfo
                {
                    Url = "https://x.com/not-on-this-page",
                    DisplayText = "Elsewhere",
                    Type = LinkType.Content,
                    ImportanceScore = 70,
                });
            },
            DisarmClickAsync: _ => Task.CompletedTask);

        var input = Substitute.For<IInputHandler>();
        input.AnimationController.AnimationState.Returns(new AnimationState());
        input.WaitForInputAsync(Arg.Any<CancellationToken>()).Returns(
            Cmd(CommandType.AnimationTick),
            Key('a'),
            Cmd(CommandType.ActivateLink));

        var outcome = await SetupWizard.RunLabelModeAsync(
            input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            links, ConfigWith(), lens, CancellationToken.None);

        outcome!.Labels.Should().ContainSingle(l => l.Url == "https://x.com/story/alpha",
            "an unknown click selected nothing, so 'a' labeled the still-focused top row");
    }

    [Fact]
    public async Task HeaderRow_LabelKey_ShowsNotice()
    {
        var cards = new List<IReadOnlyList<string>>();
        var outcome = await RunLabelMode(
            ConfigWith(), FourStories(), cards,
            Cmd(CommandType.GoToTop), Key('a'), Cmd(CommandType.GoBack));

        outcome.Should().BeNull();
        cards.SelectMany(c => c).Should().Contain(l => l.Contains("headers can't be labeled"));
    }

    [Fact]
    public async Task Tab_RevealsHiddenLinks_ForRescue()
    {
        var links = FourStories();
        links.Add(Story("https://x.com/story/hidden-promo", "Hidden promo headline that was excluded"));
        var config = ConfigWith(excludeUrlPatterns: new List<string> { "/hidden-promo" });

        var cards = new List<IReadOnlyList<string>>();
        var outcome = await RunLabelMode(
            config, links, cards,
            Cmd(CommandType.SwitchView), Cmd(CommandType.GoBack));

        outcome.Should().BeNull();
        var allLines = cards.SelectMany(c => c).ToList();
        allLines.Should().Contain(l => l.Contains("[hidden]") && l.Contains("Hidden promo"),
            "the all-links view must reveal excluded links so they can be rescued");
    }

    [Fact]
    public async Task SavedLedger_SeedsBadges_OnEntry()
    {
        var config = ConfigWith(labels: new List<UserLinkLabel>
        {
            new() { Url = "https://x.com/story/beta", Kind = LinkLabelKind.Ad, LabeledAt = DateTime.UtcNow },
        });

        var cards = new List<IReadOnlyList<string>>();
        await RunLabelMode(config, FourStories(), cards, Cmd(CommandType.GoBack));

        cards[0].Should().Contain(l => l.Contains("[ad]") && l.Contains("Beta"),
            "a label saved in an earlier session must badge immediately");
    }

    // ---- bead 5: deterministic derivation + self-test -------------------------

    private static List<LinkInfo> RiverPage() => new()
    {
        Story("https://x.com/story/alpha", "Alpha headline about a long interesting thing", "div.itc > div.ii a"),
        Story("https://x.com/story/beta", "Beta headline about a long interesting thing", "div.itc > div.ii a"),
        Story("https://x.com/story/gamma", "Gamma headline about a long interesting thing", "div.itc > div.ii a"),
        Story("https://x.com/nav-short", "Nav", "div.chrome a"),
    };

    private static UserLinkLabel Label(string url, LinkLabelKind kind, int? rank = null) => new()
    {
        Url = url,
        Kind = kind,
        Rank = rank,
        LabeledAt = DateTime.UtcNow,
    };

    [Fact]
    public void DeriveConfig_RankedArticles_BuildOneGeneralizingRiver()
    {
        var links = RiverPage();
        var current = ConfigWith(sections: new List<HierarchySection>());
        var labels = new List<UserLinkLabel>
        {
            Label("https://x.com/story/beta", LinkLabelKind.Article, 1),
            Label("https://x.com/story/alpha", LinkLabelKind.Article, 2),
        };

        var derived = LabelDerivation.DeriveConfig(current, labels, links.Select(l => l.Url).ToList(), links);

        derived.Should().NotBeNull();
        derived!.Sections.Should().ContainSingle("both articles live in the same river");
        derived.Sections[0].Name.Should().Be("Stories");
        derived.Sections[0].ParentSelectors.Should().NotBeEmpty("the river generalizes by selector, not by URL");
        derived.Kind.Should().Be(LayoutKind.AiCurated);
        derived.UserLabels.Should().HaveCount(2);
        LabelDerivation.LabelsReproducedFailures(derived, labels, links).Should().BeEmpty();

        // The gamma story (unlabeled) is captured by the same river — the pattern
        // generalizes beyond the labeled examples.
        NavigationTreeBuilder.MatchesSection(links[2], derived.Sections[0]).Should().BeTrue();
    }

    [Fact]
    public void DeriveConfig_TwoContainers_SectionsOrderByLabeledRank()
    {
        var links = new List<LinkInfo>
        {
            Story("https://x.com/a/one", "First cluster headline that is long enough", "div.aaa.up a"),
            Story("https://x.com/a/two", "Second cluster headline that is long enough", "div.aaa.up a"),
            Story("https://x.com/b/one", "Other cluster headline that is long enough", "div.bbb.down a"),
            Story("https://x.com/b/two", "Other cluster second headline long enough", "div.bbb.down a"),
        };
        var labels = new List<UserLinkLabel>
        {
            Label("https://x.com/b/one", LinkLabelKind.Article, 1),
            Label("https://x.com/a/one", LinkLabelKind.Article, 2),
        };

        var derived = LabelDerivation.DeriveConfig(
            ConfigWith(sections: new List<HierarchySection>()), labels, links.Select(l => l.Url).ToList(), links);

        derived!.Sections.Should().HaveCount(2);
        NavigationTreeBuilder.MatchesSection(links[2], derived.Sections[0]).Should().BeTrue(
            "the rank-1 article's river leads");
        NavigationTreeBuilder.MatchesSection(links[0], derived.Sections[1]).Should().BeTrue();
        derived.Sections[0].SortOrder.Should().BeLessThan(derived.Sections[1].SortOrder);
    }

    [Fact]
    public void DeriveConfig_AdUnderRailHeading_ExcludesWholeHeading()
    {
        var links = RiverPage();
        links.Add(Story("https://sponsor.example/pitch", "Sponsored pitch headline long enough here", "div.sp a", sectionTitle: "Sponsor Posts"));
        var labels = new List<UserLinkLabel> { Label("https://sponsor.example/pitch", LinkLabelKind.Ad) };

        var derived = LabelDerivation.DeriveConfig(ConfigWith(), labels, links.Select(l => l.Url).ToList(), links);

        derived!.ExcludeSectionTitles.Should().Contain("Sponsor Posts");
        LabelDerivation.LabelsReproducedFailures(derived, labels, links).Should().BeEmpty();
    }

    [Fact]
    public void AppendExactExclude_RefusesWhenPatternWouldHideOtherLinks()
    {
        var links = RiverPage();
        var prefixAd = Story("https://x.com/story", "Prefix ad headline long enough to be story-shaped", "div.sp a");
        links.Add(prefixAd);

        var refused = LabelDerivation.AppendExactExclude(ConfigWith(), prefixAd, links);
        refused.ExcludeUrlPatterns.Should().BeEmpty(
            "x.com/story is a substring of every story URL — the rule would nuke them");

        var distinctAd = Story("https://x.com/only-me", "Distinct ad headline long enough here", "div.sp a");
        links.Add(distinctAd);
        var accepted = LabelDerivation.AppendExactExclude(ConfigWith(), distinctAd, links);
        accepted.ExcludeUrlPatterns.Should().Contain("x.com/only-me");
    }

    [Fact]
    public void DeriveConfig_MenuLabels_RouteToMoreRules()
    {
        var links = RiverPage();
        links.Add(Story("https://x.com/about-us-page", "About", "nav.menu.top a"));
        var labels = new List<UserLinkLabel> { Label("https://x.com/about-us-page", LinkLabelKind.Menu) };

        var derived = LabelDerivation.DeriveConfig(ConfigWith(), labels, links.Select(l => l.Url).ToList(), links);

        NavigationTreeBuilder.MatchesMore(links[^1], derived!).Should().BeTrue();
        LabelDerivation.LabelsReproducedFailures(derived!, labels, links).Should().BeEmpty();
    }

    [Fact]
    public void DeriveConfig_ArticleProtection_DropsConflictingExcludeRule()
    {
        var links = RiverPage();
        var current = ConfigWith() with { ExcludeSelectors = new List<string> { "div.ii" } };
        var labels = new List<UserLinkLabel> { Label("https://x.com/story/alpha", LinkLabelKind.Article, 1) };

        var derived = LabelDerivation.DeriveConfig(current, labels, links.Select(l => l.Url).ToList(), links);

        derived!.ExcludeSelectors.Should().NotContain("div.ii",
            "no exclude rule may hide a labeled article");
        LabelDerivation.LabelsReproducedFailures(derived, labels, links).Should().BeEmpty();
    }

    [Fact]
    public void LabelsReproducedFailures_ReportEachViolationKind()
    {
        var links = RiverPage();
        var labels = new List<UserLinkLabel>
        {
            Label("https://x.com/story/alpha", LinkLabelKind.Article, 1),
            Label("https://x.com/story/beta", LinkLabelKind.Ad),
            Label("https://x.com/story/gamma", LinkLabelKind.Menu),
        };

        // A config that violates all three: excludes the article, keeps the ad,
        // leaves the menu link in the flow.
        var bad = ConfigWith() with { ExcludeUrlPatterns = new List<string> { "/story/alpha" } };

        var failures = LabelDerivation.LabelsReproducedFailures(bad, labels, links);

        failures.Should().Contain(f => f.Contains("hidden by an exclude rule"));
        failures.Should().Contain(f => f.Contains("still visible"));
        failures.Should().Contain(f => f.Contains("not routed to the More menu"));
    }

    // ---- bead 6: AI fallback ---------------------------------------------------

    [Fact]
    public void ApplyRuleLabels_Ignore_HidesExactlyTheLabeledLink()
    {
        // workspace-nbvb.1: 'i' means THIS link. Two chrome links share a
        // distinctive container class — before the exact-first fix the token
        // route hid BOTH (class extrapolation is the Ad label's job, not hide's).
        LinkInfo Nav(string url, string text) => new()
        {
            Url = url,
            DisplayText = text,
            Type = LinkType.Content,
            ImportanceScore = 40,
            ParentSelector = "div.rail > span.src",
        };
        var links = FourStories();
        links.Add(Nav("https://x.com/nav/one", "Nav one"));
        links.Add(Nav("https://x.com/nav/two", "Nav two"));

        var result = LabelDerivation.ApplyRuleLabels(
            ConfigWith(), new[] { Label("https://x.com/nav/one", LinkLabelKind.Ignore) }, links);

        NavigationTreeBuilder.IsExcluded(links[4], result).Should().BeTrue("the labeled link hides");
        NavigationTreeBuilder.IsExcluded(links[5], result).Should().BeFalse(
            "its container-sibling survives — the exact rule comes first for hide");
    }

    // ---- workspace-nbvb.4: rename ledger ----

    [Fact]
    public void AppendSectionRename_RenamesNow_AndLatestWinsOnTheLedger()
    {
        var section = new HierarchySection
        {
            Name = "Stories",
            SortOrder = 0,
            ParentSelectors = new List<string> { "div.river" },
        };
        var config = ConfigWith(sections: new List<HierarchySection> { section });

        var once = LabelDerivation.AppendSectionRename(config, section, "Tech Talk");
        once.Sections[0].Name.Should().Be("Tech Talk");
        once.UserSectionNames.Should().ContainSingle(r => r.Name == "Tech Talk");

        var twice = LabelDerivation.AppendSectionRename(once, once.Sections[0], "Front Page");
        twice.Sections[0].Name.Should().Be("Front Page");
        twice.UserSectionNames.Should().ContainSingle(r => r.Name == "Front Page",
            "renaming the same identifiers again replaces the entry");
    }

    [Fact]
    public void CarryAndEnforce_ReappliesRename_OverAModelFreshSectionList()
    {
        var prior = ConfigWith(sections: new List<HierarchySection>
        {
            new() { Name = "Tech Talk", SortOrder = 0, ParentSelectors = new List<string> { "div.river" } },
        }) with
        {
            UserSectionNames = new List<UserSectionRename>
            {
                new() { Identifiers = new List<string> { "div.river" }, Name = "Tech Talk", RenamedAt = DateTime.UtcNow },
            },
        };

        // The model round rebuilt the section and called it "Main feed".
        var fresh = ConfigWith(sections: new List<HierarchySection>
        {
            new() { Name = "Main feed", SortOrder = 0, ParentSelectors = new List<string> { "div.river" } },
        });

        var enforced = LabelDerivation.CarryAndEnforce(fresh, prior, FourStories());

        enforced.Sections[0].Name.Should().Be("Tech Talk", "the rename ledger wins over the model's name");
    }

    [Fact]
    public void ApplyRuleLabels_DisobedientModelConfig_StillExcludesLabeledAd()
    {
        var links = RiverPage();
        links.Add(Story("https://sponsor.example/pitch", "Sponsored pitch headline long enough here", "div.sp a", sectionTitle: "Sponsor Posts"));
        var labels = new List<UserLinkLabel> { Label("https://sponsor.example/pitch", LinkLabelKind.Ad) };

        // The "model result": keeps everything, excludes nothing.
        var modelConfig = ConfigWith(sections: new List<HierarchySection>
        {
            new() { Name = "Everything", SortOrder = 0, UrlPatterns = new List<string> { "http" } },
        });

        var enforced = LabelDerivation.ApplyRuleLabels(modelConfig, labels, links);

        NavigationTreeBuilder.IsExcluded(links[^1], enforced).Should().BeTrue(
            "label rules are re-applied in code — a disobedient model cannot resurrect a labeled ad");
    }

    [Fact]
    public async Task LabelAdjust_DeterministicMiss_SpendsOneFallbackCall()
    {
        // An ad whose ONLY routes all fail: its heading is shared with a labeled
        // article (protection drops the heading rule), its parent token covers the
        // article (unsafe), its URL prefixes the article's (exact-path refused).
        var article = Story(
            "https://x.com/story/alpha-long-slug", "Real story headline that is long enough", "div.sp a", sectionTitle: "Sponsor Posts");
        var ad = Story(
            "https://x.com/story", "Camouflaged ad headline that is long enough", "div.sp a", sectionTitle: "Sponsor Posts");
        var links = new List<LinkInfo> { article, ad };
        var seeded = ConfigWith(sections: new List<HierarchySection>
        {
            new() { Name = "Headlines", SortOrder = 0, UrlPatterns = new List<string> { "/story" } },
        });

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.InferPatternFromLabelsAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<UserLinkLabel>>(), Arg.Any<SiteHierarchyConfig>(), Arg.Any<CancellationToken>())
            .Returns(new InferredPattern { Config = seeded });

        // Seeded preview → Space → Enter ("Fix links by hand", option 0) →
        // 'a' on the article row, Down, 'x' on the ad row → Enter (apply) →
        // deterministic self-test misses the ad → ONE fallback call → preview →
        // Esc discards.
        var input = Input(
            Cmd(CommandType.ToggleSelection),
            Cmd(CommandType.ActivateLink),
            Key('a'), Cmd(CommandType.MoveDown), Key('x'),
            Cmd(CommandType.ActivateLink),
            Cmd(CommandType.GoBack));

        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            links, "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            applyPreview: null,
            lens: null,
            CancellationToken.None,
            existingConfig: seeded);

        result.Cancelled.Should().BeTrue("the script discards at the end");
        budget.Used.Should().Be(1, "the label fallback is exactly one budget-guarded call");
        await analyzer.Received(1).InferPatternFromLabelsAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<UserLinkLabel>>(l => l.Count == 2),
            Arg.Any<SiteHierarchyConfig>(), Arg.Any<CancellationToken>());
    }

    // ---- bead 8: refine respects labels + instruction log ----------------------

    [Fact]
    public async Task Refine_DisobedientModel_LabeledAdStaysExcluded_AndInstructionLogged()
    {
        var ad = Story("https://sponsor.example/pitch", "Sponsored pitch headline long enough here", "div.sp a", sectionTitle: "Sponsor Posts");
        var links = RiverPage();
        links.Add(ad);
        var seeded = ConfigWith(
            sections: new List<HierarchySection>
            {
                new() { Name = "Headlines", SortOrder = 0, UrlPatterns = new List<string> { "/story" } },
            },
            labels: new List<UserLinkLabel> { Label(ad.Url, LinkLabelKind.Ad) })
            with
        { ExcludeSectionTitles = new List<string> { "Sponsor Posts" } };

        // The "model" forgets every exclude when applying the tweak.
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.RefineLayoutAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteHierarchyConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new InferredPattern
            {
                Config = ConfigWith(sections: new List<HierarchySection>
                {
                    new() { Name = "Headlines", SortOrder = 0, UrlPatterns = new List<string> { "/story" } },
                }),
            });

        // Seeded preview → Space → Down past "Mark links…" AND the generalize
        // row (the config carries a label, workspace-nbvb.2) → Enter on
        // "Tell the AI what to change…" → refine → 's' saves.
        var input = Input(
            Cmd(CommandType.ToggleSelection),
            Cmd(CommandType.MoveDown),
            Cmd(CommandType.MoveDown),
            Cmd(CommandType.ActivateLink),
            new NavigationCommand { Type = CommandType.SaveToCollection, RawKeyChar = 's' });

        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            links, "https://x.com/", null, new ModelRoundTripBudget(),
            freeTextPrompt: _ => Task.FromResult<string?>("hide the podcast links"),
            applyPreview: null,
            lens: null,
            CancellationToken.None,
            existingConfig: seeded);

        result.Config.Should().NotBeNull();
        NavigationTreeBuilder.IsExcluded(ad, result.Config!).Should().BeTrue(
            "the ledger's ad label is re-applied in code after every refine");
        result.Config!.UserLabels.Should().ContainSingle(l => l.Kind == LinkLabelKind.Ad);
        result.Config.UserInstructions.Should().Contain("hide the podcast links",
            "an applied instruction joins the standing log");
    }

    [Fact]
    public void AppendInstruction_CapsAtMax()
    {
        var config = ConfigWith();
        for (var i = 0; i < SiteHierarchyConfig.MaxUserInstructions + 5; i++)
        {
            config = LabelDerivation.AppendInstruction(config, $"instruction {i}");
        }

        config.UserInstructions.Should().HaveCount(SiteHierarchyConfig.MaxUserInstructions);
        config.UserInstructions[^1].Should().Be($"instruction {SiteHierarchyConfig.MaxUserInstructions + 4}",
            "the newest instructions survive the cap");
    }

    [Fact]
    public async Task RefinePrompt_CarriesGroundTruthLabels_AndInstructionLog()
    {
        var config = new OpenAiHierarchyConfiguration
        {
            Model = "gpt-5-mini",
            ReasoningEffort = "minimal",
            MaxTokens = 4096,
        };
        var settings = Substitute.For<IUserSettingsStore>();
        settings.Get("OpenAiApiKey").Returns("sk-test");

        string? capturedUser = null;
        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, messages, _, _) =>
        {
            capturedUser = messages.OfType<OpenAI.Chat.UserChatMessage>().First()
                .Content.First(p => p.Text != null).Text;
            return Task.FromResult(
                "{\"sections\":[{\"name\":\"Stories\",\"story_indices\":[0,1],\"start_collapsed\":false}]," +
                "\"exclude_indices\":[],\"confidence\":0.9,\"confirm_question\":null}");
        };

        var analyzer = new OpenAiHierarchyAnalyzer(
            Options.Create(config),
            Options.Create(new OpenAiTtsConfiguration()),
            settings,
            Substitute.For<ILogger<OpenAiHierarchyAnalyzer>>(),
            completer);

        var links = RiverPage();
        var current = ConfigWith(labels: new List<UserLinkLabel>
        {
            Label("https://x.com/story/beta", LinkLabelKind.Article, 1),
            Label("https://x.com/story/gamma", LinkLabelKind.Ad),
        }) with
        { UserInstructions = new List<string> { "merge the columns" } };

        await analyzer.RefineLayoutAsync(null, links, "https://x.com/", current, "hide the events rail");

        capturedUser.Should().Contain("USER-LABELED GROUND TRUTH");
        capturedUser.Should().Contain("articles, in required order: [1]");
        capturedUser.Should().Contain("ads/hidden — keep excluded: [2]");
        capturedUser.Should().Contain("EARLIER USER INSTRUCTIONS");
        capturedUser.Should().Contain("merge the columns");
        capturedUser.Should().Contain("hide the events rail");
    }

    [Fact]
    public async Task StartWithLabelMode_LabelsToPreview_ZeroModelCalls()
    {
        // workspace-t1ok.7: the "Mark the links yourself" entry — label mode
        // runs FIRST over a flat seed, the derived config previews, Enter
        // saves; the analyzer is never called.
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        var links = RiverPage();

        // Flat-seed label view lists story-shaped articles; label the first
        // one as rank-1, apply, then Enter saves the preview.
        var input = Input(
            Key('a'),
            Cmd(CommandType.ActivateLink),                                    // apply labels
            new NavigationCommand { Type = CommandType.SaveToCollection, RawKeyChar = 's' });  // save the preview

        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            links, "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            applyPreview: null,
            lens: null,
            CancellationToken.None,
            startWithLabelMode: true);

        result.Config.Should().NotBeNull();
        result.Config!.Sections.Should().NotBeEmpty("the labeled article derived a river section");
        result.Config.UserLabels.Should().ContainSingle(l => l.Kind == LinkLabelKind.Article && l.Rank == 1);
        budget.Used.Should().Be(0, "hand-labeling costs no model calls");
        await analyzer.DidNotReceiveWithAnyArgs().InferPatternFromAnswersAsync(
            default, default!, default!, default!, default!, default);
        await analyzer.DidNotReceiveWithAnyArgs().ProposeSetupQuestionsAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task InferPatternFromLabels_PromptCarriesGroundTruthIndices()
    {
        var config = new OpenAiHierarchyConfiguration
        {
            Model = "gpt-5-mini",
            ReasoningEffort = "minimal",
            MaxTokens = 4096,
        };
        var settings = Substitute.For<IUserSettingsStore>();
        settings.Get("OpenAiApiKey").Returns("sk-test");

        string? capturedSystem = null;
        string? capturedUser = null;
        OpenAiHierarchyAnalyzer.ChatCompleter completer = (_, _, messages, _, _) =>
        {
            capturedSystem = messages.OfType<OpenAI.Chat.SystemChatMessage>().First().Content[0].Text;
            capturedUser = messages.OfType<OpenAI.Chat.UserChatMessage>().First()
                .Content.First(p => p.Text != null).Text;
            return Task.FromResult(
                "{\"sections\":[{\"name\":\"Stories\",\"story_indices\":[0,1],\"start_collapsed\":false}]," +
                "\"exclude_indices\":[2],\"confidence\":0.9,\"confirm_question\":null}");
        };

        var analyzer = new OpenAiHierarchyAnalyzer(
            Options.Create(config),
            Options.Create(new OpenAiTtsConfiguration()),
            settings,
            Substitute.For<ILogger<OpenAiHierarchyAnalyzer>>(),
            completer);

        var links = RiverPage();
        var labels = new List<UserLinkLabel>
        {
            Label("https://x.com/story/beta", LinkLabelKind.Article, 1),
            Label("https://x.com/story/alpha", LinkLabelKind.Article, 2),
            Label("https://x.com/story/gamma", LinkLabelKind.Ad),
            Label("https://x.com/nav-short", LinkLabelKind.Menu),
        };

        var result = await analyzer.InferPatternFromLabelsAsync(
            null, links, "https://x.com/", labels, ConfigWith(sections: new List<HierarchySection>()));

        result.Config.Should().NotBeNull();
        capturedSystem.Should().Contain("GROUND TRUTH");
        capturedUser.Should().Contain("ARTICLES in required order: [1, 0]",
            "label ranks resolve to link indices in rank order");
        capturedUser.Should().Contain("ADS — must be in exclude_indices: [2]");
        capturedUser.Should().Contain("MENU links — the app routes these");
    }
}
