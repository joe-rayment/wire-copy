// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>The outcome of a schedule-side config lookup (workspace-42q8.1, see <see cref="ScheduleConfigResolution"/>).</summary>
internal sealed record ScheduleConfigLookup
{
    /// <summary>The config this step/URL resolves to, or null when none covers it.</summary>
    public SiteHierarchyConfig? Config { get; init; }

    /// <summary>Every config saved for the site, regardless of URL match.</summary>
    public IReadOnlyList<SiteHierarchyConfig> SiteConfigs { get; init; } = Array.Empty<SiteHierarchyConfig>();

    /// <summary>True when the site has at least one saved layout (even if none covers the URL).</summary>
    public bool SiteHasAnyConfig => SiteConfigs.Count > 0;
}
