// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;

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
    public static string? Describe(IReadOnlyList<ScheduledRun> unacknowledgedFinished)
    {
        ArgumentNullException.ThrowIfNull(unacknowledgedFinished);
        var attention = unacknowledgedFinished.Where(NeedsAttention).ToList();
        if (attention.Count == 0)
        {
            return null;
        }

        if (attention.Count > 1)
        {
            return $"⚠ {attention.Count} scheduled runs need attention — open :schedules";
        }

        var run = attention[0];
        if (run.Status is ScheduledRunStatus.Failed or ScheduledRunStatus.Interrupted)
        {
            var reason = string.IsNullOrWhiteSpace(run.ErrorMessage) ? string.Empty : $" — {Trim(run.ErrorMessage!)}";
            return $"⚠ Scheduled run failed: {run.RecipeName}{reason} — open :schedules";
        }

        if (IsRecovered(run))
        {
            return $"⚠ {run.RecipeName}: a scheduled run used a re-derived section — ratify in :schedules";
        }

        // PartialSuccess without a recovery flag (an optional source was degraded).
        return $"⚠ {run.RecipeName}: scheduled run published with a degraded source — open :schedules";
    }

    private static bool IsRecovered(ScheduledRun run) =>
        run.StepOutcomesJson is { } json && json.Contains("\"Recovered\"", StringComparison.Ordinal);

    private static string Trim(string s) => s.Length <= 60 ? s : s[..57] + "…";
}
