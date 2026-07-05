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
                new() { Label = "Alt", ParentSelector = "section.alt" },
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
            .Returns(new InferredPattern { Config = config });
        analyzer.RefineLayoutAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteHierarchyConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new InferredPattern { Config = config });
        return analyzer;
    }

    private static IInputHandler InputSequence(params CommandType[] commands)
    {
        var input = Substitute.For<IInputHandler>();
        input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NavigationCommand { Type = commands[0] },
                commands.Skip(1).Select(c => new NavigationCommand { Type = c }).ToArray());
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
            .Returns(ci => { capturedAnswers = ci.Arg<IReadOnlyList<SetupAnswer>>(); return new InferredPattern { Config = SomeConfig() }; });

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
        budget.Used.Should().Be(3, "propose + infer + one refine for the pick");

        // workspace-9k27.4: a normal preview adjustment REFINES the current
        // layout (RefineLayoutAsync) — it must NOT regenerate from the proposal
        // (the old `shape ??=` bug sent every adjustment through re-inference).
        await analyzer.Received(1).RefineLayoutAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
            Arg.Any<SiteHierarchyConfig>(), Arg.Is<string>(i => i.Contains("The real lead")), Arg.Any<CancellationToken>());
        await analyzer.Received(1).InferPatternFromAnswersAsync(
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
                return new InferredPattern { Config = SomeConfig() };
            });
        var refineInstructions = new List<string>();
        analyzer.RefineLayoutAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteHierarchyConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                refineInstructions.Add(ci.ArgAt<string>(4));
                return new InferredPattern { Config = SomeConfig() };
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
        budget.Used.Should().Be(3, "the free-text adjustment costs exactly one refine call");

        // workspace-9k27.4: the adjustment REFINES the current layout — the
        // instruction reaches RefineLayoutAsync verbatim, and no second
        // from-scratch re-inference happens.
        allAnswers.Should().HaveCount(1);
        refineInstructions.Should().ContainSingle().Which.Should().Be("hide the opinion pieces");
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

    // ---- workspace-6yb7.4: directed-question filter ----

    [Fact]
    public void IsDiscriminating_RealAlternatives_Pass()
    {
        var question = new SetupQuestion
        {
            Id = "q",
            Prompt = "Which of these is the story river?",
            Kind = SetupQuestionKind.PickMain,
            Options = new List<SetupOption>
            {
                new() { Label = "The clustered headlines", ParentSelector = "div.clus" },
                new() { Label = "The sidebar list", ParentSelector = "div.sidebar" },
            },
        };

        SetupWizard.IsDiscriminating(question).Should().BeTrue();
    }

    [Fact]
    public void IsDiscriminating_HideOrKeepSameElement_Passes()
    {
        // Both options legitimately reference the same element — the verdict
        // differs, and the element is highlightable.
        var question = new SetupQuestion
        {
            Id = "q",
            Prompt = "Hide the sponsor posts?",
            Kind = SetupQuestionKind.ConfirmExclude,
            Options = new List<SetupOption>
            {
                new() { Label = "Hide them", ParentSelector = "div.sponsor" },
                new() { Label = "Keep them", ParentSelector = "div.sponsor" },
            },
        };

        SetupWizard.IsDiscriminating(question).Should().BeTrue();
    }

    [Fact]
    public void IsDiscriminating_NoOptions_Dropped()
    {
        // The old flow synthesized a yes/no card from DefaultAnswer for these —
        // nothing to show on the page, nothing to decide.
        var question = new SetupQuestion
        {
            Id = "q",
            Prompt = "Does this look right?",
            Kind = SetupQuestionKind.ConfirmOrder,
            DefaultAnswer = "Yes",
        };

        SetupWizard.IsDiscriminating(question).Should().BeFalse();
    }

    [Fact]
    public void IsDiscriminating_NoIdentifiers_Dropped()
    {
        var question = new SetupQuestion
        {
            Id = "q",
            Prompt = "Is the order fine?",
            Kind = SetupQuestionKind.ConfirmOrder,
            Options = new List<SetupOption>
            {
                new() { Label = "Yes" },
                new() { Label = "No" },
            },
        };

        SetupWizard.IsDiscriminating(question).Should().BeFalse(
            "a question whose options cannot be highlighted is not visually answerable");
    }

    [Fact]
    public void IsDiscriminating_DuplicateLabels_Dropped()
    {
        var question = new SetupQuestion
        {
            Id = "q",
            Prompt = "Pick one",
            Kind = SetupQuestionKind.PickMain,
            Options = new List<SetupOption>
            {
                new() { Label = "Same", ParentSelector = "div.a" },
                new() { Label = "same", ParentSelector = "div.b" },
            },
        };

        SetupWizard.IsDiscriminating(question).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_DropsNonDiscriminatingQuestions_KeepsRealOnes()
    {
        var proposal = ProposalWith(questionCount: 1); // one real question (distinct labels? "Opt" only 1 option!)
        proposal.Questions.Clear();
        proposal.Questions.Add(new SetupQuestion
        {
            Id = "junk",
            Prompt = "Does this look right?",
            Kind = SetupQuestionKind.ConfirmOrder,
            DefaultAnswer = "Yes",
        });
        proposal.Questions.Add(new SetupQuestion
        {
            Id = "real",
            Prompt = "Which is the lead?",
            Kind = SetupQuestionKind.PickMain,
            DefaultAnswer = "Hero",
            Options = new List<SetupOption>
            {
                new() { Label = "Hero", ParentSelector = "section.hero" },
                new() { Label = "First feed item", ParentSelector = "section.feed" },
            },
        });

        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(proposal);
        IReadOnlyList<SetupAnswer>? captured = null;
        analyzer.InferPatternFromAnswersAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { captured = ci.Arg<IReadOnlyList<SetupAnswer>>().ToList(); return new InferredPattern { Config = SomeConfig() }; });

        var input = InputSequence(CommandType.ActivateLink);

        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            Links(), "https://x.com/", null, new ModelRoundTripBudget(),
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        result.Config.Should().NotBeNull();
        captured.Should().ContainSingle("only the discriminating question was asked")
            .Which.QuestionId.Should().Be("real");
    }

    // ---- workspace-6yb7.6: degenerate gate ----

    private static SiteHierarchyConfig MismatchedConfig() => new()
    {
        Domain = "x.com",
        UrlPattern = "^x$",
        Sections = new List<HierarchySection> { new() { Name = "Ghost", SortOrder = 0, ParentSelectors = new List<string> { "section.does-not-exist" } } },
        CreatedAt = DateTime.UtcNow,
        ModelVersion = "gpt-5-mini",
        Kind = LayoutKind.AiCurated,
        Version = 3,
    };

    [Fact]
    public void IsDegenerate_Cases()
    {
        var links = Links(); // one content link under "section.lead a"

        SetupWizard.IsDegenerate(MismatchedConfig(), links).Should().BeTrue("zero coverage");
        SetupWizard.IsDegenerate(SomeConfig(), links).Should().BeFalse("full coverage");
        SetupWizard.IsDegenerate(
            SomeConfig() with { Sections = new List<HierarchySection>() }, links)
            .Should().BeTrue("no sections at all");
        SetupWizard.IsDegenerate(MismatchedConfig(), new List<LinkInfo>())
            .Should().BeFalse("no story links to judge coverage against");

        // Near-degenerate: 1 of 20 covered (5%) is below the 10% floor.
        var manyLinks = Enumerable.Range(0, 20).Select(i => new LinkInfo
        {
            Url = $"https://x.com/{i}",
            DisplayText = $"L{i}",
            Type = LinkType.Content,
            ImportanceScore = 60,
            ParentSelector = i == 0 ? "section.lead a" : "div.elsewhere a",
        }).ToList();
        SetupWizard.IsDegenerate(SomeConfig(), manyLinks).Should().BeTrue("5% coverage is near-degenerate");
    }

    [Fact]
    public async Task DegenerateConfig_GetsOneAutoRepair_ThenPreviews()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProposalWith(questionCount: 0));

        var inferCalls = 0;
        var allAnswers = new List<IReadOnlyList<SetupAnswer>>();
        analyzer.InferPatternFromAnswersAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                allAnswers.Add(ci.Arg<IReadOnlyList<SetupAnswer>>().ToList());
                return new InferredPattern { Config = ++inferCalls == 1 ? MismatchedConfig() : SomeConfig() };
            });

        var input = InputSequence(CommandType.ActivateLink); // Enter saves the (repaired) preview

        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            Links(), "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        result.Config.Should().NotBeNull();
        result.Config!.Sections[0].Name.Should().Be("Top Story", "the repaired config is what previews and saves");
        budget.Used.Should().Be(3, "propose + infer + exactly one automatic repair");
        allAnswers[1].Should().Contain(a => a.QuestionId == "self-test-failure",
            "the repair round-trip carries the structured mismatch feedback");
    }

    [Fact]
    public async Task StillDegenerate_ShowsFailureCard_EscLeavesUnconfigured()
    {
        var analyzer = AnalyzerReturning(ProposalWith(questionCount: 0), MismatchedConfig());

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

        var input = InputSequence(CommandType.GoBack); // Esc on the failure card

        var previewApplied = 0;
        var budget = new ModelRoundTripBudget();
        var result = await SetupWizard.RunAsync(
            analyzer, input, Render, overlay, Links(), "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null,
            applyPreview: (_, _) => { previewApplied++; return Task.CompletedTask; },
            lens: null,
            CancellationToken.None);

        result.Cancelled.Should().BeTrue("a 0-coverage config must be unsaveable");
        result.Config.Should().BeNull();
        budget.Used.Should().Be(3, "propose + infer + the single repair attempt");
        previewApplied.Should().Be(0, "a degenerate config never reaches the live preview");
        titles.Should().Contain("No reliable pattern found");
        titles.Should().NotContain("Your new layout");
    }

    [Fact]
    public async Task FailureCard_PickStory_RecoversToPreviewAndSaves()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProposalWith(questionCount: 0));

        var inferCalls = 0;
        analyzer.InferPatternFromAnswersAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(_ => new InferredPattern { Config = ++inferCalls <= 2 ? MismatchedConfig() : SomeConfig() });

        // infer #1 degenerate → auto-repair infer #2 still degenerate → failure
        // card: Enter on "Point at the main story" → pick → infer #3 good →
        // preview → Enter saves.
        var input = InputSequence(
            CommandType.ActivateLink,
            CommandType.ActivateLink);

        var pickedLink = new LinkInfo
        {
            Url = "https://x.com/2026/06/11/lead",
            DisplayText = "The lead",
            Type = LinkType.Content,
            ImportanceScore = 95,
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
        result.Config!.Sections[0].Name.Should().Be("Top Story");
        budget.Used.Should().Be(4, "propose + infer + auto-repair + pick-driven re-inference");
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

        // workspace-5vqk.3: one section header row + one row per matched headline.
        card.Options.Should().HaveCount(2);
        card.Options[0].Label.Should().Contain("1 story");
        card.Options[0].HighlightSelector.Should().Be("section.lead a[href]");
        card.Options[1].Label.Should().Contain("A", "the real extracted headline text is rendered as a row");
        card.Footnote.Should().Be("1 of 2 story links covered");
    }

    [Fact]
    public void BuildPreviewCard_UndoAvailable_HintOffersUndo()
    {
        var config = SomeConfig();
        var links = new List<LinkInfo>
        {
            new() { Url = "https://x.com/a", DisplayText = "A", Type = LinkType.Content, ImportanceScore = 80, ParentSelector = "section.lead a" },
        };

        SetupWizard.BuildPreviewCard(config, links, canUndo: false).Hint.Should().NotContain("undo");
        SetupWizard.BuildPreviewCard(config, links, canUndo: true).Hint.Should().Contain("z undo");
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
        cards.SelectMany(c => c).Should().Contain(l => l.Contains("Top Story — 1 story", StringComparison.Ordinal));
        cleared.Should().BeGreaterThan(0, "highlights are cleared after the preview step");
    }

    // ---- workspace-cbjx.2: set the lead by URL ----

    private static List<LinkInfo> UrlLinks() => new()
    {
        new LinkInfo { Url = "https://example.com/story-one?utm=x#top", DisplayText = "Story One", Type = LinkType.Content, ImportanceScore = 90, ParentSelector = "section.lead a" },
        new LinkInfo { Url = "https://www.example.com/story-two/", DisplayText = "Story Two", Type = LinkType.Content, ImportanceScore = 70 },
        new LinkInfo { Url = "https://example.com/promo", DisplayText = "Subscribe", Type = LinkType.External, ImportanceScore = 20 },
        new LinkInfo { Url = "", DisplayText = "No URL", Type = LinkType.Content, ImportanceScore = 50 },
    };

    [Theory]
    [InlineData("https://example.com/story-one")]                 // scheme + no query/fragment
    [InlineData("http://www.example.com/story-one/?ref=twitter")]  // scheme/www/query/trailing-slash differ
    [InlineData("example.com/story-one#anything")]                 // no scheme, fragment
    public void ResolveLeadByUrl_NormalizesAndMatches(string typed)
    {
        var picked = SetupWizard.ResolveLeadByUrl(typed, UrlLinks());
        picked.Should().NotBeNull();
        picked!.DisplayText.Should().Be("Story One");
    }

    [Fact]
    public void ResolveLeadByUrl_MatchesTheSecondStoryByUrl()
    {
        var picked = SetupWizard.ResolveLeadByUrl("https://example.com/story-two", UrlLinks());
        picked!.DisplayText.Should().Be("Story Two", "normalization strips www. and the trailing slash");
    }

    [Fact]
    public void ResolveLeadByUrl_NoMatch_ReturnsNull()
    {
        SetupWizard.ResolveLeadByUrl("https://other.com/nope", UrlLinks()).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveLeadByUrl_BlankInput_ReturnsNull(string blank)
    {
        SetupWizard.ResolveLeadByUrl(blank, UrlLinks()).Should().BeNull();
    }

    // ---- workspace-45ji.3: vision lead-tiebreak helpers ----

    private static SiteHierarchyConfig TwoSectionConfig() => new()
    {
        Domain = "x.com",
        UrlPattern = "^https?://x\\.com/?",
        CreatedAt = DateTime.UtcNow,
        ModelVersion = "gpt-5-mini",
        Sections = new List<HierarchySection>
        {
            new() { Name = "A", SortOrder = 0, ParentSelectors = new() { "section.a" } },
            new() { Name = "B", SortOrder = 1, ParentSelectors = new() { "section.b" } },
        },
    };

    private static List<LinkInfo> TwoLeadLinks(int secondScore) => new()
    {
        new LinkInfo { Url = "https://x.com/alpha", DisplayText = "Alpha lead story headline now", Type = LinkType.Content, ImportanceScore = 100, ParentSelector = "section.a a" },
        new LinkInfo { Url = "https://x.com/beta", DisplayText = "Beta lead story headline too", Type = LinkType.Content, ImportanceScore = secondScore, ParentSelector = "section.b a" },
        new LinkInfo { Url = "https://x.com/minor", DisplayText = "minor", Type = LinkType.Content, ImportanceScore = 30, ParentSelector = "section.b a" },
    };

    [Fact]
    public void LeadIsAmbiguous_TwoNearEqualLeads_IsTrue_AndCandidatesAreTheCluster()
    {
        var config = TwoSectionConfig();
        var links = TwoLeadLinks(secondScore: 98); // within the 5-pt margin

        SetupWizard.LeadIsAmbiguous(config, links).Should().BeTrue();
        var cands = SetupWizard.LeadCandidates(config, links);
        cands.Select(l => l.Url).Should().Equal("https://x.com/alpha", "https://x.com/beta");
    }

    [Fact]
    public void LeadIsAmbiguous_ClearDominantLead_IsFalse()
    {
        SetupWizard.LeadIsAmbiguous(TwoSectionConfig(), TwoLeadLinks(secondScore: 80)).Should().BeFalse();
    }

    [Fact]
    public void PromoteLeadSection_MovesTheChosenLeadsSectionToFront()
    {
        var config = TwoSectionConfig();
        var links = TwoLeadLinks(secondScore: 98);

        var promoted = SetupWizard.PromoteLeadSection(config, links[1], links); // Beta is in section B

        promoted.Sections.Select(s => s.Name).Should().Equal("B", "A");
        promoted.Sections[0].SortOrder.Should().Be(0);
        promoted.Sections[1].SortOrder.Should().Be(1);
    }

    [Fact]
    public void PromoteLeadSection_LeadAlreadyLeading_IsNoOp()
    {
        var config = TwoSectionConfig();
        var links = TwoLeadLinks(secondScore: 98);

        var same = SetupWizard.PromoteLeadSection(config, links[0], links); // Alpha already in the first section

        same.Sections.Select(s => s.Name).Should().Equal("A", "B");
    }

    [Fact]
    public void ResolveLeadByUrl_NeverResolvesToAnEmptyUrlLink()
    {
        // The last row has an empty Url; no target may resolve onto it.
        foreach (var typed in new[] { "https://example.com/story-one", "https://example.com/", "example.com" })
        {
            var picked = SetupWizard.ResolveLeadByUrl(typed, UrlLinks());
            (picked == null || !string.IsNullOrEmpty(picked.Url)).Should().BeTrue();
        }
    }

    // ---- workspace-5vqk.4: deterministic per-item exclude ('x' on a row) ----

    private static LinkInfo Story(string url, string text, string parent, int score = 90) => new()
    {
        Url = url, DisplayText = text, Type = LinkType.Content, ImportanceScore = score, ParentSelector = parent,
    };

    private static LinkInfo Sponsor(string url, string text, string parent) => new()
    {
        Url = url, DisplayText = text, Type = LinkType.Content, ImportanceScore = 60, ParentSelector = parent, IsSponsored = true,
    };

    private static SiteHierarchyConfig ConfigOf(params HierarchySection[] sections) => new()
    {
        Domain = "x.com", UrlPattern = "^x$", Sections = sections.ToList(),
        CreatedAt = DateTime.UtcNow, ModelVersion = "test",
    };

    private static HierarchySection Sec(string name, string selector, int order = 0) =>
        new() { Name = name, SortOrder = order, ParentSelectors = new List<string> { selector } };

    [Fact]
    public void BuildPreviewCard_OverlappingSections_ListEachStoryOnce_FirstMatchWins()
    {
        // workspace-r8on: the model sometimes returns two near-identical selectors
        // matching the SAME river. The preview must mirror the saved tree's
        // first-match-wins assignment — list each story ONCE under the first
        // section, drop the subsumed duplicate — not double-list the whole river
        // (the techmeme judge showed 20 stories rendered twice while the tree saved
        // them once).
        var links = new List<LinkInfo>
        {
            Story("https://x.com/a", "Alpha river story headline text", "div.col > div.ii"),
            Story("https://x.com/b", "Beta river story headline text", "div.col > div.ii"),
            Story("https://x.com/c", "Gamma river story headline text", "div.col > div.ii"),
        };
        var config = ConfigOf(
            Sec("Lead cluster", "div.ii", 0),
            Sec("Top stories - co-equal", "div.ii", 1)); // subsumed duplicate selector

        var card = SetupWizard.BuildPreviewCard(config, links);
        var rendered = SetupWizardOverlay.DescribeCard(card, maxContentLines: int.MaxValue);

        rendered.Count(l => l.Contains("•", StringComparison.Ordinal)).Should().Be(3,
            "each of the 3 stories is listed exactly once, not 6");
        rendered.Count(l => l.Contains("Lead cluster", StringComparison.Ordinal)).Should().Be(1);
        rendered.Should().NotContain(l => l.Contains("co-equal", StringComparison.Ordinal),
            "the subsumed duplicate section is dropped from the preview");
        card.Prompt.Should().Contain("1 section");
    }

    [Fact]
    public void ExcludeItem_DropsSponsorRowAndTokenSiblings_KeepsEveryStory()
    {
        var links = new List<LinkInfo>
        {
            Story("https://x.com/a", "Alpha real story headline text", "section.river > article.story"),
            Story("https://x.com/b", "Beta real story headline text", "section.river > article.story"),
            Sponsor("https://ad.co/1", "Sponsored promo number one text", "aside.promos > div.promo"),
            Sponsor("https://ad.co/2", "Sponsored promo number two text", "aside.promos > div.promo"),
        };
        var config = ConfigOf(Sec("Stories", "section.river"), Sec("Promos", "div.promo", 1));

        var result = SetupWizard.ExcludeItem(config, links[2], links);

        result.Should().NotBeNull("a sponsor row is safely excludable");
        result!.ExcludeSelectors.Should().Contain("div.promo");
        // The item AND its token-sibling sponsor vanish; both real stories remain.
        NavigationTreeBuilder.IsExcluded(links[2], result).Should().BeTrue();
        NavigationTreeBuilder.IsExcluded(links[3], result).Should().BeTrue("the token-sibling sponsor is dropped too");
        NavigationTreeBuilder.IsExcluded(links[0], result).Should().BeFalse();
        NavigationTreeBuilder.IsExcluded(links[1], result).Should().BeFalse();
        // The input config is untouched (so 'z' can restore it by reference).
        config.ExcludeSelectors.Should().BeEmpty();
    }

    [Fact]
    public void Guardrail_DocumentOrderConfig_NotDegenerate_PreviewsAllArticlesInOrder()
    {
        // workspace-cn2g.1: a DocumentOrder config is the valid flat ordered article
        // list, not a failure — every article renders, in order.
        var links = new List<LinkInfo>
        {
            Story("https://x.com/1", "First article headline here now", "div.a"),
            Story("https://x.com/2", "Second article headline here now", "div.b"),
            Story("https://x.com/3", "Third article headline here now", "div.c"),
        };
        var flat = ConfigOf() with { Kind = LayoutKind.DocumentOrder };

        SetupWizard.IsDegenerate(flat, links).Should().BeFalse("a DocumentOrder config with articles is the valid flat list");

        var card = SetupWizard.BuildPreviewCard(flat, links);
        var rendered = SetupWizardOverlay.DescribeCard(card, maxContentLines: int.MaxValue);
        rendered.Count(l => l.Contains("•", StringComparison.Ordinal)).Should().Be(3, "every article is listed in order");
        card.Prompt.Should().Contain("All articles, in order");
    }

    [Fact]
    public async Task Guardrail_DegenerateModelLayout_FallsBackToFlatOrderedArticles()
    {
        // workspace-cn2g.1: when the model can't produce a reliable pattern (its
        // sections match nothing) but the page HAS articles, the wizard must NOT
        // dead-end — it defaults to the flat ordered article list, savable.
        var links = new List<LinkInfo>
        {
            Story("https://x.com/1", "First article headline here now", "div.a"),
            Story("https://x.com/2", "Second article headline here now", "div.b"),
            Story("https://x.com/3", "Third article headline here now", "div.c"),
        };
        var degenerate = ConfigOf(Sec("Nope", "div.doesnotmatch"));
        var analyzer = AnalyzerReturning(ProposalWith(questionCount: 0), degenerate);

        var result = await SetupWizard.RunAsync(
            analyzer, InputSequence(CommandType.ActivateLink), _ => Task.CompletedTask,
            new SetupWizardOverlay.State(), links, "https://x.com/", null, new ModelRoundTripBudget(),
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        result.Config.Should().NotBeNull();
        result.Config!.Kind.Should().Be(LayoutKind.DocumentOrder, "the never-block guardrail saved the flat ordered list");
        result.Config.Sections.Should().BeEmpty();
    }

    [Fact]
    public void ExcludeItem_AdUnderSponsorHeading_ExcludesTheWholeHeading_Durably()
    {
        // workspace-rpop.4: the DURABLE "remove one ad, extrapolate to the rest"
        // path — when the item sits under a recognized ad heading, hide the whole
        // heading (stable across revisits) instead of a volatile selector token,
        // catching every ad in that section (incl a 2nd camouflaged sponsor headline).
        LinkInfo Sect(string url, string text, string sect, LinkType type = LinkType.Content) => new()
        {
            Url = url, DisplayText = text, Type = type, ImportanceScore = 90,
            ParentSelector = "div#topcol2 > div#RANDOM > div.item > div.ii", SectionTitle = sect,
        };
        var links = new List<LinkInfo>
        {
            new() { Url = "https://x.com/a", DisplayText = "Real news story headline one here", Type = LinkType.Content, ImportanceScore = 90, ParentSelector = "div#topcol1 > div.ii", SectionTitle = "Top News" },
            Sect("https://ad.co/zoho", "Zoho named an overall leader in BI", "Sponsor Posts"),
            Sect("https://ad.co/f5", "Agentic AI: the need for a data layer", "Sponsor Posts"),
            Sect("https://ad.co/soxton", "Soxton", "Sponsor Posts", LinkType.Navigation),
        };
        var config = ConfigOf(Sec("Stories", "div.ii"));

        var result = SetupWizard.ExcludeItem(config, links[1], links);

        result.Should().NotBeNull();
        result!.ExcludeSectionTitles.Should().Contain("Sponsor Posts");
        result.ExcludeSelectors.Should().BeEmpty("the durable heading path is preferred over a volatile token");
        NavigationTreeBuilder.IsExcluded(links[1], result).Should().BeTrue("the removed ad");
        NavigationTreeBuilder.IsExcluded(links[2], result).Should().BeTrue("the 2nd sponsor headline, caught by the heading");
        NavigationTreeBuilder.IsExcluded(links[3], result).Should().BeTrue("the sponsor brand link");
        NavigationTreeBuilder.IsExcluded(links[0], result).Should().BeFalse("Top News survives");
    }

    [Fact]
    public void ExcludeItem_CamouflagedAd_ExtrapolatesToTheAdClass_KeepsNewsAndUncoveredRails()
    {
        // workspace-r8on — the core "remove an ad, extrapolate to other ads" flow,
        // mirroring techmeme: sponsor posts sit INSIDE the news markup (div.ii) in a
        // sponsor block, and podcasts are a real but UNCOVERED story-shaped cluster.
        // Removing one ad must exclude the whole sponsor block (every ad) while
        // keeping the news AND the uncovered podcasts.
        var links = new List<LinkInfo>
        {
            Story("https://x.com/a", "Real news story headline one here", "div#topcol1 > div.item > div.ii"),
            Story("https://x.com/b", "Real news story headline two here", "div#topcol1 > div.item > div.ii"),
            Story("https://pod.co/1", "A podcast episode about tech things", "div#topcol2 > div.podcast"),
            Story("https://pod.co/2", "Another podcast episode discussion", "div#topcol2 > div.podcast"),
            Story("https://ad.co/zoho", "Zoho named an overall leader in BI", "div#topcol2 > div#sponsorblk > div.item > div.ii"),
            new() { Url = "https://ad.co/soxton", DisplayText = "Soxton", Type = LinkType.Navigation, ImportanceScore = 30, ParentSelector = "div#topcol2 > div#sponsorblk > div.item > cite" },
            new() { Url = "https://ad.co/idrive", DisplayText = "IDrive", Type = LinkType.Navigation, ImportanceScore = 30, ParentSelector = "div#topcol2 > div#sponsorblk > div.item > cite" },
        };
        var config = ConfigOf(Sec("Stories", "div.ii")); // covers the news AND the camouflaged ad
        var ad = links[4];

        var result = SetupWizard.ExcludeItem(config, ad, links);

        result.Should().NotBeNull();
        result!.ExcludeSelectors.Should().Contain("div#sponsorblk");
        result.ExcludeSelectors.Should().NotContain("div#topcol2", "excluding the whole column would hide the real podcasts");
        // The whole ad class is gone.
        NavigationTreeBuilder.IsExcluded(links[4], result).Should().BeTrue("the ad headline");
        NavigationTreeBuilder.IsExcluded(links[5], result).Should().BeTrue("Soxton brand link");
        NavigationTreeBuilder.IsExcluded(links[6], result).Should().BeTrue("IDrive brand link");
        // News and (uncovered) podcasts survive.
        NavigationTreeBuilder.IsExcluded(links[0], result).Should().BeFalse();
        NavigationTreeBuilder.IsExcluded(links[2], result).Should().BeFalse("uncovered podcast stays");
        NavigationTreeBuilder.IsExcluded(links[3], result).Should().BeFalse();
    }

    [Fact]
    public void ExcludeItem_RefusedWhenEveryDistinctiveTokenAlsoMatchesAKeptStory()
    {
        // Two stories share the ONLY discriminating token AND their URL segments —
        // excluding one would erase the other, so the river is protected: REFUSED.
        var links = new List<LinkInfo>
        {
            Story("https://x.com/news/2026/01/01", "First river story headline text", "div.col > div.ii"),
            Story("https://x.com/news/2026/01/02", "Second river story headline text", "div.col > div.ii"),
        };
        var config = ConfigOf(Sec("Stories", "div.ii"));

        SetupWizard.ExcludeItem(config, links[0], links)
            .Should().BeNull("no token/pattern separates the item from the other kept story");
    }

    [Fact]
    public async Task Preview_ExcludeKey_DropsItem_WithZeroAnalyzerCalls()
    {
        // Re-entry (existingConfig) goes straight to the preview loop — no propose/
        // infer. Pressing 'x' on a sponsor row excludes it via ZERO model calls.
        var links = new List<LinkInfo>
        {
            Story("https://x.com/a", "Alpha real story headline text", "section.river > article.story"),
            Sponsor("https://ad.co/1", "Sponsored promo number one text", "aside.promos > div.promo"),
        };
        var config = ConfigOf(Sec("Stories", "section.river"), Sec("Promos", "div.promo", 1));
        var analyzer = Substitute.For<IHierarchyAnalyzer>();

        // Rows: [0]=Stories header, [1]=Alpha, [2]=Promos header, [3]=promo. Down x3
        // to the promo row, 'x' to drop it, then Enter to save.
        var input = InputCommands(
            new NavigationCommand { Type = CommandType.MoveDown },
            new NavigationCommand { Type = CommandType.MoveDown },
            new NavigationCommand { Type = CommandType.MoveDown },
            new NavigationCommand { Type = CommandType.CancelRun, RawKeyChar = 'x' }, // real 'x' key
            new NavigationCommand { Type = CommandType.ActivateLink });

        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            links, "https://x.com/", null, new ModelRoundTripBudget(),
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null,
            CancellationToken.None, existingConfig: config);

        result.Config.Should().NotBeNull();
        NavigationTreeBuilder.IsExcluded(links[1], result.Config!).Should().BeTrue("the promo was dropped by 'x'");
        NavigationTreeBuilder.IsExcluded(links[0], result.Config!).Should().BeFalse("the story survives");
        await analyzer.DidNotReceive().RefineLayoutAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
            Arg.Any<SiteHierarchyConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await analyzer.DidNotReceive().InferPatternFromAnswersAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
            Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>());
    }

    private static IInputHandler InputCommands(params NavigationCommand[] commands)
    {
        var input = Substitute.For<IInputHandler>();
        input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(commands[0], commands.Skip(1).ToArray());
        return input;
    }

    [Fact]
    public async Task Preview_ExcludeKey_Refused_ShowsAReasonNotSilentNoOp()
    {
        // Two stories share their ONLY identifier (div.ii + /news/ segment), so 'x'
        // on one is REFUSED (can't erase it without the other). The user must see a
        // reason, not a dead key — assert the rendered card explains the refusal.
        var links = new List<LinkInfo>
        {
            Story("https://x.com/news/2026/01/01", "First river story headline text", "div.col > div.ii"),
            Story("https://x.com/news/2026/01/02", "Second river story headline text", "div.col > div.ii"),
        };
        var config = ConfigOf(Sec("Stories", "div.ii"));

        var footnotes = new List<string>();
        var overlay = new SetupWizardOverlay.State();
        Task Render(CancellationToken _)
        {
            if (overlay.Card is { } c && overlay.Mode == SetupWizardOverlay.Mode.Card && c.Footnote != null)
            {
                footnotes.Add(c.Footnote);
            }

            return Task.CompletedTask;
        }

        // Rows: [0]=Stories header, [1]=story1, [2]=story2. Down to story1, 'x' (refused), Enter.
        var input = InputCommands(
            new NavigationCommand { Type = CommandType.MoveDown },
            new NavigationCommand { Type = CommandType.CancelRun, RawKeyChar = 'x' },
            new NavigationCommand { Type = CommandType.ActivateLink });

        var result = await SetupWizard.RunAsync(
            Substitute.For<IHierarchyAnalyzer>(), input, Render, overlay, links, "https://x.com/",
            null, new ModelRoundTripBudget(),
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null,
            CancellationToken.None, existingConfig: config);

        result.Config.Should().NotBeNull();
        footnotes.Should().Contain(f => f.Contains("Can't drop", StringComparison.Ordinal),
            "a refused 'x' must explain why instead of doing nothing");
        // Both stories survived (nothing was actually excluded).
        NavigationTreeBuilder.IsExcluded(links[0], result.Config!).Should().BeFalse();
        NavigationTreeBuilder.IsExcluded(links[1], result.Config!).Should().BeFalse();
    }

    // ---- workspace-5vqk.5: gate the vision lead-tiebreak on a GENUINE contest ----

    private static IHierarchyAnalyzer AnalyzerWithVision(SiteHierarchyConfig config, int visionIndex = -1)
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SiteSetupProposal { ProposedPattern = new ProposedPattern() });
        analyzer.InferPatternFromAnswersAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(new InferredPattern { Config = config });
        analyzer.VerifyLeadWithVisionAsync(Arg.Any<byte[]>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(visionIndex);
        return analyzer;
    }

    [Fact]
    public async Task FlatCoEqualPage_SkipsVisionTiebreak_ZeroVisionCalls()
    {
        // Eight co-equal top stories (a flat aggregator river): there is no single
        // lead to elect, so the vision tiebreak must NOT be spent.
        var links = Enumerable.Range(0, 8)
            .Select(i => Story($"https://x.com/{i}", $"Co-equal story headline number {i}", "section.river > article", 90))
            .ToList();
        var config = ConfigOf(Sec("Stories", "section.river"));
        var analyzer = AnalyzerWithVision(config);

        var result = await SetupWizard.RunAsync(
            analyzer, InputCommands(new NavigationCommand { Type = CommandType.ActivateLink }),
            _ => Task.CompletedTask, new SetupWizardOverlay.State(), links, "https://x.com/",
            screenshot: new byte[] { 1, 2, 3 }, new ModelRoundTripBudget(),
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        result.Config.Should().NotBeNull();
        await analyzer.DidNotReceive().VerifyLeadWithVisionAsync(
            Arg.Any<byte[]>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenuineLeadContest_InvokesVisionTiebreakExactlyOnce()
    {
        // A real 2-way lead contest (100 vs 98) standing above a pack (70): the
        // deterministic order can't decide, so ONE vision call is warranted.
        var links = new List<LinkInfo>
        {
            Story("https://x.com/lead", "Dominant lead story headline one", "section.river > article", 100),
            Story("https://x.com/runner", "Close runner-up story headline two", "section.river > article", 98),
        };
        links.AddRange(Enumerable.Range(0, 6)
            .Select(i => Story($"https://x.com/p{i}", $"Lesser pack story headline {i}", "section.river > article", 70)));
        var config = ConfigOf(Sec("Stories", "section.river"));
        var analyzer = AnalyzerWithVision(config, visionIndex: -1);

        await SetupWizard.RunAsync(
            analyzer, InputCommands(new NavigationCommand { Type = CommandType.ActivateLink }),
            _ => Task.CompletedTask, new SetupWizardOverlay.State(), links, "https://x.com/",
            screenshot: new byte[] { 1, 2, 3 }, new ModelRoundTripBudget(),
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: null, applyPreview: null, lens: null, CancellationToken.None);

        await analyzer.Received(1).VerifyLeadWithVisionAsync(
            Arg.Any<byte[]>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
