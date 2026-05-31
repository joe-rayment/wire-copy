// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Infrastructure.Podcast;
using Xunit;

namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
public class PodcastGenerationGateTests
{
    private static IPodcastGenerationGate NewGate() => new PodcastGenerationGate();

    [Fact]
    public void TryAcquire_WhenFree_Succeeds_AndBlocksASecondAcquire()
    {
        var gate = NewGate();

        gate.TryAcquire(out var lease1).Should().BeTrue();
        lease1.Should().NotBeNull();
        gate.IsHeld.Should().BeTrue();

        // A second producer (e.g. the scheduler while a manual run holds it, or
        // vice-versa) is refused — no second GeneratePodcastAsync is admitted.
        gate.TryAcquire(out var lease2).Should().BeFalse();
        lease2.Should().BeNull();
    }

    [Fact]
    public void DisposingLease_ReleasesGate_SoALaterRunCanAcquire()
    {
        var gate = NewGate();

        gate.TryAcquire(out var lease).Should().BeTrue();
        lease!.Dispose();

        gate.IsHeld.Should().BeFalse();
        gate.TryAcquire(out var lease2).Should().BeTrue("the gate is free after the prior lease released");
        lease2.Should().NotBeNull();
    }

    [Fact]
    public void Lease_DoubleDispose_IsIdempotent()
    {
        var gate = NewGate();
        gate.TryAcquire(out var lease).Should().BeTrue();

        lease!.Dispose();
        lease.Dispose(); // must NOT over-release the semaphore

        // Exactly one slot is free: one acquire succeeds, the next is refused.
        gate.TryAcquire(out _).Should().BeTrue();
        gate.TryAcquire(out _).Should().BeFalse();
    }

    [Fact]
    public async Task AcquireAsync_BlocksWhileHeld_ThenCompletesOnRelease()
    {
        var gate = NewGate();
        gate.TryAcquire(out var held).Should().BeTrue();

        var pending = gate.AcquireAsync();
        pending.IsCompleted.Should().BeFalse("the gate is held, so the async acquire must wait");

        held!.Dispose();

        var lease = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        lease.Should().NotBeNull();
        gate.IsHeld.Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_HonoursCancellation()
    {
        var gate = NewGate();
        gate.TryAcquire(out _).Should().BeTrue();

        using var cts = new CancellationTokenSource();
        var pending = gate.AcquireAsync(cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<OperationCanceledException>();
    }
}
