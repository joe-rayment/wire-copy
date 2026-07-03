// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Application.Interfaces.Browser;

/// <summary>
/// Persistent storage for AI-generated page hierarchy configurations.
/// Configs are stored per-domain and matched by URL pattern.
/// </summary>
public interface IHierarchyConfigStore
{
    /// <summary>
    /// Gets the hierarchy config matching the given URL, if one exists.
    /// Matches against saved URL patterns for the URL's domain.
    /// </summary>
    /// <param name="url">The page URL to find a config for.</param>
    /// <returns>Matching config, or null if no config exists.</returns>
    Task<SiteHierarchyConfig?> GetConfigAsync(string url);

    /// <summary>
    /// Saves a hierarchy config. Overwrites any existing config
    /// for the same domain and URL pattern.
    /// </summary>
    /// <param name="config">The config to save.</param>
    /// <summary>Persists the config. Returns false when the write failed
    /// (disk full / permissions) so callers can avoid claiming success
    /// (workspace-9k27.4).</summary>
    Task<bool> SaveConfigAsync(SiteHierarchyConfig config);

    /// <summary>
    /// Deletes the hierarchy config matching the given URL.
    /// </summary>
    /// <param name="url">The page URL whose config should be deleted.</param>
    /// <returns>True if a config was deleted, false if none existed.</returns>
    Task<bool> DeleteConfigAsync(string url);

    /// <summary>
    /// Gets the total number of saved hierarchy configs across all domains.
    /// </summary>
    Task<int> GetConfigCountAsync();

    /// <summary>
    /// workspace-5oe9.6: purges legacy per-URL AiCurated snapshot configs
    /// (Version&lt;3, no durable sections) across all domains. Version-3
    /// pattern configs are left intact. Returns the number removed.
    /// </summary>
    Task<int> ClearLegacySnapshotsAsync();
}
