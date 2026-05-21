// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Entities.Podcast;
using WireCopy.Domain.Enums.Podcast;
using WireCopy.Persistence.Repositories;
using Xunit;

namespace WireCopy.Tests.Podcast.Persistence;

[Trait("Category", "Unit")]
public class PodcastJobRepositoryTests : TestDatabaseFixture
{
    private readonly PodcastJobRepository _sut;

    public PodcastJobRepositoryTests()
    {
        _sut = new PodcastJobRepository(DbContext);
    }

    [Fact]
    public async Task GetByIdAsync_Missing_ReturnsNull()
    {
        (await _sut.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_RoundTrip_PreservesAllFields()
    {
        var job = PodcastJob.Start(Guid.NewGuid(), "Reading List", "/tmp/x.m4a", "https://x/feed.xml");
        job.RecordProgress(PodcastJobPhase.Synthesis, "{\"chunk\":1}");

        await _sut.AddAsync(job);
        await DbContext.SaveChangesAsync();

        // Reload through a fresh DbContext to defeat the change tracker
        using var fresh = CreateDbContext();
        var freshRepo = new PodcastJobRepository(fresh);
        var loaded = await freshRepo.GetByIdAsync(job.Id);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(job.Id);
        loaded.CollectionTitle.Should().Be("Reading List");
        loaded.Status.Should().Be(PodcastJobStatus.Running);
        loaded.Phase.Should().Be(PodcastJobPhase.Synthesis);
        loaded.LastProgressJson.Should().Be("{\"chunk\":1}");
        loaded.TargetLocalPath.Should().Be("/tmp/x.m4a");
        loaded.TargetFeedUrl.Should().Be("https://x/feed.xml");
    }

    [Fact]
    public async Task GetActiveJobsAsync_OnlyReturnsRunningOrPending()
    {
        var running = PodcastJob.Start(Guid.NewGuid(), "Running List");
        var done = PodcastJob.Start(Guid.NewGuid(), "Done List");
        done.Finish(PodcastJobStatus.FullSuccess);
        var interrupted = PodcastJob.Start(Guid.NewGuid(), "Interrupted List");
        interrupted.MarkInterrupted("Test");

        await _sut.AddAsync(running);
        await _sut.AddAsync(done);
        await _sut.AddAsync(interrupted);
        await DbContext.SaveChangesAsync();

        using var fresh = CreateDbContext();
        var freshRepo = new PodcastJobRepository(fresh);
        var active = await freshRepo.GetActiveJobsAsync();

        active.Should().HaveCount(1);
        active[0].Id.Should().Be(running.Id);
    }

    [Fact]
    public async Task GetUnacknowledgedFinishedJobsAsync_ExcludesRunningAndAcknowledged()
    {
        var running = PodcastJob.Start(Guid.NewGuid(), "Still Going");

        var unread = PodcastJob.Start(Guid.NewGuid(), "Unread");
        unread.Finish(PodcastJobStatus.FullSuccess);

        var seen = PodcastJob.Start(Guid.NewGuid(), "Seen");
        seen.Finish(PodcastJobStatus.PartialSuccess);
        seen.Acknowledge();

        await _sut.AddAsync(running);
        await _sut.AddAsync(unread);
        await _sut.AddAsync(seen);
        await DbContext.SaveChangesAsync();

        using var fresh = CreateDbContext();
        var freshRepo = new PodcastJobRepository(fresh);
        var rows = await freshRepo.GetUnacknowledgedFinishedJobsAsync();

        rows.Should().ContainSingle().Which.Id.Should().Be(unread.Id);
    }

    [Fact]
    public async Task UpdateAsync_RoundTripsViaChangeTracker()
    {
        var job = PodcastJob.Start(Guid.NewGuid(), "Reading List");
        await _sut.AddAsync(job);
        await DbContext.SaveChangesAsync();

        // Mutate the tracked entity, save again, reload from a fresh context.
        job.RecordProgress(PodcastJobPhase.Publish, "{\"finalizing\":true}");
        await _sut.UpdateAsync(job);
        await DbContext.SaveChangesAsync();

        using var fresh = CreateDbContext();
        var freshRepo = new PodcastJobRepository(fresh);
        var loaded = await freshRepo.GetByIdAsync(job.Id);

        loaded!.Phase.Should().Be(PodcastJobPhase.Publish);
        loaded.LastProgressJson.Should().Be("{\"finalizing\":true}");
    }
}
