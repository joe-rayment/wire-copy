// <copyright file="HttpResilienceLoggerTests.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NYTAudioScraper.Infrastructure.Http;
using Polly;
using Xunit;

namespace NYTAudioScraper.Tests;

/// <summary>
/// Tests for HttpResilienceLogger to ensure proper logging of HTTP resilience events
/// </summary>
public class HttpResilienceLoggerTests
{
    private readonly HttpResilienceLogger _logger;
    private readonly ILogger<HttpResilienceLogger> _mockLogger;

    public HttpResilienceLoggerTests()
    {
        _mockLogger = Substitute.For<ILogger<HttpResilienceLogger>>();
        _logger = new HttpResilienceLogger(_mockLogger);
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act
        Action act = () => new HttpResilienceLogger(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    public void LogRetry_WithException_LogsWarningWithException()
    {
        // Arrange
        var exception = new HttpRequestException("Network error");
        var outcome = new DelegateResult<HttpResponseMessage>(exception);
        var delay = TimeSpan.FromSeconds(2);
        var retryCount = 1;
        var endpoint = "ElevenLabs API";

        // Act
        _logger.LogRetry(outcome, delay, retryCount, endpoint);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("retry attempt 1")),
            exception,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void LogRetry_WithStatusCode_LogsWarningWithStatusCode()
    {
        // Arrange
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        var outcome = new DelegateResult<HttpResponseMessage>(response);
        var delay = TimeSpan.FromSeconds(4);
        var retryCount = 2;
        var endpoint = "ElevenLabs API";

        // Act
        _logger.LogRetry(outcome, delay, retryCount, endpoint);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Status 503")),
            null,
            Arg.Any<Func<object, Exception?, string>>());

        response.Dispose();
    }

    [Fact]
    public void LogRetry_WithoutEndpoint_UsesDefaultText()
    {
        // Arrange
        var exception = new HttpRequestException("Error");
        var outcome = new DelegateResult<HttpResponseMessage>(exception);
        var delay = TimeSpan.FromSeconds(2);
        var retryCount = 1;

        // Act
        _logger.LogRetry(outcome, delay, retryCount, null);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("unknown endpoint")),
            exception,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void LogCircuitBreakerOpen_WithException_LogsError()
    {
        // Arrange
        var exception = new HttpRequestException("Repeated failures");
        var outcome = new DelegateResult<HttpResponseMessage>(exception);
        var breakDuration = TimeSpan.FromSeconds(30);
        var endpoint = "ElevenLabs API";

        // Act
        _logger.LogCircuitBreakerOpen(outcome, breakDuration, endpoint);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("OPENED") && o.ToString()!.Contains("30.0s")),
            exception,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void LogCircuitBreakerOpen_IncludesWarningMessage()
    {
        // Arrange
        var exception = new HttpRequestException("Error");
        var outcome = new DelegateResult<HttpResponseMessage>(exception);
        var breakDuration = TimeSpan.FromSeconds(30);

        // Act
        _logger.LogCircuitBreakerOpen(outcome, breakDuration, "Test API");

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("No requests will be sent until circuit resets")),
            exception,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void LogCircuitBreakerReset_LogsInformation()
    {
        // Arrange
        var endpoint = "ElevenLabs API";

        // Act
        _logger.LogCircuitBreakerReset(endpoint);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("RESET") && o.ToString()!.Contains("healthy again")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void LogCircuitBreakerHalfOpen_LogsInformation()
    {
        // Arrange
        var endpoint = "ElevenLabs API";

        // Act
        _logger.LogCircuitBreakerHalfOpen(endpoint);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("HALF-OPEN") && o.ToString()!.Contains("testing if service recovered")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void LogCircuitBreakerRejected_LogsWarning()
    {
        // Arrange
        var endpoint = "ElevenLabs API";

        // Act
        _logger.LogCircuitBreakerRejected(endpoint);

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("REJECTED") && o.ToString()!.Contains("circuit is open")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void AllMethods_WithNullEndpoint_UseDefaultEndpointText()
    {
        // Arrange
        var exception = new HttpRequestException("Error");
        var outcome = new DelegateResult<HttpResponseMessage>(exception);

        // Act
        _logger.LogRetry(outcome, TimeSpan.FromSeconds(1), 1, null);
        _logger.LogCircuitBreakerOpen(outcome, TimeSpan.FromSeconds(30), null);
        _logger.LogCircuitBreakerReset(null);
        _logger.LogCircuitBreakerHalfOpen(null);
        _logger.LogCircuitBreakerRejected(null);

        // Assert - All should log "unknown endpoint"
        _mockLogger.Received(5).Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("unknown endpoint")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void LogRetry_FormatsDelayCorrectly()
    {
        // Arrange
        var exception = new HttpRequestException("Error");
        var outcome = new DelegateResult<HttpResponseMessage>(exception);
        var delay = TimeSpan.FromMilliseconds(2500); // 2.5 seconds

        // Act
        _logger.LogRetry(outcome, delay, 1, "Test API");

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("2.5s")),
            exception,
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void LogCircuitBreakerOpen_FormatsBreakDurationCorrectly()
    {
        // Arrange
        var exception = new HttpRequestException("Error");
        var outcome = new DelegateResult<HttpResponseMessage>(exception);
        var breakDuration = TimeSpan.FromMilliseconds(45000); // 45 seconds

        // Act
        _logger.LogCircuitBreakerOpen(outcome, breakDuration, "Test API");

        // Assert
        _mockLogger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("45.0s")),
            exception,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
