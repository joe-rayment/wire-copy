// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>Builds the launcher-tile screen for one routine (workspace-6ifpp.1).</summary>
internal static class RoutinePage
{
    public static TileScreen Build(
        ScheduleRecipe recipe, ScheduledRun? lastRun, DateTimeOffset now, IReadOnlyList<Bookmark> bookmarks, int selected, string? hintRow = null)
    {
        var freq = RoutineText.Frequency(recipe.Cadence);
        var schedule = recipe.Enabled ? $"{freq} · {RoutineText.NextRun(recipe.Cadence, now)}" : $"off · {freq}";
        var tagline = recipe.Enabled
            ? $"{RoutineText.FrequencyInWords(recipe.Cadence)} · {RoutineText.NextRun(recipe.Cadence, now)}"
            : $"off · {RoutineText.FrequencyInWords(recipe.Cadence)}";

        var sites = recipe.Steps
            .GroupBy(s => (s.Domain, s.ConfigUrlPattern))
            .Select(g => SiteTile(g.ToList(), bookmarks))
            .ToList();

        var cells = new List<TileCell>
        {
            sites.Count > 0
                ? new TileCell { Tile = sites[0] }
                : new TileCell { Tile = new Tile { Id = "addSite", Title = "ADD A SITE", Hint = "nothing to read yet" } },
            new TileCell
            {
                Legend = "This routine",
                Top = new Tile { Id = "name", Title = "NAME", Hint = recipe.Name },
                Bottom = new Tile { Id = "schedule", Title = "SCHEDULE", Hint = schedule },
            },
            sites.Count > 1 ? new TileCell { Tile = sites[1] } : new TileCell(),
            new TileCell
            {
                Legend = "Output",
                Top = new Tile { Id = "podcast", Title = "PODCAST", Hint = $"{recipe.OutputCollectionName} · episodes go to this feed" },
                Bottom = new Tile { Id = "lastRun", Title = "LAST RUN", Hint = LastRunValue(lastRun, now) },
            },
        };
        cells.AddRange(sites.Skip(2).Select(t => new TileCell { Tile = t }));

        return new TileScreen
        {
            Screen = "routine",
            Eyebrow = $"Routines › {recipe.Name}",
            Title = recipe.Name,
            Tagline = tagline,
            CountLabel = RoutineText.Contents(recipe),
            Status = recipe.Enabled ? "on" : "off",
            BarPlaceholder = "Add a site: type an address…",
            HintRow = hintRow,
            Cells = cells,
            Columns = 2,
            Selected = selected,
            AppLabel = "Routine",
            Hints = new List<LauncherHint>
            {
                new() { Key = "Enter", Label = "open" },
                new() { Key = "1-9", Label = "jump" },
                new() { Key = "a", Label = "add a site" },
                new() { Key = "r", Label = "run now" },
                new() { Key = "Space", Label = "on/off" },
                new() { Key = "d", Label = "remove" },
                new() { Key = "?", Label = "all keys" },
            },
        };
    }

    /// <summary>host[:port] plus the path when it is not "/".</summary>
    internal static string SiteLabel(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return sourceUrl;
        }

        var label = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var path = uri.AbsolutePath;
        return path is "/" or "" ? label : label + path;
    }

    internal static string LastRunValue(ScheduledRun? run, DateTimeOffset now)
    {
        var d = run is null ? null : RoutineRunDiagnosis.Diagnose(run);
        return RoutineText.LastRunValue(run, d is { NeedsAttention: true } ? d.Short : null, now);
    }

    private static Tile SiteTile(List<RecipeStep> steps, IReadOnlyList<Bookmark> bookmarks)
    {
        var url = steps[0].SourceUrl;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        var bookmark = bookmarks.FirstOrDefault(b => steps[0].BookmarkId is { } id && b.Id == id)
            ?? bookmarks.FirstOrDefault(b => Uri.TryCreate(b.Url, UriKind.Absolute, out var bu)
                && string.Equals(bu.Host, host, StringComparison.OrdinalIgnoreCase));
        return new Tile
        {
            Id = "site:" + url,
            Title = (bookmark?.Name ?? host).ToUpperInvariant(),
            Hint = SiteLabel(url),
            Detail = string.Join(" · ", steps.Select(s => $"{s.SectionName} ({RoutineText.Take(s)})")),
        };
    }
}
