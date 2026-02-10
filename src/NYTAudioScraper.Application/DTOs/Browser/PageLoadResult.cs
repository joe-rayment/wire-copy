// Educational and personal use only.

using NYTAudioScraper.Domain.ValueObjects.Browser;

namespace NYTAudioScraper.Application.DTOs.Browser;

/// <summary>
/// Result of a page load operation.
/// </summary>
public record PageLoadResult
{
    /// <summary>
    /// Whether the page load was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Final URL after any redirects.
    /// </summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Raw HTML content of the page.
    /// </summary>
    public string Html { get; init; } = string.Empty;

    /// <summary>
    /// Page metadata extracted from HTML.
    /// </summary>
    public PageMetadata? Metadata { get; init; }

    /// <summary>
    /// Error message if Success is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// HTTP status code.
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static PageLoadResult Successful(string url, string html, PageMetadata metadata)
    {
        return new PageLoadResult
        {
            Success = true,
            Url = url,
            Html = html,
            Metadata = metadata,
            StatusCode = 200
        };
    }

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    public static PageLoadResult Failure(string errorMessage, int statusCode = 0)
    {
        return new PageLoadResult
        {
            Success = false,
            ErrorMessage = errorMessage,
            StatusCode = statusCode
        };
    }
}
