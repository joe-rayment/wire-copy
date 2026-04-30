// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// High-level orchestration service for browser functionality.
/// Coordinates all browser services to provide complete page loading and navigation.
/// </summary>
public interface IBrowserService
{
    /// <summary>
    /// Loads a page from a URL, extracting links and readable content.
    /// </summary>
    Task<Page> LoadPageAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a navigation tree from a loaded page.
    /// </summary>
    Task<NavigationTree> BuildNavigationTreeAsync(Page page, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts readable content from a page (if it's an article).
    /// </summary>
    Task<ReadableContent?> ExtractReadableContentAsync(Page page, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a page to the terminal in the specified view mode.
    /// </summary>
    Task RenderAsync(Page page, ViewMode mode, RenderOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the interactive browser loop.
    /// </summary>
    /// <param name="initialUrl">Optional initial URL to load.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RunAsync(string? initialUrl = null, CancellationToken cancellationToken = default);
}
