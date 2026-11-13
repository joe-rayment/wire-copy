// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>The schedule as one screen of launcher tiles: presets, days, and the time typed in the bar (workspace-6ifpp.2).</summary>
internal static class RoutineSchedulePage
{
    private const string LastDayHint = "At least one day has to stay on";

    private static readonly DayOfWeek[] AllDays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static readonly DayOfWeek[] Weekends = [DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static TileScreen Build(ScheduleRecipe recipe, DateTimeOffset now, int selected, int columns, string? message = null)
    {
        var days = recipe.Cadence.Days;
        var cells = new List<TileCell>
        {
            PresetCell("everyday", "EVERY DAY", "all seven", AllDays, days),
            PresetCell("weekdays", "WEEKDAYS", "Mon–Fri", Weekdays, days),
            PresetCell("weekends", "WEEKENDS", "Sat, Sun", Weekends, days),
        };
        for (var i = 3; i < columns; i++)
        {
            cells.Add(new TileCell());
        }

        foreach (var day in AllDays)
        {
            var on = days.Contains(day);
            cells.Add(new TileCell
            {
                Tile = new Tile
                {
                    Id = "day:" + day,
                    Title = day.ToString()[..3].ToUpperInvariant(),
                    Hint = on ? "on" : "off",
                    Disabled = !on,
                },
            });
        }

        var cols = Math.Max(1, columns);
        var next = recipe.Enabled ? RoutineText.NextRun(recipe.Cadence, now) : "off";
        return new TileScreen
        {
            Screen = "schedule",
            Eyebrow = $"Routines › {recipe.Name} › Schedule",
            Title = RoutineText.FrequencyInWords(recipe.Cadence),
            Tagline = message ?? RoutineText.ScheduleNote,
            CountLabel = next,
            FooterCount = next,
            BarPlaceholder = $"Time: {recipe.Cadence.LocalTime:HH:mm}",
            Cells = cells,
            Columns = cols,
            RowsPerPage = (cells.Count + cols - 1) / cols,
            Selected = selected,
            AppLabel = "Schedule",
            Hints = new List<LauncherHint>
            {
                new() { Key = "Enter", Label = "choose" },
                new() { Key = "Space", Label = "day on/off" },
                new() { Key = "t", Label = "time" },
                new() { Key = "Esc", Label = "done" },
            },
        };
    }

    public static async Task OpenAsync(CommandContext ctx, Guid routineId, CancellationToken ct)
    {
        var columns = ColumnsFor(ctx);
        string? selectedId = null;
        var selected = 0;
        string? hint = null;
        var first = true;

        while (!ct.IsCancellationRequested)
        {
            var recipe = await RoutineEditorHandler.LoadAsync(ctx, routineId).ConfigureAwait(false);
            if (recipe is null)
            {
                return;
            }

            var screen = Build(recipe, DateTimeOffset.Now, 0, columns, hint);
            hint = null;
            if (first)
            {
                first = false;
                var items = TileGrid.Items(screen);
                var start = items.FirstOrDefault(t => t.Hint.EndsWith(" · current", StringComparison.Ordinal))
                    ?? items.FirstOrDefault(t => !t.Disabled);
                selectedId = start?.Id;
            }

            selected = TileGrid.IndexOfId(screen, selectedId, selected);
            screen = screen with { Selected = selected };
            ctx.Renderer.RenderTileScreen(screen, ctx.GetCurrentRenderOptions());

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            var tile = TileGrid.ItemAt(screen, selected);
            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                return;
            }

            if (command.Type == CommandType.ShowHelp)
            {
                await TileHelp.ShowAsync(
                    ctx,
                    $"Routines › {recipe.Name} › Schedule",
                    [
                        ("ENTER", "Choose a preset or flip a day"),
                        ("SPACE", "Flip a day"),
                        ("T", "Type the time"),
                        ("ESC", "Done"),
                    ],
                    ct).ConfigureAwait(false);
                continue;
            }

            if (command.Type == CommandType.TerminalResized)
            {
                columns = ColumnsFor(ctx);
                continue;
            }

            if (command.RawKeyChar == 't'
                || (selected == TileGrid.Bar && RoutineEditorHandler.IsBarTypingKey(command, out _)))
            {
                var seed = selected == TileGrid.Bar && command.RawKeyChar is { } c && c > ' ' && c != 't' ? c.ToString() : null;
                hint = await EditTimeAsync(ctx, recipe, seed, columns, ct).ConfigureAwait(false);
            }
            else if (command.Type is CommandType.MoveUp or CommandType.MoveDown or CommandType.MoveLeft
                or CommandType.MoveRight or CommandType.ExpandNode or CommandType.CollapseNode)
            {
                selected = TileGrid.Move(screen, command.Type);
            }
            else if (command.Type == CommandType.JumpToIndex)
            {
                selected = TileGrid.Jump(screen, command.Count);
            }
            else if (tile is not null && command.Type is CommandType.ToggleSelection or CommandType.ActivateLink)
            {
                hint = await ActivateAsync(ctx, recipe, tile, command.Type == CommandType.ActivateLink).ConfigureAwait(false);
            }

            selectedId = TileGrid.ItemAt(screen, selected)?.Id;
        }
    }

    private static int ColumnsFor(CommandContext ctx) => ctx.GetCurrentRenderOptions().TerminalWidth >= 100 ? 4 : 2;

    private static TileCell PresetCell(string key, string title, string hint, DayOfWeek[] set, IReadOnlySet<DayOfWeek> current) =>
        new()
        {
            Tile = new Tile
            {
                Id = "preset:" + key,
                Title = title,
                Hint = current.SetEquals(set) ? hint + " · current" : hint,
            },
        };

    private static async Task<string?> ActivateAsync(CommandContext ctx, ScheduleRecipe recipe, Tile tile, bool enter)
    {
        if (tile.Id.StartsWith("day:", StringComparison.Ordinal))
        {
            var day = Enum.Parse<DayOfWeek>(tile.Id["day:".Length..]);
            var days = recipe.Cadence.Days.ToHashSet();
            if (!days.Remove(day))
            {
                days.Add(day);
            }
            else if (days.Count == 0)
            {
                return LastDayHint;
            }

            return await ApplyAsync(ctx, recipe, days, recipe.Cadence.LocalTime).ConfigureAwait(false);
        }

        if (!enter)
        {
            return null;
        }

        DayOfWeek[]? preset = tile.Id switch
        {
            "preset:everyday" => AllDays,
            "preset:weekdays" => Weekdays,
            "preset:weekends" => Weekends,
            _ => null,
        };
        return preset is null ? null : await ApplyAsync(ctx, recipe, preset, recipe.Cadence.LocalTime).ConfigureAwait(false);
    }

    private static async Task<string?> EditTimeAsync(CommandContext ctx, ScheduleRecipe recipe, string? seed, int columns, CancellationToken ct)
    {
        var text = await RoutineEditorHandler.ReadInBarAsync(
            ctx,
            ctx.GetCurrentRenderOptions(),
            "Time: ",
            seed,
            null,
            (_, message) => Build(recipe, DateTimeOffset.Now, TileGrid.Bar, columns, message),
            ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!TimeOnly.TryParse(text, out var time))
        {
            return RoutineText.TimeInvalid;
        }

        return await ApplyAsync(ctx, recipe, recipe.Cadence.Days, time).ConfigureAwait(false);
    }

    private static async Task<string?> ApplyAsync(CommandContext ctx, ScheduleRecipe recipe, IEnumerable<DayOfWeek> days, TimeOnly time)
    {
        try
        {
            recipe.SetCadence(ScheduleEditing.BuildCadence(days, time));
        }
        catch (ArgumentException ex)
        {
            return $"Invalid schedule: {ex.Message}";
        }

        await RoutineEditorHandler.SaveAsync(ctx, recipe).ConfigureAwait(false);
        return null;
    }
}
