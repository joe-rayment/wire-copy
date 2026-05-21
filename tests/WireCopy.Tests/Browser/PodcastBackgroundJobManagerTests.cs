// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Unit tests for <see cref="PodcastBackgroundJobManager"/> — the singleton
/// that owns the live podcast generation task so the modal can detach and
/// reattach without restarting work (workspace-vkhr Phase D).
/// </summary>
[Trait("Category", "Unit")]
public class PodcastBackgroundJobManagerTests
{
    private static Collection BuildCollection() => Collection.Create("Test Collection");

    private static PodcastProgress BuildProgress(int percent = 42) => new()
    {
        Phase = PodcastPhase.GeneratingAudio,
        PercentComplete = percent,
        CurrentArticle = 2,
        TotalArticles = 4,
    };

    [Fact]
    public void StartJob_WhenNoActiveJob_StoresJob()
    {
        var manager = new PodcastBackgroundJobManager();
        var collection = BuildCollection();
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();

        manager.HasActiveJob.Should().BeFalse("manager starts empty");

        manager.StartJob(collection, targets: null, tcs.Task, cts);

        manager.HasActiveJob.Should().BeTrue();
        manager.Collection.Should().BeSameAs(collection);
        manager.CurrentJobTask.Should().BeSameAs(tcs.Task);
        manager.Targets.Should().BeNull("targets were not supplied at start");
    }

    [Fact]
    public void StartJob_WhenActiveJob_Throws()
    {
        var manager = new PodcastBackgroundJobManager();
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();
        manager.StartJob(BuildCollection(), targets: null, tcs.Task, cts);

        var act = () =>
        {
            var tcs2 = new TaskCompletionSource<PodcastResult>();
            using var cts2 = new CancellationTokenSource();
            manager.StartJob(BuildCollection(), targets: null, tcs2.Task, cts2);
        };

        act.Should().Throw<InvalidOperationException>(
            "the manager enforces a single-active-job invariant until Clear() runs");
    }

    [Fact]
    public void ReportProgress_UpdatesLastSnapshot_AndFiresEvent()
    {
        var manager = new PodcastBackgroundJobManager();
        PodcastProgress? captured = null;
        manager.ProgressUpdated += (_, p) => captured = p;

        var progress = BuildProgress(percent: 67);
        manager.ReportProgress(progress);

        manager.LastSnapshot.Should().BeSameAs(progress);
        captured.Should().BeSameAs(progress, "subscribers receive the same instance");
    }

    [Fact]
    public async Task Completion_FiresCompletedEvent_WithResult()
    {
        var manager = new PodcastBackgroundJobManager();
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();
        var completedTcs = new TaskCompletionSource<PodcastResult?>();
        manager.Completed += (_, r) => completedTcs.TrySetResult(r);

        manager.StartJob(BuildCollection(), targets: null, tcs.Task, cts);
        var expected = PodcastResult.Successful(
            feedUrl: null,
            localFilePath: "/tmp/out.m4b",
            totalDuration: TimeSpan.FromMinutes(7),
            articlesProcessed: 3,
            articlesFailed: 0,
            fileSizeBytes: 1024,
            articlesCached: 1,
            totalCost: 0.12m);
        tcs.SetResult(expected);

        var fired = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fired.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Completion_OnCancellation_FiresCompletedWithNull()
    {
        var manager = new PodcastBackgroundJobManager();
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();
        var completedTcs = new TaskCompletionSource<PodcastResult?>();
        manager.Completed += (_, r) => completedTcs.TrySetResult(r);

        manager.StartJob(BuildCollection(), targets: null, tcs.Task, cts);
        tcs.SetCanceled();

        var fired = await completedTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fired.Should().BeNull("cancelled / faulted runs surface as null per the manager contract");
    }

    [Fact]
    public void RequestCancellation_CancelsActiveCts()
    {
        var manager = new PodcastBackgroundJobManager();
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();
        manager.StartJob(BuildCollection(), targets: null, tcs.Task, cts);

        var result = manager.RequestCancellation();

        result.Should().BeTrue();
        cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void RequestCancellation_WithNoActiveJob_ReturnsFalse()
    {
        var manager = new PodcastBackgroundJobManager();

        manager.RequestCancellation().Should().BeFalse(
            "RequestCancellation no-ops when no job is registered");
    }

    [Fact]
    public void Clear_ResetsState_HasActiveJob_FalseAfter()
    {
        var manager = new PodcastBackgroundJobManager();
        var tcs = new TaskCompletionSource<PodcastResult>();
        using var cts = new CancellationTokenSource();
        manager.StartJob(BuildCollection(), targets: null, tcs.Task, cts);
        manager.ReportProgress(BuildProgress());

        manager.Clear();

        manager.HasActiveJob.Should().BeFalse();
        manager.LastSnapshot.Should().BeNull("Clear wipes the snapshot too");
        manager.Collection.Should().BeNull();
        manager.CurrentJobTask.Should().BeNull();
    }

    [Fact]
    public void Clear_AllowsSubsequentStartJob()
    {
        var manager = new PodcastBackgroundJobManager();
        var first = new TaskCompletionSource<PodcastResult>();
        using (var cts1 = new CancellationTokenSource())
        {
            manager.StartJob(BuildCollection(), targets: null, first.Task, cts1);
            manager.Clear();
        }

        var second = new TaskCompletionSource<PodcastResult>();
        using var cts2 = new CancellationTokenSource();
        var act = () => manager.StartJob(BuildCollection(), targets: null, second.Task, cts2);

        act.Should().NotThrow("a fresh job can start after Clear");
        manager.HasActiveJob.Should().BeTrue();
    }

    [Fact]
    public void Manager_ImplementsIPodcastBackgroundJobManager()
    {
        // Sanity check so an accidental interface rename trips the test.
        new PodcastBackgroundJobManager().Should().BeAssignableTo<IPodcastBackgroundJobManager>();
    }
}
