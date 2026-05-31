// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.API;
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
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-frpl.19 (B15) — the host shutdown contract. Proves that when the host
/// stoppingToken is cancelled with a scheduled run in flight, the REAL pipeline
/// observes cancellation, finalizes the ScheduledRun to Interrupted (never left
/// Running), and releases the B0 gate — and that Program wires the ShutdownTimeout
/// that gives the scheduler the grace window to do so.
/// </summary>
[Trait("Category", "Unit")]
public class SchedulerShutdownContractTests
{
    private static readonly DateTimeOffset MondaySevenAm = new(2026, 6, 1, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Program_ConfiguresSchedulerShutdownTimeout()
    {
        using var host = Program.CreateBrowseHostBuilder().Build();

        var options = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        options.ShutdownTimeout.Should().Be(TimeSpan.FromSeconds(45));
        options.ShutdownTimeout.Should().Be(Program.SchedulerShutdownTimeout);
    }

    [Fact]
    public async Task RunInFlight_StoppingTokenCancelled_FinalizesInterrupted_AndReleasesGate()
    {
        var orchestrator = new BlockingOrchestrator();
        var h = new Harness(orchestrator);
        using var cts = new CancellationTokenSource();

        var tick = Task.Run(() => h.Scheduler.EvaluateOnceAsync(MondaySevenAm, cts.Token));

        // Wait until the run is genuinely in flight: a Running row exists, the gate
        // is held, and generation has started (the orchestrator entered its block).
        await orchestrator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Runs.All.Should().ContainSingle().Which.Status.Should().Be(ScheduledRunStatus.Running);
        h.Gate.IsHeld.Should().BeTrue("a run is generating");

        await cts.CancelAsync();
        await tick.WaitAsync(TimeSpan.FromSeconds(10));

        h.Runs.All.Should().ContainSingle().Which.Status.Should().Be(
            ScheduledRunStatus.Interrupted, "a cancelled in-flight run is finalized, never left Running");
        h.Gate.IsHeld.Should().BeFalse("the B0 gate is released on the cancellation path");
        h.Gate.TryAcquire(out _).Should().BeTrue("a subsequent run can acquire the freed gate");
    }

    [Fact]
    public async Task NonCancelledRun_StillCompletesNormally_AndReleasesGate()
    {
        var h = new Harness(new CompletingOrchestrator());
        using var cts = new CancellationTokenSource();

        await h.Scheduler.EvaluateOnceAsync(MondaySevenAm, cts.Token);

        h.Runs.All.Should().ContainSingle().Which.Status.Should().Be(ScheduledRunStatus.Completed);
        h.Gate.IsHeld.Should().BeFalse("the gate is released after a normal completion");
    }

    // ---- harness: REAL SchedulerHostedService + REAL RecipeRunPipeline + fakes ----
    private sealed class Harness
    {
        public FakeScheduleStore Store { get; } = new();
        public InMemoryRunRepo Runs { get; } = new();
        public PodcastGenerationGate Gate { get; } = new();
        public SchedulerHostedService Scheduler { get; }

        public Harness(IPodcastOrchestrator orchestrator)
        {
            Store.Recipes.Add(ScheduleRecipe.Create(
                "Brief",
                Cadence.Daily(new TimeOnly(7, 0)),
                new[] { RecipeStep.Create("https://x.example/", "x.example", "x", "Top", required: true) }));

            var loader = Substitute.For<IHeadlessSectionLoader>();
            loader.LoadLinksAndConfigAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new HeadlessSectionLoad
                {
                    Outcome = LoadOutcome.Ok,
                    Links = new List<LinkInfo>
                    {
                        new() { Url = "https://x.example/a", DisplayText = "A", Type = LinkType.Content, ImportanceScore = 50, ParentSelector = "div.top a" },
                    },
                    Config = new SiteHierarchyConfig
                    {
                        Domain = "x.example", UrlPattern = ".*",
                        Sections = new List<HierarchySection> { new() { Name = "Top", SortOrder = 0, ParentSelectors = new() { "div.top" } } },
                        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ModelVersion = "t",
                    },
                });

            var jobManager = Substitute.For<IPodcastBackgroundJobManager>();
            jobManager.HasActiveJob.Returns(false);

            var services = new ServiceCollection();
            services.AddLogging(); // the REAL pipeline needs ILogger<RecipeRunPipeline>
            services.AddSingleton<IScheduleStore>(Store);
            services.AddSingleton<IScheduledRunRepository>(Runs);
            services.AddSingleton<IPodcastGenerationGate>(Gate);
            services.AddSingleton<IUnitOfWork>(new FakeUnitOfWork());
            services.AddSingleton<IHeadlessSectionLoader>(loader);
            services.AddSingleton<ISectionResolver, SectionResolver>(); // REAL resolver
            services.AddSingleton(orchestrator);
            services.AddSingleton(jobManager);
            services.AddScoped<IRecipeRunPipeline, RecipeRunPipeline>(); // REAL pipeline
            var sp = services.BuildServiceProvider();

            Scheduler = new SchedulerHostedService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<SchedulerHostedService>.Instance,
                new FakeTimeProvider(MondaySevenAm));
        }
    }

    private sealed class BlockingOrchestrator : IPodcastOrchestrator
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PodcastResult> GeneratePodcastAsync(Collection collection, IProgress<PodcastProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken); // blocks until the host token cancels
            return PodcastResult.Successful(null, "/tmp/x.m4b", TimeSpan.FromMinutes(1), 1, 0, 1024);
        }

        public Task<PodcastTargets> ResolveTargetsAsync(Collection collection, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PodcastTargets { LocalFilePath = "/tmp/x.m4b" });

        public Task<CacheAnalysis> AnalyzeCacheStatusAsync(Collection collection, IProgress<ContentExtractionProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GetOutputFilePath(string collectionName) => $"/tmp/{collectionName}.m4b";
    }

    private sealed class CompletingOrchestrator : IPodcastOrchestrator
    {
        public Task<PodcastResult> GeneratePodcastAsync(Collection collection, IProgress<PodcastProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(PodcastResult.Successful("https://feed.example/rss.xml", "/tmp/x.m4b", TimeSpan.FromMinutes(1), 1, 0, 1024));

        public Task<PodcastTargets> ResolveTargetsAsync(Collection collection, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PodcastTargets { LocalFilePath = "/tmp/x.m4b", FeedUrl = "https://feed.example/rss.xml" });

        public Task<CacheAnalysis> AnalyzeCacheStatusAsync(Collection collection, IProgress<ContentExtractionProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GetOutputFilePath(string collectionName) => $"/tmp/{collectionName}.m4b";
    }

    private sealed class FakeScheduleStore : IScheduleStore
    {
        public List<ScheduleRecipe> Recipes { get; } = new();
        public Task<IReadOnlyList<ScheduleRecipe>> GetAllAsync() => Task.FromResult<IReadOnlyList<ScheduleRecipe>>(Recipes);
        public Task<ScheduleRecipe?> GetAsync(Guid id) => Task.FromResult(Recipes.FirstOrDefault(r => r.Id == id));
        public Task SaveAsync(ScheduleRecipe recipe) => Task.CompletedTask;
        public Task<bool> DeleteAsync(Guid id) => Task.FromResult(false);
        public Task UpdateRunStateAsync(Guid id, RecipeRunState runState) => Task.CompletedTask;
    }

    private sealed class InMemoryRunRepo : IScheduledRunRepository
    {
        public List<ScheduledRun> All { get; } = new();
        public Task AddAsync(ScheduledRun run, CancellationToken ct = default) { All.Add(run); return Task.CompletedTask; }
        public Task UpdateAsync(ScheduledRun run, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ScheduledRun>> GetActiveRunsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledRun>>(All.Where(r => r.Status is ScheduledRunStatus.Running or ScheduledRunStatus.Pending).ToList());
        public Task<ScheduledRun?> GetByOccurrenceKeyAsync(Guid recipeId, string key, CancellationToken ct = default) =>
            Task.FromResult(All.FirstOrDefault(r => r.RecipeId == recipeId && r.OccurrenceKey == key));
        public Task<IReadOnlyList<ScheduledRun>> GetUnacknowledgedFinishedRunsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledRun>>(All.Where(r => r.AcknowledgedAtUtc == null && r.FinishedAtUtc != null).ToList());
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public bool HasActiveTransaction => false;
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task BeginTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CommitTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackTransactionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
