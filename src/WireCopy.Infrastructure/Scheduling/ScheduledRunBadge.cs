// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Infrastructure.Browser.CommandHandlers;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.13 (B11) — turns the unacknowledged finished scheduled runs into a
/// single launcher-badge line, collapsing many to "N scheduled runs need attention"
/// so a backlog never spams the chrome. A run "needs attention" when it failed, was
/// interrupted, only partially succeeded, OR succeeded via a B9a/B9b RECOVERED step
/// that the user should ratify — a clean success never nags. The recovered case is
/// surfaced distinctly from a hard failure. Pure + render-free so it is unit-tested;
/// the renderer wraps the returned text with theme colour.
/// </summary>
internal static class ScheduledRunBadge
{
    /// <summary>True when a finished run is worth nagging the user about on next focus.</summary>
    public static bool NeedsAttention(ScheduledRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Status is ScheduledRunStatus.Failed or ScheduledRunStatus.Interrupted or ScheduledRunStatus.PartialSuccess
               || IsRecovered(run);
    }

    /// <summary>
    /// The badge text for the launcher, or null when nothing needs attention. One
    /// attention item renders specifically (failed vs recovered-ratify); several
    /// collapse to a count.
    /// </summary>
    public static string? Describe(IReadOnlyList<ScheduledRun> unacknowledgedFinished, bool onRoutinesHome = false)
    {
        ArgumentNullException.ThrowIfNull(unacknowledgedFinished);

        // Newest run per routine decides: older failures of a routine whose latest run is clean
        // stay quiet, and N failed runs of one routine count once.
        var attention = unacknowledgedFinished
            .GroupBy(r => r.RecipeId)
            .Select(g => g.OrderByDescending(r => r.StartedAtUtc).First())
            .Where(NeedsAttention)
            .ToList();
        if (attention.Count == 0)
        {
            return null;
        }

        if (attention.Count > 1)
        {
            return RoutineText.BadgeSeveral(attention.Count, RoutineText.BadgeSuffix(onRoutinesHome, several: true));
        }

        var run = attention[0];
        var suffix = RoutineText.BadgeSuffix(onRoutinesHome, several: false);
        if (run.Status is ScheduledRunStatus.Failed or ScheduledRunStatus.Interrupted)
        {
            // The Routines home card already carries the reason; the badge there only points at it.
            return onRoutinesHome
                ? RoutineText.BadgeFailedOnHome(run.RecipeName, suffix)
                : RoutineText.BadgeFailed(run.RecipeName, RoutineRunDiagnosis.Diagnose(run).Short, suffix);
        }

        return IsRecovered(run)
            ? RoutineText.BadgeRecovered(run.RecipeName, suffix)
            : RoutineText.BadgeDegraded(run.RecipeName, suffix);
    }

    private static bool IsRecovered(ScheduledRun run) =>
        run.StepOutcomesJson is { } json && json.Contains("\"Recovered\"", StringComparison.Ordinal);
}
