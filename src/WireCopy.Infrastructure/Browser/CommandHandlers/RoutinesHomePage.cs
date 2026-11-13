// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>The Routines home as a <see cref="TileScreen"/>: one tile per routine, the bar names a new one.</summary>
internal static class RoutinesHomePage
{
    /// <summary>Enabled routines by next run (soonest first), then disabled ones by name.</summary>
    public static List<ScheduleRecipe> Ordered(IEnumerable<ScheduleRecipe> recipes, DateTimeOffset now)
    {
        var all = recipes.ToList();
        var enabled = all.Where(r => r.Enabled)
            .OrderBy(r => NextAt(r, now) ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var disabled = all.Where(r => !r.Enabled).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
        return enabled.Concat(disabled).ToList();
    }

    public static TileScreen Build(
        IReadOnlyList<ScheduleRecipe> recipes,
        IReadOnlySet<Guid> runningIds,
        IReadOnlyDictionary<Guid, string>? alerts,
        string? badge,
        DateTimeOffset now,
        int selected)
    {
        if (recipes.Count == 0)
        {
            return new TileScreen
            {
                Screen = "routines",
                Eyebrow = "Routines",
                Title = "Routines",
                AppLabel = "Routines",
                Tagline = "Sections you pick, read to you on a schedule",
                CountLabel = "no routines yet",
                FooterCount = "no routines yet",
                Badge = badge,
                BarPlaceholder = "New routine: type a name…",
                HintRow = "→ Name it · pick sites and sections · choose when · episodes land in your podcast feed",
                Cells = Array.Empty<TileCell>(),
                Columns = 2,
                Selected = TileGrid.Bar,
                Hints =
                [
                    new LauncherHint { Key = "n", Label = "new" },
                    new LauncherHint { Key = "Esc", Label = "back" },
                    new LauncherHint { Key = "?", Label = "all keys" },
                ],
            };
        }

        var ordered = Ordered(recipes, now);
        var cells = ordered.Select(r => new TileCell { Tile = ToTile(r, runningIds, alerts, now) }).ToList();
        var count = ordered.Count == 1 ? "1 routine" : $"{ordered.Count} routines";
        return new TileScreen
        {
            Screen = "routines",
            Eyebrow = "Routines",
            Title = "Routines",
            AppLabel = "Routines",
            Tagline = Tagline(ordered, now),
            CountLabel = count,
            FooterCount = count,
            Badge = badge,
            BarPlaceholder = "New routine: type a name…",
            Cells = cells,
            Columns = 2,
            Selected = selected,
            Hints =
            [
                new LauncherHint { Key = "Enter", Label = "open" },
                new LauncherHint { Key = "1-9", Label = "jump" },
                new LauncherHint { Key = "n", Label = "new" },
                new LauncherHint { Key = "r", Label = "run now" },
                new LauncherHint { Key = "Space", Label = "on/off" },
                new LauncherHint { Key = "d", Label = "delete" },
                new LauncherHint { Key = "?", Label = "all keys" },
            ],
        };
    }

    private static string Tagline(List<ScheduleRecipe> ordered, DateTimeOffset now)
    {
        var live = ordered.Where(r => r.Enabled && r.Steps.Count > 0).ToList();
        var on = live.Count;
        if (on == 0)
        {
            return "all off";
        }

        var next = live[0];
        var when = RoutineText.NextRun(next.Cadence, now).Replace("next ", string.Empty, StringComparison.Ordinal);
        return $"{on} on · next {next.Name} {when}";
    }

    private static Tile ToTile(ScheduleRecipe r, IReadOnlySet<Guid> runningIds, IReadOnlyDictionary<Guid, string>? alerts, DateTimeOffset now)
    {
        var freq = RoutineText.Frequency(r.Cadence);
        string hint;
        if (runningIds.Contains(r.Id))
        {
            hint = RoutineText.CardRunning;
        }
        else if (!r.Enabled)
        {
            hint = "off · " + freq;
        }
        else if (r.Steps.Count == 0)
        {
            hint = freq + " · not scheduled until it has a site";
        }
        else
        {
            hint = freq + " · " + RoutineText.NextRun(r.Cadence, now);
        }

        var detail = r.Steps.Count == 0 ? "nothing to read yet · add a site" : RoutineText.Contents(r) + " · " + RoutineText.LastResult(r, now);
        var hasAlert = alerts is not null && alerts.ContainsKey(r.Id);
        return new Tile
        {
            Id = "routine:" + r.Id,
            Title = r.Name.ToUpperInvariant(),
            Hint = hint,
            Detail = hasAlert ? null : detail,
            Alert = hasAlert ? alerts![r.Id] + " · Enter to fix" : null,
            Disabled = !r.Enabled,
        };
    }

    private static DateTimeOffset? NextAt(ScheduleRecipe r, DateTimeOffset now) =>
        NextDueCalculator.Decide(r.Cadence, RecipeRunState.Initial, now).NextAt;
}
