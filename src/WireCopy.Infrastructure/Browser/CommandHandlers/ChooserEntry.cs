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
}
