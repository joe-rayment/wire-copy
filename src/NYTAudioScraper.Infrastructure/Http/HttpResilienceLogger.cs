using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;

namespace NYTAudioScraper.Infrastructure.Http;

/// <summary>
/// Dedicated logger for HTTP resilience policies (retry, circuit breaker)
/// Provides centralized logging for all HTTP resilience events
/// </summary>
public class HttpResilienceLogger
{
    private readonly ILogger<HttpResilienceLogger> _logger;

    public HttpResilienceLogger(ILogger<HttpResilienceLogger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Logs retry attempts for HTTP requests
    /// </summary>
    public void LogRetry(Outcome<HttpResponseMessage> outcome, TimeSpan delay, int retryCount, string? endpoint = null)
    {
        var reason = outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString() ?? "Unknown";
        var statusCode = outcome.Result?.StatusCode;

        if (statusCode.HasValue)
        {
            _logger.LogWarning(
                "HTTP retry attempt {RetryCount} after {Delay}s for {Endpoint}: Status {StatusCode}",
                retryCount,
                delay.TotalSeconds,
                endpoint ?? "unknown endpoint",
                (int)statusCode.Value);
        }
        else
        {
            _logger.LogWarning(
                outcome.Exception,
                "HTTP retry attempt {RetryCount} after {Delay}s for {Endpoint}: {Reason}",
                retryCount,
                delay.TotalSeconds,
                endpoint ?? "unknown endpoint",
                reason);
        }
    }

    /// <summary>
    /// Logs circuit breaker opening (too many failures detected)
    /// </summary>
    public void LogCircuitBreakerOpen(Outcome<HttpResponseMessage> outcome, TimeSpan breakDuration, string? endpoint = null)
    {
        var reason = outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString() ?? "repeated failures";

        _logger.LogError(
            outcome.Exception,
            "HTTP circuit breaker OPENED for {Duration}s due to {Reason} on {Endpoint}. " +
            "No requests will be sent until circuit resets.",
            breakDuration.TotalSeconds,
            reason,
            endpoint ?? "unknown endpoint");
    }

    /// <summary>
    /// Logs circuit breaker reset (service is healthy again)
    /// </summary>
    public void LogCircuitBreakerReset(string? endpoint = null)
    {
        _logger.LogInformation(
            "HTTP circuit breaker RESET for {Endpoint} - service is healthy again",
            endpoint ?? "unknown endpoint");
    }

    /// <summary>
    /// Logs circuit breaker half-open state (testing if service recovered)
    /// </summary>
    public void LogCircuitBreakerHalfOpen(string? endpoint = null)
    {
        _logger.LogInformation(
            "HTTP circuit breaker HALF-OPEN for {Endpoint} - testing if service recovered",
            endpoint ?? "unknown endpoint");
    }

    /// <summary>
    /// Logs when circuit breaker rejects a request (circuit is open)
    /// </summary>
    public void LogCircuitBreakerRejected(string? endpoint = null)
    {
        _logger.LogWarning(
            "HTTP request REJECTED by circuit breaker for {Endpoint} - circuit is open",
            endpoint ?? "unknown endpoint");
    }
}
