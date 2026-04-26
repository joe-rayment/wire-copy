// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Options;
using TermReader.Infrastructure.Browser.Cache;
using TermReader.Infrastructure.Configuration;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class InputIdleDetectorTests : IDisposable
{
    private readonly InputIdleDetector _detector;

    public InputIdleDetectorTests()
    {
        var config = new CacheConfiguration { IdleThresholdMs = 100 };
        _detector = new InputIdleDetector(Options.Create(config));
    }

    public void Dispose()
    {
        _detector.Dispose();
    }

    [Fact]
    public void IsIdle_InitiallyFalse()
    {
        _detector.IsIdle.Should().BeFalse();
    }

    [Fact]
    public async Task IsIdle_BecomesTrue_AfterIdleThreshold()
    {
        // Wait for threshold + polling interval margin
        await Task.Delay(700);
        _detector.IsIdle.Should().BeTrue();
    }

    [Fact]
    public async Task RecordActivity_ResetsIdleState()
    {
        // Wait until idle
        await Task.Delay(700);
        _detector.IsIdle.Should().BeTrue();

        // Record activity
        _detector.RecordActivity();
        _detector.IsIdle.Should().BeFalse();
    }

    [Fact]
    public async Task WaitForIdleAsync_ReturnsImmediately_WhenAlreadyIdle()
    {
        await Task.Delay(700);
        _detector.IsIdle.Should().BeTrue();

        // Should complete immediately since already idle
        var task = _detector.WaitForIdleAsync();
        var completed = await Task.WhenAny(task, Task.Delay(1000));
        completed.Should().Be(task);
    }

    [Fact]
    public async Task WaitForIdleAsync_Completes_AfterIdleThreshold()
    {
        _detector.RecordActivity();

        var task = _detector.WaitForIdleAsync();
        var completed = await Task.WhenAny(task, Task.Delay(2000));
        completed.Should().Be(task, "should complete when idle threshold is reached");
    }

    [Fact]
    public async Task WaitForIdleAsync_Cancellable()
    {
        _detector.RecordActivity();

        using var cts = new CancellationTokenSource(50);

        var act = async () => await _detector.WaitForIdleAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RecordActivity_KeepsActive_WithContinuousInput()
    {
        for (var i = 0; i < 5; i++)
        {
            _detector.RecordActivity();
            await Task.Delay(50);
        }

        _detector.IsIdle.Should().BeFalse();
    }
}
