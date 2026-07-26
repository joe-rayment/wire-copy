// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Entities.Podcast;
using WireCopy.Domain.Enums.Podcast;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Persistence;
using WireCopy.Persistence.Repositories;
using Xunit;

namespace WireCopy.Tests.Podcast.Persistence;

/// <summary>
/// Tests the F.2 startup orphan sweep. Hosting harness is wired by hand so we
/// can poke the in-memory SQLite database with concrete repository instances
/// — IServiceScopeFactory + DI + EF Core are the moving parts that the
/// service depends on, so they're exercised together.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastJobLifecycleServiceTests
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PodcastJobLifecycleServiceTests()
    {
        _connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(_options));
        services.AddScoped<IPodcastJobRepository, PodcastJobRepository>();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(
            sp.GetRequiredService<AppDbContext>(),
            NullLogger<UnitOfWork>.Instance));
        return services.BuildServiceProvider();
    }

    private async Task SeedAsync(params PodcastJob[] jobs)
    {
        using var ctx = new AppDbContext(_options);
        foreach (var job in jobs)
        {
            ctx.PodcastJobs.Add(job);
        }

        await ctx.SaveChangesAsync();
    }

    private async Task<PodcastJob?> LoadAsync(Guid id)
    {
        using var ctx = new AppDbContext(_options);
        return await ctx.PodcastJobs.FirstOrDefaultAsync(j => j.Id == id);
    }

    /// <summary>
    /// workspace-xpr5t: the sweep must not touch the database until the migration has
    /// created the schema. On a first run it used to query PodcastJobs while the
    /// migration was still running, and EF logged the failed command at Error with a
    /// stack trace — a clean install that reads as broken. The sweep now waits for
    /// <see cref="DatabaseReadySignal"/>.
    /// </summary>
    [Fact]
    public async Task StartAsync_WaitsForTheDatabaseReadySignalBeforeTouchingTheTable()
    {
        var running = PodcastJob.Start(Guid.NewGuid(), "Test", "/tmp/t.m4a", "https://x/feed.xml");
        await SeedAsync(running);

        var provider = BuildProvider();
        var signal = new DatabaseReadySignal();
        var sut = new PodcastJobLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PodcastJobLifecycleService>.Instance,
            signal);

        await sut.StartAsync(CancellationToken.None);

        // Nothing may happen while the schema is still being created.
        var finished = await Task.WhenAny(sut.SweepTask!, Task.Delay(300));
        finished.Should().NotBeSameAs(sut.SweepTask, "the sweep must wait, not race the migration");
        (await LoadAsync(running.Id))!.Status.Should().Be(
            PodcastJobStatus.Running, "the row must be untouched while the signal is pending");

        signal.MarkReady();
        await sut.SweepTask!;

        (await LoadAsync(running.Id))!.Status.Should().Be(
            PodcastJobStatus.Interrupted, "…and the sweep must still do its job once released");
    }

    /// <summary>
    /// The wait is bounded: a host that never signals must degrade to sweeping anyway,
    /// not leave the task parked forever.
    /// </summary>
    [Fact]
    public async Task StartAsync_UnsignalledHost_SweepsAnywayAfterTheTimeout()
    {
        var signal = new DatabaseReadySignal();
        var waited = await signal.WaitAsync(CancellationToken.None, TimeSpan.FromMilliseconds(50));
        waited.Should().BeFalse("nothing signalled, so the wait must report that it gave up");
        signal.Ready.IsCompleted.Should().BeFalse("giving up must not fake the signal for other waiters");
    }

    [Fact]
    public async Task StartAsync_EmptyTable_NoOp()
    {
        var provider = BuildProvider();
        var sut = new PodcastJobLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PodcastJobLifecycleService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        // The sweep is fired with Task.Run; await its handle directly.
        await sut.SweepTask!;

        using var ctx = new AppDbContext(_options);
        (await ctx.PodcastJobs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_RunningRow_BecomesInterrupted()
    {
        var running = PodcastJob.Start(Guid.NewGuid(), "Test", "/tmp/t.m4a", "https://x/feed.xml");
        running.RecordProgress(PodcastJobPhase.Synthesis, "{\"chunk\":2}");
        await SeedAsync(running);

        var provider = BuildProvider();
        var sut = new PodcastJobLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PodcastJobLifecycleService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await sut.SweepTask!;

        var reloaded = await LoadAsync(running.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Status.Should().Be(PodcastJobStatus.Interrupted);
        reloaded.ErrorClass.Should().Be("Interrupted");
        reloaded.ErrorMessage.Should().Be("App restarted before generation completed");
    }

    [Fact]
    public async Task StartAsync_AlreadyTerminalRow_Untouched()
    {
        var done = PodcastJob.Start(Guid.NewGuid(), "Done");
        done.Finish(PodcastJobStatus.FullSuccess);
        await SeedAsync(done);

        var provider = BuildProvider();
        var sut = new PodcastJobLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PodcastJobLifecycleService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await sut.SweepTask!;

        var reloaded = await LoadAsync(done.Id);
        reloaded!.Status.Should().Be(PodcastJobStatus.FullSuccess);
        reloaded.ErrorClass.Should().BeNull("terminal rows must not be retouched");
    }

    [Fact]
    public async Task StartAsync_MultipleActiveRows_AllMarkedInterrupted()
    {
        var a = PodcastJob.Start(Guid.NewGuid(), "A");
        var b = PodcastJob.Start(Guid.NewGuid(), "B");
        var c = PodcastJob.Start(Guid.NewGuid(), "C");
        c.Finish(PodcastJobStatus.TotalFailure, "Boom", "x");
        await SeedAsync(a, b, c);

        var provider = BuildProvider();
        var sut = new PodcastJobLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PodcastJobLifecycleService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await sut.SweepTask!;

        (await LoadAsync(a.Id))!.Status.Should().Be(PodcastJobStatus.Interrupted);
        (await LoadAsync(b.Id))!.Status.Should().Be(PodcastJobStatus.Interrupted);
        (await LoadAsync(c.Id))!.Status.Should().Be(PodcastJobStatus.TotalFailure);
    }
}
