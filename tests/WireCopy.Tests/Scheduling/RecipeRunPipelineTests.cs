// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-frpl.8 (B7) — the per-occurrence run pipeline: ordered multi-source
/// assembly into ONE uniquely-named transient collection, cross-step URL dedup,
/// the quality floor (required-step + non-empty), and gated generation.
/// </summary>
[Trait("Category", "Unit")]
public class RecipeRunPipelineTests
{
    private const string NytUrl = "https://nyt.example/";
    private const string TechmemeUrl = "https://techmeme.example/";

    private static readonly SiteHierarchyConfig Cfg = new()
    {
        Domain = "example",
        UrlPattern = ".*",
        Sections = new(),
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ModelVersion = "test",
    };

    private readonly IHeadlessSectionLoader _loader = Substitute.For<IHeadlessSectionLoader>();
    private readonly ISectionResolver _resolver = Substitute.For<ISectionResolver>();
    private readonly IPodcastOrchestrator _orchestrator = Substitute.For<IPodcastOrchestrator>();
    private readonly IPodcastBackgroundJobManager _jobManager = Substitute.For<IPodcastBackgroundJobManager>();
    private readonly IScheduledRunRepository _runRepo = Substitute.For<IScheduledRunRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IScheduleStore _store = Substitute.For<IScheduleStore>();

    public RecipeRunPipelineTests()
    {
        _jobManager.HasActiveJob.Returns(false);
        _orchestrator.ResolveTargetsAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>())
            .Returns(new PodcastTargets { LocalFilePath = "/tmp/out.m4b", FeedUrl = "https://feed.example/rss.xml" });
        _orchestrator.GeneratePodcastAsync(Arg.Any<Collection>(), Arg.Any<IProgress<PodcastProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(OkResult());
    }

    private RecipeRunPipeline Sut() => new(
        _loader, _resolver, _orchestrator, _jobManager, _runRepo, _uow, _store,
        NullLogger<RecipeRunPipeline>.Instance);

    [Fact]
    public async Task ThreeStepsAcrossTwoSites_AssembleIntoOneCollectionInOrder_DedupingCrossStepUrls()
    {
        var front = RecipeStep.Create(NytUrl, "nyt.example", "nyt", "Front", required: true);
        var topStory = RecipeStep.Create(TechmemeUrl, "techmeme.example", "tm", "Top", TakeMode.SingleTopStory, required: true);
        var business = RecipeStep.Create(NytUrl, "nyt.example", "nyt", "Business", required: true);
        var recipe = Recipe("NYT Brief", front, topStory, business);

        LoadOk(NytUrl);
        LoadOk(TechmemeUrl);
        ResolveAs("Front", Resolved(("u1", "A"), ("u2", "B")));
        ResolveAs("Top", Resolved(("u3", "C"), ("u4", "D"))); // SingleTopStory → only u3
        ResolveAs("Business", Resolved(("u2", "B"), ("u5", "E"))); // u2 already added by Front

        Collection? captured = null;
        CaptureGeneratedCollection(c => captured = c);

        var run = NewRun(recipe);
        await Sut().RunAsync(recipe, run);

        captured.Should().NotBeNull();
        captured!.Items.Select(i => i.Url).Should().Equal("u1", "u2", "u3", "u5");
        run.Status.Should().Be(ScheduledRunStatus.Completed);
        run.ItemCount.Should().Be(4);
    }

    [Fact]
    public async Task SingleTopStory_ContributesExactlyOneItem()
    {
        var step = RecipeStep.Create(TechmemeUrl, "techmeme.example", "tm", "Top", TakeMode.SingleTopStory, required: true);
        var recipe = Recipe("Top Story", step);

        LoadOk(TechmemeUrl);
        ResolveAs("Top", Resolved(("u1", "A"), ("u2", "B"), ("u3", "C")));

        Collection? captured = null;
        CaptureGeneratedCollection(c => captured = c);

        await Sut().RunAsync(recipe, NewRun(recipe));

        captured!.Items.Should().ContainSingle().Which.Url.Should().Be("u1");
    }

    [Fact]
    public async Task TwoOccurrencesOfSameRecipe_ProduceUniqueCollectionNames()
    {
        var step = RecipeStep.Create(NytUrl, "nyt.example", "nyt", "Front", required: true);
        var recipe = Recipe("NYT Brief", step);
        LoadOk(NytUrl);
        ResolveAs("Front", Resolved(("u1", "A")));

        var names = new List<string>();
        CaptureGeneratedCollection(c => names.Add(c.Name));

        await Sut().RunAsync(recipe, NewRun(recipe, "2026-05-30@07:00"));
        await Sut().RunAsync(recipe, NewRun(recipe, "2026-05-31@07:00"));

        names.Should().HaveCount(2);
        names[0].Should().NotBe(names[1], "the occurrence key is embedded so the name-keyed cache + output path never collide");
        names.Should().AllSatisfy(n => n.Should().StartWith("NYT Brief"));
    }

    [Fact]
    public async Task AllStepsZeroMatch_ReturnsTotalFailure_AndNeverCallsGenerate()
    {
        var s1 = RecipeStep.Create(NytUrl, "nyt.example", "nyt", "Front", required: true);
        var s2 = RecipeStep.Create(NytUrl, "nyt.example", "nyt", "Business", required: true);
        var recipe = Recipe("NYT Brief", s1, s2);

        LoadOk(NytUrl);
        ResolveAs("Front", ZeroMatch());
        ResolveAs("Business", ZeroMatch());

        var run = NewRun(recipe);
        await Sut().RunAsync(recipe, run);

        run.Status.Should().Be(ScheduledRunStatus.Failed);
        run.ErrorClass.Should().Be("NoContentResolved");
        await _orchestrator.DidNotReceive().GeneratePodcastAsync(
            Arg.Any<Collection>(), Arg.Any<IProgress<PodcastProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequiredStepBlocked_WhileOptionalResolves_QualityFloorAborts_NoNearEmptyEpisode()
    {
        var required = RecipeStep.Create(NytUrl, "nyt.example", "nyt", "Front", required: true);
        var optional = RecipeStep.Create(TechmemeUrl, "techmeme.example", "tm", "Top", required: false);
        var recipe = Recipe("NYT Brief", required, optional);

        LoadBlocked(NytUrl); // the REQUIRED source is blocked → contributes nothing
        LoadOk(TechmemeUrl);
        ResolveAs("Top", Resolved(("u1", "A"))); // optional does resolve

        var run = NewRun(recipe);
        await Sut().RunAsync(recipe, run);

        run.Status.Should().Be(ScheduledRunStatus.Failed);
        run.ErrorClass.Should().Be("NoContentResolved");
        await _orchestrator.DidNotReceive().GeneratePodcastAsync(
            Arg.Any<Collection>(), Arg.Any<IProgress<PodcastProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllRequiredResolve_OneOptionalBlocked_YieldsPartialSuccess_AndStillPublishes()
    {
        var required = RecipeStep.Create(NytUrl, "nyt.example", "nyt", "Front", required: true);
        var optional = RecipeStep.Create(TechmemeUrl, "techmeme.example", "tm", "Top", required: false);
        var recipe = Recipe("NYT Brief", required, optional);

        LoadOk(NytUrl);
        ResolveAs("Front", Resolved(("u1", "A"), ("u2", "B")));
        LoadBlocked(TechmemeUrl); // optional source down

        var run = NewRun(recipe);
        await Sut().RunAsync(recipe, run);

        run.Status.Should().Be(ScheduledRunStatus.PartialSuccess);
        run.ItemCount.Should().Be(2);
        await _orchestrator.Received(1).GeneratePodcastAsync(
            Arg.Any<Collection>(), Arg.Any<IProgress<PodcastProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuccessfulRun_RegistersOnTheJobSurface_AndClearsOnCompletion()
    {
        var step = RecipeStep.Create(NytUrl, "nyt.example", "nyt", "Front", required: true);
        var recipe = Recipe("NYT Brief", step);
        LoadOk(NytUrl);
        ResolveAs("Front", Resolved(("u1", "A")));

        var run = NewRun(recipe);
        await Sut().RunAsync(recipe, run);

        run.Status.Should().Be(ScheduledRunStatus.Completed);
        _jobManager.Received(1).StartJob(
            Arg.Any<Collection>(), Arg.Any<PodcastTargets?>(), Arg.Any<Task<PodcastResult>>(), Arg.Any<CancellationTokenSource>());
        _jobManager.Received().Clear();
    }

    // ---- helpers ----
    private static ScheduleRecipe Recipe(string name, params RecipeStep[] steps) =>
        ScheduleRecipe.Create(name, Cadence.Daily(new TimeOnly(7, 0)), steps, name);

    private static ScheduledRun NewRun(ScheduleRecipe recipe, string occurrenceKey = "2026-05-30@07:00") =>
        ScheduledRun.Start(recipe.Id, recipe.Name, occurrenceKey);

    private void LoadOk(string url) =>
        _loader.LoadLinksAndConfigAsync(url, Arg.Any<CancellationToken>())
            .Returns(new HeadlessSectionLoad { Outcome = LoadOutcome.Ok, Links = Array.Empty<LinkInfo>(), Config = Cfg });

    private void LoadBlocked(string url) =>
        _loader.LoadLinksAndConfigAsync(url, Arg.Any<CancellationToken>())
            .Returns(new HeadlessSectionLoad { Outcome = LoadOutcome.Blocked });

    private void ResolveAs(string sectionName, SectionResolution resolution) =>
        _resolver.Resolve(Arg.Any<SiteHierarchyConfig>(), Arg.Any<IReadOnlyList<LinkInfo>>(),
            Arg.Is<RecipeStep>(s => s.SectionName == sectionName)).Returns(resolution);

    private void CaptureGeneratedCollection(Action<Collection> capture) =>
        _orchestrator.GeneratePodcastAsync(
            Arg.Do<Collection>(capture), Arg.Any<IProgress<PodcastProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(OkResult());

    private static SectionResolution Resolved(params (string Url, string Title)[] items) => new()
    {
        Status = ResolutionStatus.Resolved,
        Items = items,
        MatchCount = items.Length,
        Tier = SectionMatchTier.Selector,
    };

    private static SectionResolution ZeroMatch() => new()
    {
        Status = ResolutionStatus.ZeroMatch,
        Diagnostic = "section matched 0 links today",
    };

    private static PodcastResult OkResult() =>
        PodcastResult.Successful("https://feed.example/rss.xml", "/tmp/out.m4b", TimeSpan.FromMinutes(5), 1, 0, 1024);
}
