// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.14 (B12a) — the pure, render-free heart of the Schedules editor:
/// the rules that decide whether a step may be pinned, how a step/cadence is built
/// from the user's choices, and how an existing recipe degrades when its site config
/// was deleted. Kept separate from the TUI so these guarantees — never persist an
/// unpinned section; never crash on a deleted config — are unit-tested directly.
/// </summary>
internal static class ScheduleEditing
{
    /// <summary>
    /// A site can contribute a durable step ONLY when it has a saved config that is
    /// not flagged for re-analysis and actually has sections to pick from. Otherwise
    /// the step-builder must route to inline setup (B12b) and BLOCK the save.
    /// </summary>
    public static bool CanPinSection(SiteHierarchyConfig? config) =>
        config is { NeedsReanalyze: false } && config.Sections.Count > 0;

    /// <summary>
    /// True when an EXISTING recipe step can no longer resolve its durable section
    /// against the current saved config (config deleted, flagged for re-analysis, or
    /// the section renamed/removed) — the editor renders "needs reconfigure" instead
    /// of crashing, and the step is never silently dropped.
    /// </summary>
    public static bool StepNeedsReconfigure(SiteHierarchyConfig? config, RecipeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (config is null || config.NeedsReanalyze)
        {
            return true;
        }

        return !config.Sections.Any(s =>
            string.Equals(s.Name, step.SectionName, StringComparison.OrdinalIgnoreCase) ||
            s.SortOrder == step.SortOrderFallback);
    }

    /// <summary>
    /// Builds a durable step from a section the user picked out of a live config —
    /// the section's Name + SortOrder become the durable identity, and the config's
    /// UrlPattern is captured so resolution survives heading/URL drift.
    /// </summary>
    public static RecipeStep BuildStep(
        string sourceUrl,
        string domain,
        string configUrlPattern,
        HierarchySection section,
        TakeMode takeMode,
        int? takeCount,
        bool required,
        IEnumerable<string>? headingAliases = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        return RecipeStep.Create(
            sourceUrl,
            domain,
            configUrlPattern,
            section.Name,
            takeMode,
            takeCount,
            required,
            section.SortOrder,
            headingAliases: headingAliases);
    }

    /// <summary>
    /// Builds a cadence from the day-of-week toggles + local time (+ optional grace).
    /// Throws (via <see cref="Cadence.Create"/>) when no day is selected, so the UI
    /// must keep the user on the cadence step until at least one day is chosen.
    /// </summary>
    public static Cadence BuildCadence(IEnumerable<DayOfWeek> days, TimeOnly localTime, TimeSpan? grace = null) =>
        Cadence.Create(days, localTime, grace);

    /// <summary>A compact human cadence summary for the list/edit screens, e.g. "Mon–Fri 07:00" or "Daily 07:00".</summary>
    public static string DescribeCadence(Cadence cadence)
    {
        ArgumentNullException.ThrowIfNull(cadence);
        var time = cadence.LocalTime.ToString("HH:mm");
        var days = cadence.Days;
        if (days.Count == 7)
        {
            return $"Daily {time}";
        }

        var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        if (days.Count == 5 && weekdays.All(days.Contains))
        {
            return $"Mon–Fri {time}";
        }

        var labels = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var ordered = Enum.GetValues<DayOfWeek>().Where(days.Contains).Select(d => labels[(int)d]);
        return $"{string.Join(",", ordered)} {time}";
    }
}
