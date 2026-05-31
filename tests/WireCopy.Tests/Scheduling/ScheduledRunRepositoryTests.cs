// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Persistence;
using WireCopy.Persistence.Repositories;
using Xunit;

namespace WireCopy.Tests.Scheduling;

[Trait("Category", "Unit")]
public class ScheduledRunRepositoryTests : TestDatabaseFixture
{
    private ScheduledRunRepository Repo(AppDbContext ctx) => new(ctx);

    [Fact]
    public async Task RoundTrips_IncludingStepOutcomesJson()
    {
        var run = ScheduledRun.Start(Guid.NewGuid(), "Morning Brief", "2026-05-31@07:00");
        run.Finish(ScheduledRunStatus.Completed, itemCount: 5, targetLocalPath: "/out/brief.m4b",
            targetFeedUrl: "https://cdn/feed.xml", stepOutcomesJson: "[{\"step\":\"Business\",\"outcome\":\"Resolved\"}]");

        await Repo(DbContext).AddAsync(run);
        await DbContext.SaveChangesAsync();

        await using var read = CreateDbContext();
        var loaded = await read.Set<ScheduledRun>().FirstAsync(r => r.Id == run.Id);
        loaded.RecipeName.Should().Be("Morning Brief");
        loaded.OccurrenceKey.Should().Be("2026-05-31@07:00");
        loaded.Status.Should().Be(ScheduledRunStatus.Completed);
        loaded.ItemCount.Should().Be(5);
        loaded.StepOutcomesJson.Should().Contain("Resolved");
        loaded.FinishedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByOccurrenceKey_ReturnsTheDedupRow()
    {
        var recipeId = Guid.NewGuid();
        await Repo(DbContext).AddAsync(ScheduledRun.Start(recipeId, "R", "2026-05-31@07:00"));
        await DbContext.SaveChangesAsync();

        await using var read = CreateDbContext();
        (await Repo(read).GetByOccurrenceKeyAsync(recipeId, "2026-05-31@07:00")).Should().NotBeNull();
        (await Repo(read).GetByOccurrenceKeyAsync(recipeId, "2026-06-01@07:00")).Should().BeNull();
        (await Repo(read).GetByOccurrenceKeyAsync(Guid.NewGuid(), "2026-05-31@07:00")).Should().BeNull();
    }

    [Fact]
    public async Task OrphanSweep_MarksStrandedRunningRunInterrupted()
    {
        var run = ScheduledRun.Start(Guid.NewGuid(), "R", "2026-05-31@07:00");
        await Repo(DbContext).AddAsync(run);
        await DbContext.SaveChangesAsync();

        // Simulate the startup sweep: active Running runs -> Interrupted.
        await using (var sweep = CreateDbContext())
        {
            var active = await Repo(sweep).GetActiveRunsAsync();
            active.Should().ContainSingle();
            active[0].MarkInterrupted("app restarted");
            await sweep.SaveChangesAsync();
        }

        await using var read = CreateDbContext();
        var loaded = await read.Set<ScheduledRun>().FirstAsync(r => r.Id == run.Id);
        loaded.Status.Should().Be(ScheduledRunStatus.Interrupted);
        (await Repo(read).GetActiveRunsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetUnacknowledgedFinishedRuns_ReturnsFailedUntilAcknowledged()
    {
        var run = ScheduledRun.Start(Guid.NewGuid(), "R", "2026-05-31@07:00");
        run.Finish(ScheduledRunStatus.Failed, itemCount: 0, errorClass: "NoContentResolved", errorMessage: "0 articles");
        await Repo(DbContext).AddAsync(run);
        await DbContext.SaveChangesAsync();

        await using (var ctx = CreateDbContext())
        {
            (await Repo(ctx).GetUnacknowledgedFinishedRunsAsync()).Should().ContainSingle();
            var r = await ctx.Set<ScheduledRun>().FirstAsync();
            r.Acknowledge();
            await ctx.SaveChangesAsync();
        }

        await using var read = CreateDbContext();
        (await Repo(read).GetUnacknowledgedFinishedRunsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_AppliesViaMigrateAsync_AndScheduledRunsTableWorks()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;

        await using var ctx = new AppDbContext(options);
        await ctx.Database.MigrateAsync(); // applies AddScheduledRunsTable (append-only)

        ctx.Set<ScheduledRun>().Add(ScheduledRun.Start(Guid.NewGuid(), "R", "2026-05-31@07:00"));
        await ctx.SaveChangesAsync();
        (await ctx.Set<ScheduledRun>().CountAsync()).Should().Be(1);
    }
}
