// Educational and personal use only.

using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Analyzes a page visually using AI to determine the hierarchy
/// of links as they appear on screen.
/// </summary>
public interface IHierarchyAnalyzer
{
    /// <summary>
    /// Gets whether the analyzer is configured and ready to use
    /// (i.e., API key is available).
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Analyzes a page screenshot and extracted links to determine
    /// the visual hierarchy of content sections.
    /// </summary>
    /// <param name="screenshot">PNG screenshot bytes of the page viewport.</param>
    /// <param name="links">Links already extracted from the page HTML.</param>
    /// <param name="pageUrl">The URL of the page being analyzed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Hierarchy configuration determined by AI analysis.</returns>
    Task<SiteHierarchyConfig> AnalyzePageHierarchyAsync(
        byte[] screenshot,
        List<LinkInfo> links,
        string pageUrl,
        CancellationToken cancellationToken = default);
}
