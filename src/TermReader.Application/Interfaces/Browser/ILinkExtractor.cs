// Educational and personal use only.

using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Service for extracting and classifying links from HTML content.
/// </summary>
public interface ILinkExtractor
{
    /// <summary>
    /// Extracts all links from HTML content.
    /// </summary>
    /// <param name="html">Raw HTML content.</param>
    /// <param name="baseUrl">Base URL for resolving relative links.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of classified links.</returns>
    Task<List<LinkInfo>> ExtractLinksAsync(string html, string baseUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies a link based on its context in the DOM.
    /// </summary>
    /// <param name="url">Absolute URL of the link.</param>
    /// <param name="displayText">Anchor text of the link.</param>
    /// <param name="parentSelector">CSS selector path to parent element.</param>
    /// <param name="baseUrl">Base URL of the page.</param>
    /// <returns>Classified LinkInfo.</returns>
    LinkInfo ClassifyLink(string url, string displayText, string? parentSelector, string baseUrl);
}
