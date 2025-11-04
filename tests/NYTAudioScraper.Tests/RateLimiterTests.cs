using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NYTAudioScraper.Infrastructure.Audio;

namespace NYTAudioScraper.Tests;

public class RateLimiterTests : IDisposable
{
    private readonly ILogger<RateLimiter> _logger;
    private RateLimiter? _rateLimiter;

    public RateLimiterTests()
    {
        _logger = Substitute.For<ILogger<RateLimiter>>();
    }

    [Fact]
    public void Constructor_WithInvalidMaxConcurrency_ThrowsArgumentException()
    {
        // Act & Assert
        var act = () => new RateLimiter(0, 1000, _logger);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Max concurrency must be greater than 0*");
    }

    [Fact]
    public void Constructor_WithNegativeDelay_ThrowsArgumentException()
    {
        // Act & Assert
        var act = () => new RateLimiter(3, -100, _logger);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Min delay cannot be negative*");
    }

    [Fact]
    public async Task AcquireAsync_WithinLimit_DoesNotBlock()
    {
        // Arrange
        _rateLimiter = new RateLimiter(3, 0, _logger);

        // Act
        var startTime = DateTime.UtcNow;
        await _rateLimiter.AcquireAsync();
        var duration = DateTime.UtcNow - startTime;

        // Assert
        duration.TotalMilliseconds.Should().BeLessThan(100); // Should be instant
        _rateLimiter.AvailableSlots.Should().Be(2); // One slot taken
    }

    [Fact]
    public async Task AcquireAndRelease_RestoresAvailableSlots()
    {
        // Arrange
        _rateLimiter = new RateLimiter(3, 0, _logger);

        // Act
        await _rateLimiter.AcquireAsync();
        var slotsAfterAcquire = _rateLimiter.AvailableSlots;
        _rateLimiter.Release();
        var slotsAfterRelease = _rateLimiter.AvailableSlots;

        // Assert
        slotsAfterAcquire.Should().Be(2);
        slotsAfterRelease.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ExecutesActionAndReturnsResult()
    {
        // Arrange
        _rateLimiter = new RateLimiter(3, 0, _logger);
        var expectedResult = 42;

        // Act
        var result = await _rateLimiter.ExecuteAsync(async () =>
        {
            await Task.Delay(10);
            return expectedResult;
        });

        // Assert
        result.Should().Be(expectedResult);
        _rateLimiter.AvailableSlots.Should().Be(3); // Should be released after execution
    }

    [Fact]
    public async Task ExecuteAsync_WithException_ReleasesSlot()
    {
        // Arrange
        _rateLimiter = new RateLimiter(3, 0, _logger);

        // Act & Assert
        var act = async () => await _rateLimiter.ExecuteAsync<int>(async () =>
        {
            await Task.Delay(10);
            throw new InvalidOperationException("Test exception");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        _rateLimiter.AvailableSlots.Should().Be(3); // Slot should be released even after exception
    }

    [Fact]
    public async Task AcquireAsync_WithMinDelay_EnforcesDelay()
    {
        // Arrange
        _rateLimiter = new RateLimiter(3, minDelayMs: 100, _logger);

        // Act
        await _rateLimiter.AcquireAsync();
        _rateLimiter.Release();

        var startTime = DateTime.UtcNow;
        await _rateLimiter.AcquireAsync();
        var duration = DateTime.UtcNow - startTime;

        // Assert
        duration.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(90); // Allow small timing variance
    }

    [Fact]
    public async Task ParallelExecutions_RespectsConcurrencyLimit()
    {
        // Arrange
        _rateLimiter = new RateLimiter(2, 0, _logger);
        var concurrentExecutions = 0;
        var maxConcurrentExecutions = 0;
        var lockObject = new object();

        // Act - Try to run 5 operations in parallel with max concurrency of 2
        var tasks = Enumerable.Range(0, 5).Select(i =>
            _rateLimiter.ExecuteAsync(async () =>
            {
                lock (lockObject)
                {
                    concurrentExecutions++;
                    maxConcurrentExecutions = Math.Max(maxConcurrentExecutions, concurrentExecutions);
                }

                await Task.Delay(50); // Simulate work

                lock (lockObject)
                {
                    concurrentExecutions--;
                }

                return i;
            })
        );

        await Task.WhenAll(tasks);

        // Assert
        maxConcurrentExecutions.Should().BeLessOrEqualTo(2);
    }

    [Fact]
    public void AvailableSlots_ReflectsCurrentState()
    {
        // Arrange
        _rateLimiter = new RateLimiter(5, 0, _logger);

        // Act & Assert
        _rateLimiter.AvailableSlots.Should().Be(5);

        _rateLimiter.AcquireAsync().GetAwaiter().GetResult();
        _rateLimiter.AvailableSlots.Should().Be(4);

        _rateLimiter.AcquireAsync().GetAwaiter().GetResult();
        _rateLimiter.AvailableSlots.Should().Be(3);

        _rateLimiter.Release();
        _rateLimiter.AvailableSlots.Should().Be(4);
    }

    public void Dispose()
    {
        _rateLimiter?.Dispose();
    }
}
