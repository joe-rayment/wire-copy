// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Entities.Podcast;
using WireCopy.Domain.Enums.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast.Persistence;

[Trait("Category", "Unit")]
public class PodcastJobTests
{
    [Fact]
    public void Start_CreatesRunningRow_WithNotStartedPhase()
    {
        var collectionId = Guid.NewGuid();
        var job = PodcastJob.Start(collectionId, "Reading List", "/tmp/foo.m4a", "https://x/feed.xml");

        job.Id.Should().NotBe(Guid.Empty);
        job.CollectionId.Should().Be(collectionId);
        job.CollectionTitle.Should().Be("Reading List");
        job.Status.Should().Be(PodcastJobStatus.Running);
        job.Phase.Should().Be(PodcastJobPhase.NotStarted);
        job.TargetLocalPath.Should().Be("/tmp/foo.m4a");
        job.TargetFeedUrl.Should().Be("https://x/feed.xml");
        job.StartedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        job.LastProgressAtUtc.Should().Be(job.StartedAtUtc);
        job.LastProgressJson.Should().BeNull();
        job.AcknowledgedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Start_EmptyTitle_Throws()
    {
        var act = () => PodcastJob.Start(Guid.NewGuid(), "  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecordProgress_UpdatesPhaseAndSnapshot()
    {
        var job = PodcastJob.Start(Guid.NewGuid(), "C");
        var before = job.LastProgressAtUtc;

        // Sleep briefly so the timestamp moves forward in a verifiable way.
        Thread.Sleep(5);
        job.RecordProgress(PodcastJobPhase.Synthesis, "{\"chunkIndex\":3}");

        job.Phase.Should().Be(PodcastJobPhase.Synthesis);
        job.LastProgressJson.Should().Be("{\"chunkIndex\":3}");
        job.LastProgressAtUtc.Should().BeAfter(before);
        job.Status.Should().Be(PodcastJobStatus.Running, "RecordProgress does not change terminal state");
    }

    [Fact]
    public void Finish_MovesToTerminalState_WithErrorClass()
    {
        var job = PodcastJob.Start(Guid.NewGuid(), "C");

        job.Finish(PodcastJobStatus.PartialSuccess, "SomeFailed", "1 of 3 articles failed");

        job.Status.Should().Be(PodcastJobStatus.PartialSuccess);
        job.Phase.Should().Be(PodcastJobPhase.Done);
        job.ErrorClass.Should().Be("SomeFailed");
        job.ErrorMessage.Should().Be("1 of 3 articles failed");
    }

    [Fact]
    public void Finish_RefusesRunningStatus()
    {
        var job = PodcastJob.Start(Guid.NewGuid(), "C");
        var act = () => job.Finish(PodcastJobStatus.Running);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Finish_TwiceThrows()
    {
        var job = PodcastJob.Start(Guid.NewGuid(), "C");
        job.Finish(PodcastJobStatus.FullSuccess);

        var act = () => job.Finish(PodcastJobStatus.TotalFailure);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkInterrupted_RunningRow_BecomesInterrupted()
    {
        var job = PodcastJob.Start(Guid.NewGuid(), "C");
        job.MarkInterrupted("App restarted");

        job.Status.Should().Be(PodcastJobStatus.Interrupted);
        job.ErrorClass.Should().Be("Interrupted");
        job.ErrorMessage.Should().Be("App restarted");
    }

    [Fact]
    public void MarkInterrupted_AlreadyTerminal_IsNoOp()
    {
        var job = PodcastJob.Start(Guid.NewGuid(), "C");
        job.Finish(PodcastJobStatus.FullSuccess);

        job.MarkInterrupted("App restarted");

        job.Status.Should().Be(PodcastJobStatus.FullSuccess);
        job.ErrorClass.Should().BeNull();
    }

    [Fact]
    public void Acknowledge_SetsTimestamp_OnceOnly()
    {
        var job = PodcastJob.Start(Guid.NewGuid(), "C");
        job.Finish(PodcastJobStatus.FullSuccess);

        job.Acknowledge();
        var first = job.AcknowledgedAtUtc;
        first.Should().NotBeNull();

        Thread.Sleep(5);
        job.Acknowledge();
        job.AcknowledgedAtUtc.Should().Be(first, "second acknowledge is idempotent");
    }
}
