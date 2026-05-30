// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// How a saved <see cref="SiteHierarchyConfig"/> should be turned into a link
/// tree when a page (re)loads.
/// </summary>
internal enum HierarchyRoute
{
    /// <summary>No saved config — render plain document-order groups.</summary>
    DocumentOrder,

    /// <summary>
    /// Durable pattern config: <see cref="SiteHierarchyConfig.Sections"/> is
    /// non-empty. Built via the generalizing <c>BuildWithHierarchyConfig</c>
    /// path so it survives a revisit as article URLs rotate.
    /// </summary>
    PatternConfig,

    /// <summary>RSS layout — fetch + render the saved feed.</summary>
    RssFeed,

    /// <summary>
    /// Legacy per-URL AiCurated snapshot (no durable Sections, but an
    /// <see cref="SiteHierarchyConfig.AiResult"/> is present). Built via
    /// <c>BuildFromAiResultAsync</c>. Decays to document order on revisit — kept
    /// only as a fallback for configs written before workspace-5oe9.5.
    /// </summary>
    AiSnapshot,
}

/// <summary>
/// workspace-5oe9.5 — single source of truth for the page-load tree-build
/// routing decision, shared by <c>PageLoadPipeline.BuildPage</c> and
/// <c>PageLoadPipeline.RebuildFromBuildCacheAsync</c> so both stores route
/// identically.
///
/// <para>The decisive fix: a config with durable <see cref="SiteHierarchyConfig.Sections"/>
/// routes to <see cref="HierarchyRoute.PatternConfig"/> FIRST and
/// STORE-AGNOSTICALLY — it does NOT key on Kind/Strategy, which the
/// <c>DiskCacheStore</c> rehydrate drops. Previously AiCurated+AiResult was
/// checked before Sections, so a durable config rehydrated from the build cache
/// (Kind defaulted, AiResult null) — or any AiCurated config — collapsed to the
/// stale URL-snapshot path and rendered document order.</para>
/// </summary>
internal static class HierarchyRouteResolver
{
    public static HierarchyRoute Decide(SiteHierarchyConfig? config)
    {
        if (config == null)
        {
            return HierarchyRoute.DocumentOrder;
        }

        // Durable pattern config wins, regardless of Kind/Strategy (which the
        // build cache does not persist) or a lingering AiResult snapshot.
        if (config.Sections.Count > 0)
        {
            return HierarchyRoute.PatternConfig;
        }

        if (config.Kind == LayoutKind.RssFeed && !string.IsNullOrEmpty(config.RssFeedUrl))
        {
            return HierarchyRoute.RssFeed;
        }

        // Legacy fallback: a snapshot with no durable sections.
        if (config.AiResult != null)
        {
            return HierarchyRoute.AiSnapshot;
        }

        // A config that exists but carries no sections/feed/snapshot still goes
        // through the hierarchy builder (produces grouped output).
        return HierarchyRoute.PatternConfig;
    }
}
