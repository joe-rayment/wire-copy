// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-5oe9.9 — the entry decision for Ctrl+L. An UNCONFIGURED link-list
/// page opens AI-first ("Set up this site"); an already-configured page opens a
/// compact summary instead of re-running setup. The full multi-strategy
/// preflight/probe modal is demoted behind an explicit "compare all strategies"
/// affordance.
/// </summary>
internal static class ChooserEntry
{
    public enum Mode
    {
        /// <summary>No saved config — AI-first setup entry.</summary>
        SetupAiFirst,

        /// <summary>A saved config exists — show the compact summary.</summary>
        ConfiguredSummary,
    }

    public static Mode Decide(SiteHierarchyConfig? savedConfig)
        => savedConfig == null ? Mode.SetupAiFirst : Mode.ConfiguredSummary;

    /// <summary>
    /// AI setup is offerable when the analyzer has a key AND the page has enough
    /// content links to be worth curating — the SAME synchronous check the
    /// AiCurated strategy uses, with NO network probe.
    /// </summary>
    public static bool AiSetupAvailable(bool aiConfigured, int contentLinkCount)
        => aiConfigured && contentLinkCount >= 3;

    /// <summary>One-line description of the current saved layout for the summary card.</summary>
    public static string DescribeConfig(SiteHierarchyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var kind = config.Kind switch
        {
            LayoutKind.RssFeed => "RSS feed",
            LayoutKind.DocumentOrder => "Document order",
            _ when config.Sections.Count > 0 => $"AI Curated · {config.Sections.Count} section(s), pattern-based",
            LayoutKind.AiCurated => "AI Curated · snapshot",
            _ => "AI hierarchy",
        };

        return ConfigMigration.NeedsReanalysis(config)
            ? $"Current: {kind} — legacy snapshot, press r to re-run AI setup"
            : $"Current: {kind}";
    }

    /// <summary>
    /// workspace-v2m8.3: the summary card's Refine option label. It claims to
    /// keep the user's fixes ONLY when fixes exist (labels or applied
    /// instructions) — the unconditional "(keeps your fixes)" read as nonsense
    /// on a site the user had never corrected.
    /// </summary>
    public static string RefineOptionLabel(SiteHierarchyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var fixes = config.UserLabels.Count + config.UserInstructions.Count;
        return fixes switch
        {
            0 => "Refine the layout with AI",
            1 => "Refine the layout with AI (keeps your 1 fix)",
            _ => $"Refine the layout with AI (keeps your {fixes} fixes)",
        };
    }
}
