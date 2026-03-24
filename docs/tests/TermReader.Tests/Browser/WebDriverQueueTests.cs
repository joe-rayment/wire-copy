// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenQA.Selenium;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class WebDriverQueueTests : IDisposable
{
    private readonly IBrowserSession _browserSession;
    private readonly WebDriverQueue _queue;
    private readonly IWebDriver _driver;

    public WebDriverQueueTests()
    {
        _browserSession = Substitute.For<IBrowserSession>();
        _browserSession.IsSeleniumAvailable.Returns(true);
        _driver = Substitute.For<IWebDriver>();
        _browserSession.GetOrCreateDriver(Arg.Any<bool>()).Returns(_driver);
        _queue = new WebDriverQueue(_browserSession, NullLogger<WebDriverQueue>.Instance);
    }

    public void Dispose()
    {
        _queue.Dispose();
    }

    #region Foreground Acquisition

    [Fact]
    public async Task Foreground_AcquiresDriverImmediately()
    {
        using var lease = await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);

        lease.Driver.Should().BeSameAs(_driver);
    }

    [Fact]
    public async Task Foreground_PassesHeadlessFlag()
    {
        using var lease = await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: false, CancellationToken.None);

        _browserSession.Received(1).GetOrCreateDriver(false);
    }

    [Fact]
    public async Task Foreground_ReleasesLock_OnDispose()
    {
        var lease1 = await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);
        lease1.Dispose();

        // Should be able to acquire again without blocking
        using var lease2 = await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);

        lease2.Driver.Should().BeSameAs(_driver);
    }

    [Fact]
    public async Task Foreground_DoubleDispose_Safe()
    {
        var lease = await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);
        lease.Dispose();
        lease.Dispose(); // Should not throw
    }

    #endregion

    #region Background Acquisition

    [Fact]
    public async Task Background_AcquiresDriverWhenFree()
    {
        using var lease = await _queue.AcquireAsync(
            WebDriverPriority.Background, headless: true, CancellationToken.None);

        lease.Driver.Should().BeSameAs(_driver);
    }

    [Fact]
    public async Task Background_SetsIsBackgroundActive()
    {
        _queue.IsBackgroundActive.Should().BeFalse();

        var lease = await _queue.AcquireAsync(
            WebDriverPriority.Background, headless: true, CancellationToken.None);

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
            WebDriverPriority.Background, headless: true, CancellationToken.None);

        // Foreground should block until background releases
        var fgTask = _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);

        // Give foreground task a moment to start waiting
        await Task.Delay(50);
        fgTask.IsCompleted.Should().BeFalse("foreground should block while background holds lock");

        // Background releases
        bgLease.Dispose();

        // Foreground should complete quickly
        using var fgLease = await fgTask.WaitAsync(TimeSpan.FromSeconds(5));
        fgLease.Driver.Should().BeSameAs(_driver);
    }

    [Fact]
    public async Task Background_WaitsForForegroundToRelease()
    {
        // Foreground acquires first
        var fgLease = await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);

        // Background should block
        var bgTask = _queue.AcquireAsync(
            WebDriverPriority.Background, headless: true, CancellationToken.None);

        await Task.Delay(50);
        bgTask.IsCompleted.Should().BeFalse("background should block while foreground holds lock");

        // Foreground releases
        fgLease.Dispose();

        // Background should complete
        using var bgLease = await bgTask.WaitAsync(TimeSpan.FromSeconds(5));
        bgLease.Driver.Should().BeSameAs(_driver);
    }

    [Fact]
    public async Task Sequential_Acquisition_Works()
    {
        for (var i = 0; i < 5; i++)
        {
            var priority = i % 2 == 0 ? WebDriverPriority.Foreground : WebDriverPriority.Background;
            using var lease = await _queue.AcquireAsync(priority, headless: true, CancellationToken.None);
            lease.Driver.Should().BeSameAs(_driver);
        }
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task Foreground_RespectsCancellation()
    {
        // Hold the lock
        var bgLease = await _queue.AcquireAsync(
            WebDriverPriority.Background, headless: true, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        bgLease.Dispose();
    }

    [Fact]
    public async Task Background_RespectsCancellation()
    {
        // Hold the lock
        var fgLease = await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await _queue.AcquireAsync(
            WebDriverPriority.Background, headless: true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fgLease.Dispose();
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task Foreground_ReleasesLock_WhenDriverCreationFails()
    {
        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new WebDriverException("Driver creation failed"));

        var act = async () => await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);

        await act.Should().ThrowAsync<WebDriverException>();

        // Lock should be released — can acquire again
        _browserSession.GetOrCreateDriver(Arg.Any<bool>()).Returns(_driver);
        using var lease = await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);
        lease.Driver.Should().BeSameAs(_driver);
    }

    [Fact]
    public async Task Background_ReleasesLock_WhenDriverCreationFails()
    {
        _browserSession.GetOrCreateDriver(Arg.Any<bool>())
            .Returns(_ => throw new WebDriverException("Driver creation failed"));

        var act = async () => await _queue.AcquireAsync(
            WebDriverPriority.Background, headless: true, CancellationToken.None);

        await act.Should().ThrowAsync<WebDriverException>();

        _queue.IsBackgroundActive.Should().BeFalse("background flag should reset on failure");

        // Lock should be released
        _browserSession.GetOrCreateDriver(Arg.Any<bool>()).Returns(_driver);
        using var lease = await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);
        lease.Driver.Should().BeSameAs(_driver);
    }

    [Fact]
    public void Disposed_Queue_ThrowsObjectDisposed()
    {
        _queue.Dispose();

        var act = async () => await _queue.AcquireAsync(
            WebDriverPriority.Foreground, headless: true, CancellationToken.None);

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
