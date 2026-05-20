// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-g3uu — root-cause unit tests for the cost-gate-modal input
/// handler drop. The bug: ShowCacheAnalysisScreenAsync exited when its
/// analysisTask completed and left an orphaned <c>WaitForInputAsync</c>
/// still subscribed to <c>TerminalInputHandler._keyChannel</c>. The orphan
/// then dequeued the next screen's keys behind the cost-gate modal's back,
/// and the modal's own <c>WaitForInputAsync</c> blocked indefinitely on a
/// channel that had already been drained.
///
/// <para>
/// Fix: a <c>DrainPendingKeyAsync</c> helper cancels the linked CTS the
/// screen passed to <c>WaitForInputAsync</c> and awaits the task so the
/// inner channel read observes the cancellation and yields its slot to the
/// next caller. These tests pin the helper's contract.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class DrainPendingKeyTests
{
    [Fact]
    public async Task Drain_NullTask_Returns_WithoutThrowing()
    {
        using var cts = new CancellationTokenSource();

        // The screen may never have started a WaitForInputAsync (e.g. cache
        // analysis returned synchronously before the loop iterated even once).
        // The drain must be a no-op in that case — and crucially must NOT
        // cancel the CTS (the caller may want to reuse it).
        await PodcastConfirmationScreens.DrainPendingKeyAsync(pendingKeyTask: null, cts);

        cts.IsCancellationRequested.Should().BeFalse(
            "no pending task means there's nothing to drain — the helper must NOT cancel proactively");
    }

    [Fact]
    public async Task Drain_PendingTask_CancelsCtsAndAwaitsCancellation()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        // Simulate WaitForInputAsync that blocks until token cancellation.
        var orphan = Task.Run<NavigationCommand>(async () =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new NavigationCommand { Type = CommandType.NoOp };
        });

        var drainTask = PodcastConfirmationScreens.DrainPendingKeyAsync(orphan, cts);

        // The drain must complete (even though the orphan is blocked) because
        // it cancels the CTS internally.
        var winner = await Task.WhenAny(drainTask, Task.Delay(1000));
        winner.Should().Be(drainTask,
            "the helper must cancel the CTS to unblock the orphaned WaitForInputAsync — otherwise the screen would hang on exit");

        await drainTask;
        cts.IsCancellationRequested.Should().BeTrue(
            "the CTS must be cancelled so the orphan's internal channel read observes cancellation");
        orphan.Status.Should().BeOneOf(TaskStatus.Canceled, TaskStatus.RanToCompletion, TaskStatus.Faulted);
        orphan.IsCompleted.Should().BeTrue(
            "the orphan must have observed the cancellation, not still be running");
    }

    [Fact]
    public async Task Drain_AlreadyCompletedTask_ReturnsCleanly()
    {
        using var cts = new CancellationTokenSource();
        var completed = Task.FromResult(new NavigationCommand { Type = CommandType.NoOp });

        await PodcastConfirmationScreens.DrainPendingKeyAsync(completed, cts);

        // Cancellation still happens for consistency (the next caller won't
        // re-use this CTS anyway — it's a using var local to the screen).
        cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Drain_TaskThatFaults_DoesNotPropagateException()
    {
        using var cts = new CancellationTokenSource();
        var faulted = Task.FromException<NavigationCommand>(new InvalidOperationException("orphan blew up"));

        // The cache analysis screen is exiting; it has no business with this
        // key anyway. Swallow the exception so the caller can proceed to the
        // next screen without crashing.
        var act = async () => await PodcastConfirmationScreens.DrainPendingKeyAsync(faulted, cts);
        await act.Should().NotThrowAsync(
            "an orphaned task's exception is irrelevant to the caller — the screen is bailing out anyway");
    }

    [Fact]
    public async Task Drain_NullCts_Throws()
    {
        var act = async () => await PodcastConfirmationScreens.DrainPendingKeyAsync(
            Task.FromResult(new NavigationCommand { Type = CommandType.NoOp }),
            keyCts: null!);

        await act.Should().ThrowAsync<ArgumentNullException>(
            "a null CTS is a programming bug — fail loudly");
    }

    /// <summary>
    /// Simulates the call site's contract: a `using` CTS, a pending orphan,
    /// and an exception thrown mid-loop. The fix moves the Drain into a
    /// finally block so this path correctly cancels and awaits the orphan
    /// even when the screen exits via an exception. Without the finally,
    /// `using` would dispose the CTS but the orphan would stay subscribed
    /// to the channel.
    /// </summary>
    [Fact]
    public async Task Drain_InFinally_CancelsOrphan_EvenWhenLoopThrows()
    {
        Task<NavigationCommand>? capturedOrphan = null;
        CancellationTokenSource? capturedCts = null;

        async Task SimulatedScreen()
        {
            using var keyCts = new CancellationTokenSource();
            Task<NavigationCommand>? pendingKeyTask = null;
            capturedCts = keyCts;

            try
            {
                pendingKeyTask = Task.Run<NavigationCommand>(async () =>
                {
                    await Task.Delay(Timeout.Infinite, keyCts.Token).ConfigureAwait(false);
                    return new NavigationCommand { Type = CommandType.NoOp };
                });
                capturedOrphan = pendingKeyTask;

                // Simulate the loop body throwing — the exact path that
                // would have leaked the orphan before this fix.
                throw new InvalidOperationException("simulated loop body failure");
            }
            finally
            {
                await PodcastConfirmationScreens.DrainPendingKeyAsync(pendingKeyTask, keyCts).ConfigureAwait(false);
            }
        }

        var act = async () => await SimulatedScreen();
        await act.Should().ThrowAsync<InvalidOperationException>(
            "the loop's exception must still propagate — the drain is cleanup, not suppression");

        // Critical: the orphan must have observed cancellation. This is the
        // bug-shape pin: a `using var` alone would NOT cancel the orphan;
        // only the finally-drain does.
        capturedOrphan.Should().NotBeNull();
        capturedOrphan!.IsCompleted.Should().BeTrue(
            "the finally block drained the orphan even though the try block threw");
        capturedCts.Should().NotBeNull();
        capturedCts!.IsCancellationRequested.Should().BeTrue(
            "the drain helper's CancelAsync ran inside the finally");
    }
}
