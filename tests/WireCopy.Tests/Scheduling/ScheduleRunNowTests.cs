// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-frpl.14 (B12a) — "run now" reuses the scheduler's gate + Running-row
/// admission protocol under a unique run-now occurrence key, and reports Busy
/// (doing nothing) when a generation is already in progress.
/// </summary>
[Trait("Category", "Unit")]
public class ScheduleRunNowTests
{
    private static ScheduleRecipe Recipe() =>
        ScheduleRecipe.Create("Brief", Cadence.Daily(new TimeOnly(7, 0)),
            new[] { RecipeStep.Create("https://x/", "x", "x", "Top", required: true) });

    private sealed class Harness
    {
        public InMemoryRunRepo Runs { get; } = new();
        public PodcastGenerationGate Gate { get; } = new();
        public FakePipeline Pipeline { get; } = new();

        public ScheduleRunNow Build()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IScheduledRunRepository>(Runs);
            services.AddSingleton<IRecipeRunPipeline>(Pipeline);
            services.AddSingleton<IUnitOfWork>(new FakeUnitOfWork());
            var sp = services.BuildServiceProvider();
            return new ScheduleRunNow(
                sp.GetRequiredService<IServiceScopeFactory>(),
                Gate,
                NullLogger<ScheduleRunNow>.Instance,
                new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));
        }
    }

    [Fact]
    public async Task RunAsync_AcquiresGate_WritesRunNowRow_RunsPipeline_ReleasesGate()
    {
        var h = new Harness();
        var recipe = Recipe();

        var outcome = await h.Build().RunAsync(recipe);

        outcome.Should().Be(RunNowOutcome.Started);
        h.Pipeline.Calls.Should().Be(1);
        var run = h.Runs.All.Should().ContainSingle().Subject;
        run.OccurrenceKey.Should().StartWith("run-now@", "a manual run uses a unique key so it never collides with a scheduled occurrence's dedup row");
        h.Gate.IsHeld.Should().BeFalse("the gate is released after the run");
    }

    [Fact]
    public async Task RunAsync_WhenGateHeld_ReturnsBusy_AndDoesNothing()
    {
        var h = new Harness();
        h.Gate.TryAcquire(out var lease).Should().BeTrue(); // a generation is already running

        var outcome = await h.Build().RunAsync(Recipe());

        outcome.Should().Be(RunNowOutcome.Busy);
        h.Pipeline.Calls.Should().Be(0);
        h.Runs.All.Should().BeEmpty("no row is written when the gate is busy");
        lease!.Dispose();
    }

    [Fact]
    public async Task TwoRunNows_ProduceDistinctOccurrenceKeys()
    {
        var h = new Harness();
        var recipe = Recipe();

        await h.Build().RunAsync(recipe);
        await h.Build().RunAsync(recipe);

        h.Runs.All.Select(r => r.OccurrenceKey).Distinct().Should().HaveCount(2);
    }

    private sealed class FakePipeline : IRecipeRunPipeline
    {
        public int Calls { get; private set; }
        public Task RunAsync(ScheduleRecipe recipe, ScheduledRun run, CancellationToken ct = default)
        {
            Calls++;
            run.Finish(ScheduledRunStatus.Completed, itemCount: 1);
            return Task.CompletedTask;
        }
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
