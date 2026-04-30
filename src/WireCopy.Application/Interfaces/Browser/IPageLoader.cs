// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Service for loading web pages using browser automation.
/// </summary>
public interface IPageLoader
{
    /// <summary>
    /// Loads a page from the specified URL.
    /// </summary>
    /// <param name="request">Page load request with URL and options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing HTML and metadata, or error information.</returns>
    Task<PageLoadResult> LoadAsync(PageLoadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the raw HTML source of a page.
    /// </summary>
    /// <param name="url">URL to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Raw HTML string.</returns>
    Task<string> GetPageSourceAsync(string url, CancellationToken cancellationToken = default);
}
