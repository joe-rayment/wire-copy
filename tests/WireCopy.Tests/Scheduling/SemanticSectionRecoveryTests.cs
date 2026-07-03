// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-frpl.10 (B9b) — the budgeted, confidence-gated, self-tested semantic
/// recovery tier. Proven entirely with a fake <see cref="IHierarchyAnalyzer"/> — no
/// real OpenAI call. A SINGLE high-confidence classification that passes the
/// self-test recovers (run-local, flagged Recovered); everything else falls through
/// to the loud skip, and recovery model calls are capped per day.
/// </summary>
[Trait("Category", "Unit")]
public class SemanticSectionRecoveryTests
{
    private readonly IHierarchyAnalyzer _analyzer = Substitute.For<IHierarchyAnalyzer>();

    private static readonly SiteHierarchyConfig Config = new()
    {
        Domain = "nytimes.com",
        UrlPattern = ".*",
        Sections = new List<HierarchySection> { new() { Name = "Business", SortOrder = 1, ParentSelectors = new() { "section.business" } } },
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ModelVersion = "t",
    };

    public SemanticSectionRecoveryTests() => _analyzer.IsConfigured.Returns(true);

    private SemanticSectionRecovery Sut() =>
        new(_analyzer, NullLogger<SemanticSectionRecovery>.Instance, new FakeTimeProvider(new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)));

    // Today's page renamed "Business" to "Markets & Finance" (heading drift) AND its
    // selectors no longer match — so only a semantic classify can recover it.
    private static List<LinkInfo> DriftedLinks() => new()
    {
        Link("https://nyt/m1", "Markets up", "section.css-NEW a", "Markets & Finance"),
        Link("https://nyt/m2", "Deals", "section.css-NEW a", "Markets & Finance"),
        Link("https://nyt/t1", "Top story", "section.top a", "Top Stories"),
    };

    private static RecipeStep BusinessStep(TakeMode mode = TakeMode.WholeSection) =>
        RecipeStep.Create("https://www.nytimes.com/", "nytimes.com", "nyt", "Business", takeMode: mode, sortOrderFallback: 1, required: true);

    [Fact]
    public async Task SingleHighConfidence_PassesSelfTest_Recovers()
    {
        _analyzer.ClassifySectionAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SectionClassification { CandidateLabel = "Markets & Finance", Confidence = 0.92 });

        var r = await Sut().TryRecoverAsync(Config, DriftedLinks(), BusinessStep());

        r.Should().NotBeNull();
        r!.Status.Should().Be(ResolutionStatus.Recovered);
        r.Items.Select(i => i.Url).Should().Equal("https://nyt/m1", "https://nyt/m2");
        r.Diagnostic.Should().Contain("Markets & Finance").And.Contain("ratify");
    }

    [Fact]
    public async Task LowConfidence_FallsThroughToSkip()
    {
        _analyzer.ClassifySectionAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SectionClassification { CandidateLabel = "Markets & Finance", Confidence = 0.5 });

        (await Sut().TryRecoverAsync(Config, DriftedLinks(), BusinessStep())).Should().BeNull();
    }

    [Fact]
    public async Task NoCandidate_FallsThroughToSkip()
    {
        _analyzer.ClassifySectionAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SectionClassification.None);

        (await Sut().TryRecoverAsync(Config, DriftedLinks(), BusinessStep())).Should().BeNull();
    }

    [Fact]
    public async Task SelfTestFails_WhenClassifiedHeadingMatchesNoArticle()
    {
        // The model returns a confident label that does not actually head any of
        // today's content links → the self-test (re-match > 0) must reject it.
        _analyzer.ClassifySectionAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SectionClassification { CandidateLabel = "Obituaries", Confidence = 0.99 });

        (await Sut().TryRecoverAsync(Config, DriftedLinks(), BusinessStep())).Should().BeNull();
    }

    [Fact]
    public async Task NotConfigured_MakesNoModelCall()
    {
        _analyzer.IsConfigured.Returns(false);

        var r = await Sut().TryRecoverAsync(Config, DriftedLinks(), BusinessStep());

        r.Should().BeNull();
        await _analyzer.DidNotReceive().ClassifySectionAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoHeadingsPresentToday_MakesNoModelCall()
    {
        var flat = new List<LinkInfo> { Link("https://nyt/x", "X", "div.a a", sectionTitle: null) };

        var r = await Sut().TryRecoverAsync(Config, flat, BusinessStep());

        r.Should().BeNull();
        await _analyzer.DidNotReceive().ClassifySectionAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SingleTopStoryTakeMode_RecoversExactlyOne()
    {
        _analyzer.ClassifySectionAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SectionClassification { CandidateLabel = "Markets & Finance", Confidence = 0.9 });

        var r = await Sut().TryRecoverAsync(Config, DriftedLinks(), BusinessStep(TakeMode.SingleTopStory));

        r!.Items.Should().ContainSingle().Which.Url.Should().Be("https://nyt/m1");
    }

    [Fact]
    public async Task DailyBudget_CapsModelCalls()
    {
        _analyzer.ClassifySectionAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SectionClassification { CandidateLabel = "Markets & Finance", Confidence = 0.9 });
        var sut = Sut(); // a single instance shares the per-day counter

        for (var i = 0; i < 25; i++)
        {
            await sut.TryRecoverAsync(Config, DriftedLinks(), BusinessStep());
        }

        // The cap is 20/day — the 21st..25th attempts make NO further model call.
        await _analyzer.Received(20).ClassifySectionAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- the analyzer's parse helper (no model) ----
    [Fact]
    public void ParseSectionClassification_AcceptsOnlyOfferedLabels_AndClampsConfidence()
    {
        var labels = new List<string> { "Markets & Finance", "Top Stories" };

        OpenAiHierarchyAnalyzer.ParseSectionClassification("""{"label":"Markets & Finance","confidence":1.4}""", labels)
            .Should().BeEquivalentTo(new { CandidateLabel = "Markets & Finance", Confidence = 1.0 });

        // Hallucinated label not in the offered set → None.
        OpenAiHierarchyAnalyzer.ParseSectionClassification("""{"label":"Sports","confidence":0.9}""", labels)
            .CandidateLabel.Should().BeNull();

        // Null label → None.
        OpenAiHierarchyAnalyzer.ParseSectionClassification("""{"label":null,"confidence":0.9}""", labels)
            .CandidateLabel.Should().BeNull();
    }

    [Fact]
    public async Task Pipeline_OnZeroMatch_UsesSemanticRecovery_AndStillPublishes()
    {
        // The resolver returns ZeroMatch (config section selectors don't match the
        // drifted links); the pipeline must consult the recovery tier and, on a
        // Recovered result, assemble + publish (a degraded-but-recovered PartialSuccess).
        var loader = Substitute.For<Application.Interfaces.Scheduling.IUnattendedSectionLoader>();
        loader.LoadLinksAndConfigAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UnattendedSectionLoad { Outcome = Application.DTOs.Scheduling.LoadOutcome.Ok, Links = DriftedLinks(), Config = Config });

        var recovery = Substitute.For<Application.Interfaces.Scheduling.ISemanticSectionRecovery>();
        recovery.TryRecoverAsync(Arg.Any<SiteHierarchyConfig>(), Arg.Any<IReadOnlyList<LinkInfo>>(), Arg.Any<RecipeStep>(), Arg.Any<CancellationToken>())
            .Returns(new SectionResolution
            {
                Status = ResolutionStatus.Recovered,
                Items = new[] { ("https://nyt/m1", "Markets up") },
                MatchCount = 1,
            });

        var orchestrator = Substitute.For<Application.Interfaces.Podcast.IPodcastOrchestrator>();
        orchestrator.ResolveTargetsAsync(Arg.Any<WireCopy.Domain.Entities.Collections.Collection>(), Arg.Any<CancellationToken>())
            .Returns(new Application.DTOs.Podcast.PodcastTargets { LocalFilePath = "/tmp/o.m4b" });
        orchestrator.GeneratePodcastAsync(Arg.Any<WireCopy.Domain.Entities.Collections.Collection>(), Arg.Any<IProgress<Application.DTOs.Podcast.PodcastProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Application.DTOs.Podcast.PodcastResult.Successful("https://feed/x.xml", "/tmp/o.m4b", TimeSpan.FromMinutes(1), 1, 0, 1024));

        var pipeline = new RecipeRunPipeline(
            loader,
            new SectionResolver(),
            orchestrator,
            Substitute.For<Application.Interfaces.Browser.IPodcastBackgroundJobManager>(),
            Substitute.For<Application.Interfaces.Scheduling.IScheduledRunRepository>(),
            Substitute.For<Application.Interfaces.IUnitOfWork>(),
            Substitute.For<Application.Interfaces.Scheduling.IScheduleStore>(),
            NullLogger<RecipeRunPipeline>.Instance,
            recovery);

        var recipe = ScheduleRecipe.Create("Brief", Cadence.Daily(new TimeOnly(7, 0)), new[] { BusinessStep() });
        var run = WireCopy.Domain.Entities.Scheduling.ScheduledRun.Start(recipe.Id, recipe.Name, "2026-06-01@07:00");

        await pipeline.RunAsync(recipe, run);

        await recovery.Received(1).TryRecoverAsync(
            Arg.Any<SiteHierarchyConfig>(), Arg.Any<IReadOnlyList<LinkInfo>>(), Arg.Any<RecipeStep>(), Arg.Any<CancellationToken>());
        await orchestrator.Received(1).GeneratePodcastAsync(
            Arg.Any<WireCopy.Domain.Entities.Collections.Collection>(), Arg.Any<IProgress<Application.DTOs.Podcast.PodcastProgress>?>(), Arg.Any<CancellationToken>());
        run.Status.Should().Be(WireCopy.Domain.Enums.Scheduling.ScheduledRunStatus.PartialSuccess, "a recovered step is degraded-but-published");
    }

    private static LinkInfo Link(string url, string text, string parent, string? sectionTitle) => new()
    {
        Url = url, DisplayText = text, Type = LinkType.Content, ImportanceScore = 60, ParentSelector = parent, SectionTitle = sectionTitle,
    };
}
