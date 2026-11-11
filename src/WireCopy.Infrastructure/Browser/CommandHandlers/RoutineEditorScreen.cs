// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>Row model and terminal drawing for the routine editor (workspace-wyvga.5).</summary>
internal static class RoutineEditorScreen
{
    private const string Reset = "\x1b[0m";

    /// <summary>Builds every row, top to bottom. Sites = distinct Domain + ConfigUrlPattern in first-appearance order.</summary>
    public static List<RoutineRow> BuildRows(ScheduleRecipe recipe, DateTimeOffset now, ScheduledRun? lastRun = null)
    {
        var rows = new List<RoutineRow>
        {
            new(RoutineRowKind.Name, "Name", recipe.Name),
            new(RoutineRowKind.Frequency, "Frequency", RoutineText.Frequency(recipe.Cadence) + " · " + RoutineText.NextRun(recipe.Cadence, now)),
            new(RoutineRowKind.Podcast, "Podcast", recipe.OutputCollectionName),
            new(RoutineRowKind.Enabled, "Enabled", recipe.Enabled ? "on" : "off"),
            new(RoutineRowKind.LastRun, RoutineText.LastRunRow, LastRunValue(lastRun, now)),
            new(RoutineRowKind.ContentsHeading, "Contents"),
        };

        if (recipe.Steps.Count == 0)
        {
            rows.Add(new(RoutineRowKind.NothingYet, "nothing yet"));
        }

        foreach (var site in recipe.Steps
                     .Select((step, index) => (step, index))
                     .GroupBy(x => (x.step.Domain, x.step.ConfigUrlPattern)))
        {
            var url = site.First().step.SourceUrl;
            rows.Add(new(RoutineRowKind.Site, SiteLabel(url), SiteUrl: url));
            foreach (var (step, index) in site)
            {
                rows.Add(new(RoutineRowKind.Section, step.SectionName, RoutineText.Take(step), url, index));
            }
        }

        rows.Add(new(RoutineRowKind.AddSite, "+ Add a site"));
        return rows;
    }

    /// <summary>host[:port] plus the path when it is not "/".</summary>
    public static string SiteLabel(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return sourceUrl;
        }

        var label = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var path = uri.AbsolutePath;
        return path is "/" or "" ? label : label + path;
    }

    /// <summary>Index of the first selectable row at or after <paramref name="from"/> in direction <paramref name="step"/>.</summary>
    public static int Move(IReadOnlyList<RoutineRow> rows, int from, int step)
    {
        var i = from;
        for (var n = 0; n < rows.Count; n++)
        {
            i = (i + step + rows.Count) % rows.Count;
            if (rows[i].Selectable)
            {
                return i;
            }
        }

        return from;
    }

    public static int FirstSelectable(IReadOnlyList<RoutineRow> rows) => rows.ToList().FindIndex(r => r.Selectable);

    /// <summary>Paints the whole screen; keeps the selected row inside the window. Returns the new scroll offset.</summary>
    public static int Draw(
        RenderHelpers helpers,
        ThemePalette palette,
        int width,
        int terminalHeight,
        string routineName,
        IReadOnlyList<RoutineRow> rows,
        int selected,
        int offset,
        RoutineEditView? edit = null)
    {
        helpers.Clear();
        helpers.WriteLine();
        var eyebrow = RenderHelpers.TruncateText(RoutineText.Eyebrow(routineName), width - 4);
        helpers.WriteLine($"  {palette.GetRoutineAccentFg().AnsiFg}{eyebrow}{Reset}");
        helpers.WriteLine();

        helpers.BeginCapture();
        var firstLine = 0;
        var lastLine = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var start = helpers.CapturedLineCount;
            if (edit != null && rows[i].Kind == edit.Kind)
            {
                WriteEditRow(helpers, palette, rows[i], edit);
            }
            else
            {
                WriteRow(helpers, palette, width, rows[i], i == selected);
            }

            if (i == selected)
            {
                firstLine = start;
                lastLine = helpers.CapturedLineCount - 1;
            }
        }

        var body = helpers.EndCapture();
        var bodyTop = helpers.LinesWritten;
        var budget = Math.Max(1, terminalHeight - helpers.LinesWritten - 2);
        if (body.Count <= budget)
        {
            offset = 0;
            SetEditPosition(edit, bodyTop, firstLine, offset, false);
            body.ForEach(l => helpers.WriteLine(l));
        }
        else
        {
            var window = Math.Max(1, budget - 2);
            offset = Math.Clamp(offset, 0, Math.Max(0, body.Count - window));
            if (firstLine < offset)
            {
                offset = firstLine;
            }
            else if (lastLine >= offset + window)
            {
                offset = lastLine - window + 1;
            }

            SetEditPosition(edit, bodyTop, firstLine, offset, true);
            var dim = palette.GetDimFg().AnsiFg;
            helpers.WriteLine(offset > 0 ? $"  {dim}▲ more{Reset}" : string.Empty);
            for (var i = offset; i < Math.Min(body.Count, offset + window); i++)
            {
                helpers.WriteLine(body[i]);
            }

            helpers.WriteLine(offset + window < body.Count ? $"  {dim}▼ more{Reset}" : string.Empty);
        }

        helpers.WriteLine();
        helpers.WriteLine($"  {palette.GetDimFg().AnsiFg}{RenderHelpers.TruncateText(RoutineText.EditorFooter, width - 4)}{Reset}");
        helpers.ClearRemainingLines();
        return offset;
    }

    private static void SetEditPosition(RoutineEditView? edit, int bodyTop, int editLine, int offset, bool scrolled)
    {
        if (edit != null)
        {
            edit.ScreenRow = bodyTop + (editLine - offset) + (scrolled ? 1 : 0);
        }
    }

    /// <summary>The row under edit: label, then blank space where the caller's prompt draws, then a hint (or the error) on the line below.</summary>
    private static void WriteEditRow(RenderHelpers helpers, ThemePalette palette, RoutineRow row, RoutineEditView edit)
    {
        const int labelCol = 24;
        var label = row.Label.PadRight(labelCol);
        var gutter = $"{palette.GetRoutineAccentFg().AnsiFg}▌{Reset}";
        helpers.WriteLine($"  {gutter} {palette.GetMutedFg().AnsiFg}•{Reset} {palette.PrimaryText.AnsiFg}{label}{Reset}{palette.GetDimFg().AnsiFg}{edit.Prefix}{Reset}");
        edit.InputCol = 2 + 1 + 1 + 2 + labelCol + edit.Prefix.Length;
        var color = edit.Error is null ? palette.GetDimFg().AnsiFg : palette.ErrorFg.AnsiFg;
        helpers.WriteLine($"{new string(' ', 6 + labelCol)}{color}{edit.Error ?? edit.Hint}{Reset}");
    }

    private static string LastRunValue(ScheduledRun? run, DateTimeOffset now)
    {
        var d = run is null ? null : RoutineRunDiagnosis.Diagnose(run);
        return RoutineText.LastRunValue(run, d is { NeedsAttention: true } ? d.Short : null, now);
    }

    private static void WriteRow(RenderHelpers helpers, ThemePalette palette, int width, RoutineRow row, bool selected)
    {
        switch (row.Kind)
        {
            case RoutineRowKind.Name or RoutineRowKind.Frequency or RoutineRowKind.Podcast or RoutineRowKind.Enabled or RoutineRowKind.LastRun:
                var action = row.Kind switch { RoutineRowKind.Enabled => "toggle", RoutineRowKind.LastRun => RoutineText.LastRunAction, _ => "edit" };
                var muted = palette.GetMutedFg().AnsiFg;
                var (main, _) = SettingsRowRenderer.Build(palette, width, selected, false, "•", muted, row.Label, row.Value, palette.PrimaryText.AnsiFg, action);
                helpers.WriteLine(main);
                break;
            case RoutineRowKind.ContentsHeading:
                helpers.WriteLine(string.Empty);
                helpers.WriteLine($"  {palette.SecondaryText.AnsiFg}{row.Label}{Reset}");
                break;
            case RoutineRowKind.NothingYet:
                helpers.WriteLine($"      {palette.GetDimFg().AnsiFg}{row.Label}{Reset}");
                break;
            case RoutineRowKind.Site:
                helpers.WriteLine(Line(palette, width, selected, 0, row.Label, palette.PrimaryText.AnsiFg));
                break;
            case RoutineRowKind.Section:
                helpers.WriteLine(Line(palette, width, selected, 4, row.Label + " · " + row.Value, palette.SecondaryText.AnsiFg));
                break;
            case RoutineRowKind.AddSite:
                helpers.WriteLine(Line(palette, width, selected, 0, row.Label, palette.GetRoutineAccentFg().AnsiFg));
                break;
        }
    }

    private static string Line(ThemePalette palette, int width, bool selected, int indent, string text, string color)
    {
        var plain = RenderHelpers.TruncateText(new string(' ', indent) + text, Math.Max(4, width - 8));
        var gutter = $"{palette.GetMutedFg().AnsiFg}{(selected ? "▌" : " ")}{Reset}";
        var padded = plain.PadRight(Math.Max(4, width - 8));
        return selected
            ? $"  {gutter} {palette.SelectedItemBg.AnsiBg}{palette.SelectedItemFg.AnsiFg}{padded}{Reset}"
            : $"  {gutter} {color}{plain}{Reset}";
    }
}
