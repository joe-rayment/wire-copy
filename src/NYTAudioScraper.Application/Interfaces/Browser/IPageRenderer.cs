// Educational and personal use only.

using NYTAudioScraper.Application.DTOs.Browser;
using NYTAudioScraper.Domain.Entities.Browser;
using NYTAudioScraper.Domain.Enums.Browser;
using NYTAudioScraper.Domain.ValueObjects.Browser;

namespace NYTAudioScraper.Application.Interfaces.Browser;

/// <summary>
/// Service for rendering pages to the terminal.
/// Handles both hierarchical (link tree) and readable (article) views.
/// </summary>
public interface IPageRenderer
{
    /// <summary>
    /// Renders the page in hierarchical view (link tree).
    /// </summary>
    /// <param name="page">Page to render.</param>
    /// <param name="context">Current navigation context.</param>
    /// <param name="options">Render options.</param>
    void RenderHierarchical(Page page, NavigationContext context, RenderOptions options);

    /// <summary>
    /// Renders the page in readable view (article content).
    /// </summary>
    /// <param name="page">Page to render.</param>
    /// <param name="context">Current navigation context.</param>
    /// <param name="options">Render options.</param>
    void RenderReadable(Page page, NavigationContext context, RenderOptions options);

    /// <summary>
    /// Renders loading indicator.
    /// </summary>
    /// <param name="url">URL being loaded.</param>
    void RenderLoading(string url);

    /// <summary>
    /// Renders error message.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="url">URL that failed.</param>
    void RenderError(string message, string url);

    /// <summary>
    /// Renders status bar at bottom of screen.
    /// </summary>
    /// <param name="context">Current navigation context.</param>
    /// <param name="mode">Current view mode.</param>
    void RenderStatusBar(NavigationContext context, ViewMode mode);

    /// <summary>
    /// Clears the terminal screen.
    /// </summary>
    void Clear();
}
