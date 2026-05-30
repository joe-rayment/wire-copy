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

        var input = Substitute.For<IInputHandler>();
        input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(new NavigationCommand { Type = CommandType.ActivateLink }); // Enter every card

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
            CancellationToken.None);

        result.Config.Should().NotBeNull();
        result.UseDocumentOrder.Should().BeFalse();
        budget.Used.Should().Be(2, "accept-all spends exactly propose + infer");
        capturedAnswers!.Count.Should().BeLessThanOrEqualTo(SetupWizard.MaxStructuredQuestions,
            "structured questions are clamped to at most 3 (5 proposed)");

        // Identifier transparency: at least one rendered card shows the durable
        // selector that will be saved.
        cards.SelectMany(c => c).Should().Contain(l => l.Contains("section.lead", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DocumentOrderEscape_OnOverviewCard_SkipsInferenceRoundTrip()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProposalWith(questionCount: 2));

        var input = Substitute.For<IInputHandler>();
        // On the overview card: Down to the "document order" option, then Enter.
        input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NavigationCommand { Type = CommandType.MoveDown },
                new NavigationCommand { Type = CommandType.ActivateLink });

        var overlay = new SetupWizardOverlay.State();
        var budget = new ModelRoundTripBudget();

        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, overlay, Links(), "https://x.com/", null, budget,
            _ => Task.FromResult<string?>(string.Empty), pickLeadFromTree: null, CancellationToken.None);

        result.UseDocumentOrder.Should().BeTrue();
        result.Config.Should().BeNull();
        budget.Used.Should().Be(1, "only the proposal call ran; inference was skipped");
        await analyzer.DidNotReceive().InferPatternFromAnswersAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
            Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>());
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
            pickLeadFromTree: null, CancellationToken.None);

        result.Cancelled.Should().BeTrue();
        await analyzer.DidNotReceive().ProposeSetupQuestionsAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- workspace-5oe9.9: point-at-a-link pick mode ----

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
    public async Task PickLeadOverride_ContentNode_PerformsExactlyOneExtraRoundTrip()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProposalWith(questionCount: 0));
        analyzer.InferPatternFromAnswersAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(_ => SomeConfig());

        var input = Substitute.For<IInputHandler>();
        // Overview: Down ×2 to the "point at the main story" option (index 2), then Enter.
        input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NavigationCommand { Type = CommandType.MoveDown },
                new NavigationCommand { Type = CommandType.MoveDown },
                new NavigationCommand { Type = CommandType.ActivateLink });

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
            CancellationToken.None);

        result.Config.Should().NotBeNull();
        budget.Used.Should().Be(3, "propose + infer + one re-inference for the override");
        await analyzer.Received(2).InferPatternFromAnswersAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
            Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PickLeadOverride_HeaderNode_DoesNotReInfer()
    {
        var analyzer = Substitute.For<IHierarchyAnalyzer>();
        analyzer.ProposeSetupQuestionsAsync(Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProposalWith(questionCount: 0));
        analyzer.InferPatternFromAnswersAsync(
                Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
                Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>())
            .Returns(_ => SomeConfig());

        var input = Substitute.For<IInputHandler>();
        input.WaitForInputAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NavigationCommand { Type = CommandType.MoveDown },
                new NavigationCommand { Type = CommandType.MoveDown },
                new NavigationCommand { Type = CommandType.ActivateLink });

        var header = LinkInfo.CreateSubSectionHeader("Top Story", LinkType.Content);
        var budget = new ModelRoundTripBudget();

        var result = await SetupWizard.RunAsync(
            analyzer, input, _ => Task.CompletedTask, new SetupWizardOverlay.State(),
            Links(), "https://x.com/", null, budget,
            freeTextPrompt: _ => Task.FromResult<string?>(string.Empty),
            pickLeadFromTree: _ => Task.FromResult<LinkInfo?>(header),
            CancellationToken.None);

        result.Config.Should().NotBeNull("a rejected pick keeps the base config");
        budget.Used.Should().Be(2, "a header pick is rejected before any re-inference");
        await analyzer.Received(1).InferPatternFromAnswersAsync(
            Arg.Any<byte[]?>(), Arg.Any<List<LinkInfo>>(), Arg.Any<string>(),
            Arg.Any<SiteSetupProposal>(), Arg.Any<IReadOnlyList<SetupAnswer>>(), Arg.Any<CancellationToken>());
    }
}
