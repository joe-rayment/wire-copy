// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.Extensions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class PageAccessQueueTests : IDisposable
{
    private readonly IBrowserSession _browserSession;
    private readonly PageAccessQueue _queue;
    private readonly IPage _page;

    public PageAccessQueueTests()
    {
        _browserSession = Substitute.For<IBrowserSession>();
        _browserSession.IsBrowserAvailable.Returns(true);
        _page = Substitute.For<IPage>();
        _browserSession.GetOrCreatePageAsync().Returns(Task.FromResult(_page));
        _queue = new PageAccessQueue(_browserSession, NullLogger<PageAccessQueue>.Instance);
    }

    public void Dispose()
    {
        _queue.Dispose();
    }

    #region Foreground Acquisition

    [Fact]
    public async Task Foreground_AcquiresPageImmediately()
    {
        using var lease = await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);

        lease.Page.Should().BeSameAs(_page);
    }

    [Fact]
    public async Task Foreground_CreatesPageViaSession()
    {
        using var lease = await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);

        await _browserSession.Received(1).GetOrCreatePageAsync();
    }

    [Fact]
    public async Task Foreground_ReleasesLock_OnDispose()
    {
        var lease1 = await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);
        lease1.Dispose();

        // Should be able to acquire again without blocking
        using var lease2 = await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);

        lease2.Page.Should().BeSameAs(_page);
    }

    [Fact]
    public async Task Foreground_DoubleDispose_Safe()
    {
        var lease = await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);
        lease.Dispose();
        lease.Dispose(); // Should not throw
    }

    #endregion

    #region Background Acquisition

    [Fact]
    public async Task Background_AcquiresPageWhenFree()
    {
        using var lease = await _queue.AcquireAsync(
            PageAccessPriority.Background, CancellationToken.None);

        lease.Page.Should().BeSameAs(_page);
    }

    [Fact]
    public async Task Background_SetsIsBackgroundActive()
    {
        _queue.IsBackgroundActive.Should().BeFalse();

        var lease = await _queue.AcquireAsync(
            PageAccessPriority.Background, CancellationToken.None);

        _queue.IsBackgroundActive.Should().BeTrue();

        lease.Dispose();

        _queue.IsBackgroundActive.Should().BeFalse();
    }

    #endregion

    #region Priority Serialization

    [Fact]
    public async Task Foreground_WaitsForBackgroundToRelease()
    {
        // Background acquires first
        var bgLease = await _queue.AcquireAsync(
            PageAccessPriority.Background, CancellationToken.None);

        // Foreground should block until background releases
        var fgTask = _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);

        // Give foreground task a moment to start waiting
        await Task.Delay(50);
        fgTask.IsCompleted.Should().BeFalse("foreground should block while background holds lock");

        // Background releases
        bgLease.Dispose();

        // Foreground should complete quickly
        using var fgLease = await fgTask.WaitAsync(TimeSpan.FromSeconds(5));
        fgLease.Page.Should().BeSameAs(_page);
    }

    [Fact]
    public async Task Background_WaitsForForegroundToRelease()
    {
        // Foreground acquires first
        var fgLease = await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);

        // Background should block
        var bgTask = _queue.AcquireAsync(
            PageAccessPriority.Background, CancellationToken.None);

        await Task.Delay(50);
        bgTask.IsCompleted.Should().BeFalse("background should block while foreground holds lock");

        // Foreground releases
        fgLease.Dispose();

        // Background should complete
        using var bgLease = await bgTask.WaitAsync(TimeSpan.FromSeconds(5));
        bgLease.Page.Should().BeSameAs(_page);
    }

    [Fact]
    public async Task Sequential_Acquisition_Works()
    {
        for (var i = 0; i < 5; i++)
        {
            var priority = i % 2 == 0 ? PageAccessPriority.Foreground : PageAccessPriority.Background;
            using var lease = await _queue.AcquireAsync(priority, CancellationToken.None);
            lease.Page.Should().BeSameAs(_page);
        }
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task Foreground_RespectsCancellation()
    {
        // Hold the lock
        var bgLease = await _queue.AcquireAsync(
            PageAccessPriority.Background, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await _queue.AcquireAsync(
            PageAccessPriority.Foreground, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        bgLease.Dispose();
    }

    [Fact]
    public async Task Background_RespectsCancellation()
    {
        // Hold the lock
        var fgLease = await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await _queue.AcquireAsync(
            PageAccessPriority.Background, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fgLease.Dispose();
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task Foreground_ReleasesLock_WhenPageCreationFails()
    {
        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("Page creation failed"));

        var act = async () => await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);

        await act.Should().ThrowAsync<PlaywrightException>();

        // Lock should be released - can acquire again. (Configure() re-specifies the
        // parameterless method without invoking the throw configured above.)
        _browserSession.Configure().GetOrCreatePageAsync().Returns(Task.FromResult(_page));
        using var lease = await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);
        lease.Page.Should().BeSameAs(_page);
    }

    [Fact]
    public async Task Background_ReleasesLock_WhenPageCreationFails()
    {
        _browserSession.GetOrCreatePageAsync()
            .Returns<IPage>(_ => throw new PlaywrightException("Page creation failed"));

        var act = async () => await _queue.AcquireAsync(
            PageAccessPriority.Background, CancellationToken.None);

        await act.Should().ThrowAsync<PlaywrightException>();

        _queue.IsBackgroundActive.Should().BeFalse("background flag should reset on failure");

        // Lock should be released. (Configure() re-specifies the parameterless
        // method without invoking the throw configured above.)
        _browserSession.Configure().GetOrCreatePageAsync().Returns(Task.FromResult(_page));
        using var lease = await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);
        lease.Page.Should().BeSameAs(_page);
    }

    [Fact]
    public void Disposed_Queue_ThrowsObjectDisposed()
    {
        _queue.Dispose();

        var act = async () => await _queue.AcquireAsync(
            PageAccessPriority.Foreground, CancellationToken.None);

        act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        _queue.Dispose();
        _queue.Dispose(); // Should not throw
    }

    #endregion
}
