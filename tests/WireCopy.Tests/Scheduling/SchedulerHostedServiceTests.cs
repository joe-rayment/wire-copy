// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

[Trait("Category", "Unit")]
public class SchedulerHostedServiceTests
{
    private static readonly DateTimeOffset MondaySevenAm = new(2026, 6, 1, 7, 0, 0, TimeSpan.Zero);

    private static RecipeStep Step() => RecipeStep.Create("https://x.com/", "x.com", "^x$", "Top", required: true);

    private static ScheduleRecipe DailyRecipe(string name = "Brief") =>
        ScheduleRecipe.Create(name, Cadence.Daily(new TimeOnly(7, 0)), new[] { Step() });

    private sealed class Harness
    {
        public FakeScheduleStore Store { get; } = new();
        public InMemoryRunRepo Runs { get; } = new();
        public IPodcastGenerationGate Gate { get; } = new PodcastGenerationGate();
        public FakePipeline Pipeline { get; } = new();
        public SchedulerHostedService Build(DateTimeOffset now)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IScheduleStore>(Store);
            services.AddSingleton<IScheduledRunRepository>(Runs);
            services.AddSingleton(Gate);
            services.AddSingleton<IRecipeRunPipeline>(Pipeline);
            services.AddSingleton<IUnitOfWork>(new FakeUnitOfWork());
            var sp = services.BuildServiceProvider();
            return new SchedulerHostedService(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SchedulerHostedService>.Instance, new FakeTimeProvider(now));
        }
    }

    [Fact]
    public async Task DueSlot_FiresExactlyOncePerOccurrence()
    {
        var h = new Harness();
        h.Store.Recipes.Add(DailyRecipe());
        var scheduler = h.Build(MondaySevenAm);

        await scheduler.EvaluateOnceAsync(MondaySevenAm, CancellationToken.None);
        await scheduler.EvaluateOnceAsync(MondaySevenAm, CancellationToken.None); // second tick, same occurrence

        h.Pipeline.Calls.Should().Be(1, "the EF run row dedups the second tick");
        h.Runs.All.Should().ContainSingle();
    }

    [Fact]
    public async Task Dedup_IsDrivenByEfRow_NotJsonCache()
    {
        var h = new Harness();
        var recipe = DailyRecipe();
        // JSON cache CLAIMS today's occurrence already ran, but NO EF row exists.
        recipe.RecordRun(DateOnly.FromDateTime(MondaySevenAm.DateTime), "2026-06-01@07:00", RunStatus.Success);
        h.Store.Recipes.Add(recipe);

        await h.Build(MondaySevenAm).EvaluateOnceAsync(MondaySevenAm, CancellationToken.None);

        h.Pipeline.Calls.Should().Be(1, "the EF row is authoritative; a stale JSON cache must not skip a due run");
    }

    [Fact]
    public async Task GateHeld_Defers_ThenFiresOnceReleased()
    {
        var h = new Harness();
        h.Store.Recipes.Add(DailyRecipe());
        var scheduler = h.Build(MondaySevenAm);

        h.Gate.TryAcquire(out var lease).Should().BeTrue(); // a manual run holds the gate
        await scheduler.EvaluateOnceAsync(MondaySevenAm, CancellationToken.None);
        h.Pipeline.Calls.Should().Be(0, "deferred while the gate is held");
        h.Runs.All.Should().BeEmpty("no row written on defer, so it stays due");

        lease!.Dispose();
        await scheduler.EvaluateOnceAsync(MondaySevenAm, CancellationToken.None);
        h.Pipeline.Calls.Should().Be(1, "fires on the next tick once the gate is free");
    }

    [Fact]
    public async Task TwoRecipesDueInOneTick_BothFire()
    {
        var h = new Harness();
        h.Store.Recipes.Add(DailyRecipe("A"));
        h.Store.Recipes.Add(DailyRecipe("B"));

        await h.Build(MondaySevenAm).EvaluateOnceAsync(MondaySevenAm, CancellationToken.None);

        h.Pipeline.Calls.Should().Be(2, "the gate serializes them but both run in the tick");
    }

    [Fact]
    public async Task RecipeThatThrows_DoesNotKillTheLoop()
    {
        var h = new Harness();
        h.Pipeline.ThrowForRecipe = "A";
        h.Store.Recipes.Add(DailyRecipe("A"));
        h.Store.Recipes.Add(DailyRecipe("B"));

        await h.Build(MondaySevenAm).EvaluateOnceAsync(MondaySevenAm, CancellationToken.None);

        h.Pipeline.RanRecipes.Should().Contain("B", "a failing recipe must not stop later recipes");
    }

    [Fact]
    public async Task MissedPastGrace_RecordsSkipped_AndDoesNotGenerate()
    {
        var h = new Harness();
        // Slot 07:00, grace 30m, now 09:00 → missed past grace.
        var recipe = ScheduleRecipe.Create("M",
            Cadence.Create(new[] { DayOfWeek.Monday }, new TimeOnly(7, 0), TimeSpan.FromMinutes(30)),
            new[] { Step() });
        h.Store.Recipes.Add(recipe);

        await h.Build(MondaySevenAm).EvaluateOnceAsync(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), CancellationToken.None);

        h.Pipeline.Calls.Should().Be(0);
        h.Runs.All.Should().ContainSingle().Which.Status.Should().Be(ScheduledRunStatus.Skipped);
    }

    [Fact]
    public async Task CancelledStoppingToken_DoesNotGenerate()
    {
        var h = new Harness();
        h.Store.Recipes.Add(DailyRecipe());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // A cancelled host token tears the tick down before any generation —
        // ExecuteAsync swallows the resulting OperationCanceledException.
        var scheduler = h.Build(MondaySevenAm);
        try
        {
            await scheduler.EvaluateOnceAsync(MondaySevenAm, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected on a pre-cancelled token
        }

        h.Pipeline.Calls.Should().Be(0, "no run is started under a cancelled host token");
    }

    // ---- fakes ----
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

    private sealed class FakePipeline : IRecipeRunPipeline
    {
        public int Calls { get; private set; }
        public List<string> RanRecipes { get; } = new();
        public string? ThrowForRecipe { get; set; }

        public Task RunAsync(ScheduleRecipe recipe, ScheduledRun run, CancellationToken ct = default)
        {
            Calls++;
            RanRecipes.Add(recipe.Name);
            ct.ThrowIfCancellationRequested();
            if (ThrowForRecipe == recipe.Name)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        }
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
