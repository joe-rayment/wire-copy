// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Persistent storage for AI-generated per-domain article-extraction layouts.
/// One file per domain, mirroring <see cref="IHierarchyConfigStore"/>'s shape
/// but with <see cref="ArticleSelectorConfig"/> as the payload.
/// </summary>
public interface IArticleLayoutStore
{
    /// <summary>
    /// Loads the saved layout for the given domain, or null if none exists.
    /// </summary>
    /// <param name="domain">Lower-cased domain (e.g. <c>nytimes.com</c>).</param>
    /// <returns>Saved config or null.</returns>
    Task<ArticleSelectorConfig?> LoadAsync(string domain);

    /// <summary>
    /// Atomically writes the supplied config to disk. The previous file (if
    /// any) is replaced.
    /// </summary>
    /// <param name="config">Config to persist.</param>
    Task SaveAsync(ArticleSelectorConfig config);
}
