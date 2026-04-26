// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Application.DTOs.Browser;

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
    /// How this page was fetched (HTTP, browser, or from cache).
    /// </summary>
    public FetchMethod FetchMethod { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static PageLoadResult Successful(string url, string html, PageMetadata metadata, FetchMethod fetchMethod = FetchMethod.Http)
    {
        return new PageLoadResult
        {
            Success = true,
            Url = url,
            Html = html,
            Metadata = metadata,
            StatusCode = 200,
            FetchMethod = fetchMethod
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
