// Educational and personal use only.

using Microsoft.Extensions.Logging;

namespace NYTAudioScraper.Infrastructure.Audio;

/// <summary>
/// HTTP message handler that implements adaptive rate limiting.
/// </summary>
public class AdaptiveRateLimitHandler : DelegatingHandler
{
    private readonly AdaptiveRateLimiter _rateLimiter;
    private readonly ILogger<AdaptiveRateLimitHandler> _logger;

    public AdaptiveRateLimitHandler(
        AdaptiveRateLimiter rateLimiter,
        ILogger<AdaptiveRateLimitHandler> logger)
    {
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Wait for the adaptive delay before making the request
        await _rateLimiter.WaitAsync(cancellationToken);

        // Send the request
        var response = await base.SendAsync(request, cancellationToken);

        // Handle rate limiting responses
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("Rate limit exceeded (429 Too Many Requests)");

            // Check for Retry-After header
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta.HasValue)
                {
                    var retryAfterSeconds = (int)response.Headers.RetryAfter.Delta.Value.TotalSeconds;
                    _rateLimiter.UpdateDelayFromRetryAfter(retryAfterSeconds);
                }
                else if (response.Headers.RetryAfter.Date.HasValue)
                {
                    var retryAfterSeconds = (int)(response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                    if (retryAfterSeconds > 0)
                    {
                        _rateLimiter.UpdateDelayFromRetryAfter(retryAfterSeconds);
                    }
                }
            }
            else
            {
                // No Retry-After header, use exponential backoff
                _rateLimiter.IncreaseDelay();
            }
        }
        else if (response.IsSuccessStatusCode)
        {
            // Successful request, gradually decrease delay
            _rateLimiter.DecreaseDelay();
        }

        return response;
    }
}
