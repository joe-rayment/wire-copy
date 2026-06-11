// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI.Components;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-6yb7.3 — preview-first wizard flow: round 1 propose, clarifying
/// questions, round 2 infer, then a live preview the user saves (Enter),
/// adjusts (Space), or discards (Esc). 'Looks good' / 'Use plain document
/// order' no longer exist anywhere in the flow.
/// </summary>
[Trait("Category", "Unit")]
public class SetupWizardTests
{
    private static SiteSetupProposal ProposalWith(int questionCount)
    {
        var questions = Enumerable.Range(0, questionCount).Select(i => new SetupQuestion
        {
            Id = $"q{i}",
            Prompt = $"Question {i}?",
            Kind = SetupQuestionKind.PickMain,
            DefaultAnswer = "Opt",
            Options = new List<SetupOption>
            {
                new() { Label = "Opt", ParentSelector = "section.feed" },
            },
        }).ToList();

        return new SiteSetupProposal
        {
            ProposedPattern = new ProposedPattern
            {
                TopStory = new SetupOption { Label = "Lead story", ParentSelector = "section.lead", UrlPattern = "/politics/" },
                Tiers = new List<SetupOption> { new() { Label = "Feed", ParentSelector = "section.feed" } },
                Exclude = new List<SetupOption> { new() { Label = "Ads", ParentSelector = "aside.promo" } },
            },
            Questions = questions,
        };
    }

    private static SiteHierarchyConfig SomeConfig() => new()
    {
        Domain = "x.com",
        UrlPattern = "^x$",
        Sections = new List<HierarchySection> { new() { Name = "Top Story", SortOrder = 0, ParentSelectors = new List<string> { "section.lead" } } },
        CreatedAt = DateTime.UtcNow,
        ModelVersion = "gpt-5-mini",
        Kind = LayoutKind.AiCurated,
        Version = 3,
    };

    private static List<LinkInfo> Links() => new()
    {
        new LinkInfo { Url = "https://x.com/a", DisplayText = "A", Type = LinkType.Content, ImportanceScore = 80, ParentSelector = "section.lead a" },
    };

    private static IHierarchyAnalyzer AnalyzerReturning(SiteSetupProposal proposal, SiteHierarchyConfig config)
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(proposal);
        analyzer.InferPatternFromAnswersAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(config);
        return analyzer;
    }

    private static IInputHandler InputSequence(params CommandType[] commands)
    {
        var input = Substitute.For<IInputHandler>();
        if (commands.Length == 1)
        {
            input.WaitForInputAsync(Arg.Any<CancellationToken>())
                .Returns(new NavigationCommand { Type = commands[0] });
        }
        else
        {
            input.WaitForInputAsync(Arg.Any<CancellationToken>())
                .Returns(
                    new NavigationCommand { Type = commands[0] },
                    commands.Skip(1).Select(c => new NavigationCommand { Type = c }).ToArray());
        }

        return input;
    }

    [Fact]
    public async Task AcceptAllPath_ClampsQuestions_ExactlyTwoRoundTrips_ShowsIdentifier()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProposalWith(questionCount: 5));

        IReadOnlyList<SetupAnswer>? capturedAnswers = null;
        analyzer.InferPatternFromAnswersAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { capturedAnswers = ci.Arg<IReadOnlyList<SetupAnswer>>(); return SomeConfig(); });

        var input = InputSequence(CommandType.ActivateLink); // Enter every card

        var overlay = new SetupWizardOverlay.State();
        var cards = new List<IReadOnlyList<string>>();
        Task Render(CancellationToken _)
        {
            if (overlay.Card != null)
            {
                cards.Add(SetupWizardOverlay.DescribeCard(overlay.Card));
            }

            return Task.CompletedTask;
        }

        var budget = new ModelRoundTripBudget();

        var result = await SetupWizard.RunAsync(
            analyzer, input, Render, overlay, Links(), "https://x.com/", screenshot: null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null,
            applyPreview: null,
            lens: null,
            CancellationToken.None);

        result.Config.Should().NotBeNull();
        budget.Used.Should().Be(2, "accept-all spends exactly propose + infer");
        capturedAnswers!.Count.Should().BeLessThanOrEqualTo(SetupWizard.MaxStructuredQuestions,
            "structured questions are clamped to at most 3 (5 proposed)");

        // Identifier transparency: the preview card shows the durable selector
        // that will be saved.
        cards.SelectMany(c => c).Should().Contain(l => l.Contains("section.lead", StringComparison.Ordinal));

        // The removed confirmation-theater options must not resurface.
        cards.SelectMany(c => c).Should().NotContain(l => l.Contains("Looks good", StringComparison.OrdinalIgnoreCase));
        cards.SelectMany(c => c).Should().NotContain(l => l.Contains("document order", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ObviousPattern_ZeroQuestions_GoesStraightToPreview()
    {
        var analyzer = AnalyzerReturning(ProposalWith(questionCount: 0), SomeConfig());
        var input = InputSequence(CommandType.ActivateLink); // Enter on the preview saves

        var overlay = new SetupWizardOverlay.State();
        var titles = new List<string>();
        Task Render(CancellationToken _)
        {
            if (overlay.Card != null && overlay.Mode == SetupWizardOverlay.Mode.Card)
            {
                titles.Add(overlay.Card.Title);
            }

            return Task.CompletedTask;
        }

        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, Render, overlay, Links(), "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        result.Config.Should().NotBeNull();
        budget.Used.Should().Be(2);
        titles.Should().OnlyContain(t => t == "Your new layout",
            "with no questions the ONLY interactive surface is the preview itself");
    }

    [Fact]
    public async Task Preview_AppliesCandidateTreeBeforeAskingToSave()
    {
        var analyzer = AnalyzerReturning(ProposalWith(questionCount: 0), SomeConfig());
        var input = InputSequence(CommandType.ActivateLink);

        var previewed = new List<SiteHierarchyConfig>();
        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            Links(), "https://x.com/", null, new ModelRoundTripBudget(),
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null,
            applyPreview: (config, _) => { previewed.Add(config); return Task.CompletedTask; },
            lens: null,
            CancellationToken.None);

        result.Config.Should().NotBeNull();
        previewed.Should().ContainSingle()
            .Which.Should().BeSameAs(result.Config, "Enter saves exactly what was previewed");
    }

    [Fact]
    public async Task Preview_Esc_CancelsWithoutSaving()
    {
        var analyzer = AnalyzerReturning(ProposalWith(questionCount: 0), SomeConfig());
        var input = InputSequence(CommandType.GoBack); // Esc on the preview

        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            Links(), "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        result.Cancelled.Should().BeTrue("Esc on the preview must not save the config");
        result.Config.Should().BeNull();
        budget.Used.Should().Be(2);
    }

    [Fact]
    public async Task BudgetExhausted_BeforePropose_CancelsGracefully()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        var input = Substitute.For<IInputHandler>();
        var spent = new ModelRoundTripBudget(max: 0);

        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            Links(), "https://x.com/", null, spent, _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        result.Cancelled.Should().BeTrue();
        await analyzer.DidNotReceive().ProposeSetupQuestionsAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- workspace-5oe9.9: point-at-a-link pick mode (now inside the adjust loop) ----

    [Fact]
    public void SectionFromPickedLink_ContentNode_YieldsSectionWithItsIdentifier()
    {
        var link = new LinkInfo
        {
            Url = "https://x.com/2026/05/30/headline",
            DisplayText = "Headline",
            Type = LinkType.Content,
            ImportanceScore = 90,
            ParentSelector = "main section.lead > h1 > a",
        };

        var section = SetupWizard.SectionFromPickedLink(link, "Top Story");

        section.Should().NotBeNull();
        section!.ParentSelectors.Should().Contain(s => s.Contains("section.lead", StringComparison.Ordinal));
    }

    [Fact]
    public void SectionFromPickedLink_HeaderNode_IsRejected()
    {
        var header = LinkInfo.CreateSubSectionHeader("Top Story", LinkType.Content);
        SetupWizard.SectionFromPickedLink(header, "Top Story").Should().BeNull();
    }

    [Fact]
    public async Task Adjust_PickLead_PerformsExactlyOneExtraRoundTrip()
    {
        var analyzer = AnalyzerReturning(ProposalWith(questionCount: 0), SomeConfig());

        // Preview: Space (adjust) → adjust card Enter on "Point at the main
        // story" → re-infer → preview again → Enter saves.
        var input = InputSequence(
            CommandType.ToggleSelection,
            CommandType.ActivateLink,
            CommandType.ActivateLink);

        var pickedLink = new LinkInfo
        {
            Url = "https://x.com/2026/05/30/the-real-lead",
            DisplayText = "The real lead",
            Type = LinkType.Content,
            ImportanceScore = 99,
            ParentSelector = "main section.hero a",
        };

        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            Links(), "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: _ => Task.FromResult<LinkInfo?>(pickedLink),
            applyPreview: null,
            lens: null,
            CancellationToken.None);

        result.Config.Should().NotBeNull();
        budget.Used.Should().Be(3, "propose + infer + one re-inference for the pick");
        await analyzer.Received(2).InferPatternFromAnswersAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
            Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adjust_HeaderPick_DoesNotReInfer_KeepsBaseConfig()
    {
        var analyzer = AnalyzerReturning(ProposalWith(questionCount: 0), SomeConfig());

        var input = InputSequence(
            CommandType.ToggleSelection,
            CommandType.ActivateLink,
            CommandType.ActivateLink);

        var header = LinkInfo.CreateSubSectionHeader("Top Story", LinkType.Content);
        var budget = new ModelRoundTripBudget();

        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            Links(), "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: _ => Task.FromResult<LinkInfo?>(header),
            applyPreview: null,
            lens: null,
            CancellationToken.None);

        result.Config.Should().NotBeNull("a rejected pick keeps the base config");
        budget.Used.Should().Be(2, "a header pick is rejected before any re-inference");
        await analyzer.Received(1).InferPatternFromAnswersAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
            Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adjust_FreeText_ReachesRoundTwo_AsAdjustmentAnswer()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProposalWith(questionCount: 0));

        var allAnswers = new List<IReadOnlyList<SetupAnswer>>();
        analyzer.InferPatternFromAnswersAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                allAnswers.Add(ci.Arg<IReadOnlyList<SetupAnswer>>().ToList());
                return SomeConfig();
            });

        // Preview: Space → adjust card: no pick wired, so cursor 0 is the
        // free-text option → Enter → free text → re-infer → preview → Enter.
        var input = InputSequence(
            CommandType.ToggleSelection,
            CommandType.ActivateLink,
            CommandType.ActivateLink);

        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            Links(), "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>("hide the opinion pieces"),
            pickLeadFromTree: null,
            applyPreview: null,
            lens: null,
            CancellationToken.None);

        result.Config.Should().NotBeNull();
        budget.Used.Should().Be(3, "the free-text adjustment costs exactly one re-inference");
        allAnswers.Should().HaveCount(2);
        allAnswers[1].Should().Contain(a => a.QuestionId == "adjustment" && a.Answer == "hide the opinion pieces");
    }

    [Fact]
    public async Task Adjust_Esc_ReturnsToPreviewWithoutSpendingBudget()
    {
        var analyzer = AnalyzerReturning(ProposalWith(questionCount: 0), SomeConfig());

        // Preview: Space → adjust card: Esc → back on preview: Enter saves.
        var input = InputSequence(
            CommandType.ToggleSelection,
            CommandType.GoBack,
            CommandType.ActivateLink);

        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            Links(), "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        result.Config.Should().NotBeNull();
        budget.Used.Should().Be(2, "backing out of the adjust card costs nothing");
    }

    // ---- workspace-wylw: live lens confirmation ----

    [Fact]
    public void CssForIdentifier_BuildsDescendantAndHrefSelectors()
    {
        SetupWizard.CssForIdentifier("section.lead", "/politics/")
            .Should().Be("section.lead a[href], a[href*=\"/politics/\"]");
        SetupWizard.CssForIdentifier("section.feed", string.Empty)
            .Should().Be("section.feed a[href]");
        SetupWizard.CssForIdentifier(string.Empty, "/a\"b/")
            .Should().Be("a[href*=\"/a\\\"b/\"]", "quotes are escaped for the attribute selector");
        SetupWizard.CssForIdentifier(string.Empty, string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void CssForSection_JoinsAllIdentifiers()
    {
        var section = new HierarchySection
        {
            Name = "Top",
            SortOrder = 0,
            ParentSelectors = new List<string> { "section.lead", ".hero" },
            UrlPatterns = new List<string> { "/politics/" },
        };

        SetupWizard.CssForSection(section)
            .Should().Be("section.lead a[href], .hero a[href], a[href*=\"/politics/\"]");
    }

    [Fact]
    public void BuildPreviewCard_ShowsRealMatchCounts_AndCoverage()
    {
        var config = SomeConfig(); // one section matching "section.lead"
        var links = new List<LinkInfo>
        {
            new() { Url = "https://x.com/a", DisplayText = "A", Type = LinkType.Content, ImportanceScore = 80, ParentSelector = "section.lead a" },
            new() { Url = "https://x.com/b", DisplayText = "B", Type = LinkType.Content, ImportanceScore = 60, ParentSelector = "aside.promo a" },
        };

        var card = SetupWizard.BuildPreviewCard(config, links);

        card.Options.Should().HaveCount(1);
        card.Options[0].Label.Should().Contain("1 link(s)");
        card.Options[0].HighlightSelector.Should().Be("section.lead a[href]");
        card.Footnote.Should().Be("1 of 2 story links covered");
    }

    [Fact]
    public void BuildPreviewCard_ZeroMatches_WarnsBeforeSaving()
    {
        var config = SomeConfig();
        var links = new List<LinkInfo>
        {
            new() { Url = "https://x.com/b", DisplayText = "B", Type = LinkType.Content, ImportanceScore = 60, ParentSelector = "aside.promo a" },
        };

        var card = SetupWizard.BuildPreviewCard(config, links);

        card.Footnote.Should().StartWith("⚠");
    }

    [Fact]
    public async Task QuestionAndPreviewCards_HighlightFocusedOptionOnLens()
    {
        var analyzer = AnalyzerReturning(ProposalWith(questionCount: 1), SomeConfig());
        var input = InputSequence(CommandType.ActivateLink);

        var highlighted = new List<string>();
        var cleared = 0;
        var lens = new SetupWizard.Lens(
            (css, _) => { highlighted.Add(css); return Task.FromResult(3); },
            _ => { cleared++; return Task.CompletedTask; });

        var overlay = new SetupWizardOverlay.State();
        var cards = new List<IReadOnlyList<string>>();
        Task Render(CancellationToken _)
        {
            if (overlay.Card != null && overlay.Mode == SetupWizardOverlay.Mode.Card)
            {
                cards.Add(SetupWizardOverlay.DescribeCard(overlay.Card));
            }

            return Task.CompletedTask;
        }

        var result = await SetupWizard.RunAsync(
            analyzer, input, Render, overlay, Links(), "https://x.com/", null, new ModelRoundTripBudget(),
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null,
            applyPreview: null,
            lens: lens,
            CancellationToken.None);

        result.Config.Should().NotBeNull();

        // The question card focused its first option → its links lit up.
        highlighted.Should().Contain("section.feed a[href]");

        // The preview card lit the saved section's links and was rendered with
        // its real match count before Enter saved.
        highlighted.Should().Contain("section.lead a[href]");
        cards.SelectMany(c => c).Should().Contain(l => l.Contains("Top Story — 1 link(s)", StringComparison.Ordinal));
        cleared.Should().BeGreaterThan(0, "highlights are cleared after the preview step");
    }
}
