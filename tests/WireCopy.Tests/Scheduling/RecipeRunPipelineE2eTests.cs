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
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-frpl.16 (B13a) — fast, TUI-free, deterministic end-to-end of the
/// scheduled-run runtime path: a 3-step recipe across two sites flows through the
/// REAL <see cref="SectionResolver"/> (over fixture links/config) and the
/// <see cref="RecipeRunPipeline"/> into a stub orchestrator + stub publisher, with
/// the B0 gate held by the caller and an in-memory run repository. Proves the
/// assembled Collection (ordered, cross-step-deduped, uniquely named) reaches the
/// orchestrator, the publisher produces the expected ordered feed entries, and the
/// ScheduledRun row is finalized Completed — all with NO creds and NO real network.
/// </summary>
[Trait("Category", "Integration")]
public class RecipeRunPipelineE2eTests
{
    private const string NytUrl = "https://www.nytimes.com/";
    private const string TechmemeUrl = "https://www.techmeme.com/";

    [Fact]
    public async Task ScheduledRecipe_ResolveAssemblePublish_ProducesOrderedFeed_AndCompletesRun()
    {
        // --- fixtures: two sites, durable section configs + today's links ---
        var nytConfig = new SiteHierarchyConfig
        {
            Domain = "nytimes.com",
            UrlPattern = "^https?://(www\\.)?nytimes\\.com/?$",
            Sections = new List<HierarchySection>
            {
                new() { Name = "Front", SortOrder = 0, ParentSelectors = new() { "section.front" } },
                new() { Name = "Business", SortOrder = 1, ParentSelectors = new() { "section.business" } },
            },
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModelVersion = "test",
        };
        var techmemeConfig = new SiteHierarchyConfig
        {
            Domain = "techmeme.com",
            UrlPattern = "^https?://(www\\.)?techmeme\\.com/?$",
            Sections = new List<HierarchySection> { new() { Name = "Top", SortOrder = 0, ParentSelectors = new() { "div.top" } } },
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModelVersion = "test",
        };

        var nytLinks = new List<LinkInfo>
        {
            Content("https://www.nytimes.com/f1", "Front 1", "section.front a"),
            Content("https://www.nytimes.com/f2", "Front 2", "section.front a"),
            Content("https://www.nytimes.com/shared", "Shared", "section.front a"),    // also under Business below
            Content("https://www.nytimes.com/shared", "Shared", "section.business a"), // cross-step dup → first wins
            Content("https://www.nytimes.com/b1", "Biz 1", "section.business a"),
        };
        var techmemeLinks = new List<LinkInfo>
        {
            Content("https://www.techmeme.com/t1", "TM Top", "div.top a"),
            Content("https://www.techmeme.com/t2", "TM Second", "div.top a"),
            Content("https://www.techmeme.com/t3", "TM Third", "div.top a"),
        };

        var loader = Substitute.For<IUnattendedSectionLoader>();
        loader.LoadLinksAndConfigAsync(NytUrl, Arg.Any<CancellationToken>())
            .Returns(new UnattendedSectionLoad { Outcome = LoadOutcome.Ok, Links = nytLinks, Config = nytConfig });
        loader.LoadLinksAndConfigAsync(TechmemeUrl, Arg.Any<CancellationToken>())
            .Returns(new UnattendedSectionLoad { Outcome = LoadOutcome.Ok, Links = techmemeLinks, Config = techmemeConfig });

        // --- the recipe: NYT front → Techmeme single top story → NYT business ---
        var recipe = ScheduleRecipe.Create(
            "Morning Brief",
            Cadence.Daily(new TimeOnly(7, 0)),
            new[]
            {
                RecipeStep.Create(NytUrl, "nytimes.com", "nyt", "Front", required: true),
                RecipeStep.Create(TechmemeUrl, "techmeme.com", "tm", "Top", TakeMode.SingleTopStory, required: true),
                RecipeStep.Create(NytUrl, "nytimes.com", "nyt", "Business", sortOrderFallback: 1, required: true),
            },
            "Morning Brief");

        // --- real resolver + stub publish leg, B0 gate, in-memory run repo ---
        var publisher = new RecordingPublisher();
        var orchestrator = new StubOrchestrator(publisher);
        var runRepo = new InMemoryRunRepo();
        var gate = new PodcastGenerationGate();
        var pipeline = new RecipeRunPipeline(
            loader,
            new SectionResolver(),
            orchestrator,
            Substitute.For<IPodcastBackgroundJobManager>(),
            runRepo,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IScheduleStore>(),
            NullLogger<RecipeRunPipeline>.Instance);

        var run = ScheduledRun.Start(recipe.Id, recipe.Name, "2026-05-30@07:00");
        await runRepo.AddAsync(run);

        // The caller (scheduler B6 / run-now B12a) owns the gate; the pipeline runs under it.
        gate.TryAcquire(out var lease).Should().BeTrue();
        try
        {
            await pipeline.RunAsync(recipe, run);
        }
        finally
        {
            lease!.Dispose();
        }

        // --- the assembled collection: ordered + cross-step deduped + uniquely named ---
        orchestrator.Captured.Should().NotBeNull();
        orchestrator.Captured!.Items.Select(i => i.Url).Should().Equal(
            "https://www.nytimes.com/f1",
            "https://www.nytimes.com/f2",
            "https://www.nytimes.com/shared", // contributed by Front; the Business duplicate is dropped
            "https://www.techmeme.com/t1",    // SingleTopStory → exactly one from Techmeme
            "https://www.nytimes.com/b1");
        orchestrator.Captured.Name.Should().StartWith("Morning Brief").And.Contain("2026-05-30@07.00",
            "the occurrence key is embedded so the name-keyed cache + output path are unique per run");

        // --- the stub publisher produced a feed with the same ordered entries ---
        publisher.PublishedEpisodes.Select(e => e.Title).Should().Equal(
            "Front 1", "Front 2", "Shared", "TM Top", "Biz 1");
        publisher.PublishedEpisodes.Select(e => e.SourceUrl).Should().Equal(
            "https://www.nytimes.com/f1",
            "https://www.nytimes.com/f2",
            "https://www.nytimes.com/shared",
            "https://www.techmeme.com/t1",
            "https://www.nytimes.com/b1");

        // --- the ScheduledRun row is finalized to success ---
        run.Status.Should().Be(ScheduledRunStatus.Completed);
        run.ItemCount.Should().Be(5);
        run.TargetFeedUrl.Should().Be(RecordingPublisher.FeedUrlConst);
        runRepo.All.Should().ContainSingle().Which.Status.Should().Be(ScheduledRunStatus.Completed);
    }

    private static LinkInfo Content(string url, string text, string parent) => new()
    {
        Url = url, DisplayText = text, Type = LinkType.Content, ImportanceScore = 60, ParentSelector = parent,
    };

    // ---- stub publish leg (records the ordered feed entries it is asked to publish) ----
    private sealed class RecordingPublisher : IPodcastPublisher
    {
        public const string FeedUrlConst = "https://storage.example/feeds/morning-brief/feed.xml";

        public List<EpisodeSource> PublishedEpisodes { get; } = new();

        public Task<FeedPublishResult> PublishFeedAsync(
            PodcastMetadata podcast,
            IReadOnlyList<EpisodeSource> episodes,
            IProgress<PublishProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            PublishedEpisodes.AddRange(episodes);
            return Task.FromResult(new FeedPublishResult
            {
                Success = true,
                FeedUrl = FeedUrlConst,
                EpisodesPublished = episodes.Count,
                PublishedAtUtc = new DateTime(2026, 5, 30, 7, 1, 0, DateTimeKind.Utc),
            });
        }

        public Task<string?> GetExistingFeedUrlAsync(string title, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(FeedUrlConst);

        public Task<string> ResolveFeedUrlAsync(string title, CancellationToken cancellationToken = default) =>
            Task.FromResult(FeedUrlConst);

        public Task<FeedPublishResult> BootstrapFeedAsync(PodcastMetadata podcast, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FeedPublishResult
            {
                Success = true, FeedUrl = FeedUrlConst, EpisodesPublished = 0, PublishedAtUtc = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc),
            });
    }

    // ---- stub orchestrator: captures the assembled Collection and drives the publisher ----
    private sealed class StubOrchestrator : IPodcastOrchestrator
    {
        private readonly RecordingPublisher _publisher;

        public StubOrchestrator(RecordingPublisher publisher) => _publisher = publisher;

        public Collection? Captured { get; private set; }

        public async Task<PodcastResult> GeneratePodcastAsync(
            Collection collection,
            IProgress<PodcastProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Captured = collection;
            var episodes = collection.Items.Select(i => new EpisodeSource
            {
                Title = i.Title,
                Description = i.Url,
                LocalAudioFilePath = $"/tmp/{i.Id}.m4a",
                Duration = TimeSpan.FromMinutes(3),
                SourceUrl = i.Url,
            }).ToList();

            var metadata = new PodcastMetadata
            {
                Title = collection.Name,
                Description = "Scheduled brief",
                Author = "WireCopy",
                Language = "en-us",
                ImageUrl = "https://storage.example/cover.png",
            };

            var pub = await _publisher.PublishFeedAsync(metadata, episodes, null, cancellationToken);
            return PodcastResult.Successful(pub.FeedUrl, "/tmp/out.m4b", TimeSpan.FromMinutes(15), episodes.Count, 0, 4096);
        }

        public Task<PodcastTargets> ResolveTargetsAsync(Collection collection, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PodcastTargets { LocalFilePath = "/tmp/out.m4b", FeedUrl = RecordingPublisher.FeedUrlConst });

        public Task<CacheAnalysis> AnalyzeCacheStatusAsync(
            Collection collection, IProgress<ContentExtractionProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by the scheduled-run e2e");

        public string GetOutputFilePath(string collectionName) => $"/tmp/{collectionName}.m4b";
    }

    private sealed class InMemoryRunRepo : IScheduledRunRepository
    {
        public List<ScheduledRun> All { get; } = new();

        public Task AddAsync(ScheduledRun run, CancellationToken ct = default)
        {
            All.Add(run);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ScheduledRun run, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ScheduledRun>> GetActiveRunsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledRun>>(All.Where(r => r.Status is ScheduledRunStatus.Running or ScheduledRunStatus.Pending).ToList());

        public Task<ScheduledRun?> GetByOccurrenceKeyAsync(Guid recipeId, string key, CancellationToken ct = default) =>
            Task.FromResult(All.FirstOrDefault(r => r.RecipeId == recipeId && r.OccurrenceKey == key));

        public Task<IReadOnlyList<ScheduledRun>> GetUnacknowledgedFinishedRunsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledRun>>(All.Where(r => r.AcknowledgedAtUtc == null && r.FinishedAtUtc != null).ToList());
    }
}
