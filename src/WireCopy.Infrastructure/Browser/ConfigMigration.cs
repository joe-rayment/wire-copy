// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.ScrapingStrategies;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-5oe9.6 — recognises AiCurated configs written before the durable
/// pattern landed (workspace-5oe9.5) so they don't silently rot into document
/// order on a revisit. Such configs still render via the legacy snapshot path
/// for the current visit, but are flagged so the UI nudges the user to re-run
/// AI setup (Ctrl+L / :reanalyze), which overwrites them with a Version-3
/// pattern config.
/// </summary>
internal static class ConfigMigration
{
    /// <summary>
    /// A "legacy snapshot": an AiCurated layout persisted as per-URL keys with
    /// NO durable sections. Defined precisely so genuinely-durable configs are
    /// never swept in — a Version&lt;3 AiHierarchical config that already has
    /// Sections (and no AiResult) is on the pattern path and is NOT legacy.
    /// </summary>
    public static bool IsLegacySnapshot(SiteHierarchyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var isAiCurated = config.Kind == LayoutKind.AiCurated
            || string.Equals(config.Strategy, AiCuratedStrategy.StrategyId, StringComparison.Ordinal);
        return isAiCurated
            && config.Sections.Count == 0
            && config.AiResult != null
            && config.Version < 3;
    }

    /// <summary>
    /// True when the UI should nudge the user to re-run AI setup — either the
    /// config was explicitly flagged (a fresh analysis that failed its
    /// self-test, workspace-5oe9.5) or it is a legacy snapshot.
    /// </summary>
    public static bool NeedsReanalysis(SiteHierarchyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.NeedsReanalyze || IsLegacySnapshot(config);
    }
}
