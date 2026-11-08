// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// The routine editor (workspace-wyvga.5): a Settings-style row screen painted in the terminal.
/// The caller (Routines home) has already turned routine colours on; this never touches the mode.
/// Rows and drawing live in <see cref="RoutineEditorScreen"/>. Every change saves at once.
/// </summary>
internal static class RoutineEditorHandler
{
    public static async Task OpenAsync(CommandContext ctx, RenderOptions options, Guid routineId, CancellationToken ct, bool nameNew = false)
    {
        var selectedRow = -1;
        var offset = 0;
        var selectedKind = RoutineRowKind.Name;
        string? selectedSite = null;
        var selectedStep = -1;

        while (!ct.IsCancellationRequested)
        {
            var recipe = await LoadAsync(ctx, routineId).ConfigureAwait(false);
            if (recipe is null)
            {
                return;
            }

            if (nameNew)
            {
                nameNew = false;
                if (await EditNameAsync(ctx, options, recipe, ghostDefault: true, ct).ConfigureAwait(false))
                {
                    selectedKind = RoutineRowKind.AddSite;
                    selectedSite = null;
                    selectedStep = -1;
                }

                continue;
            }

            var rows = RoutineEditorScreen.BuildRows(recipe, DateTimeOffset.Now);
            selectedRow = RestoreSelection(rows, selectedKind, selectedSite, selectedStep, selectedRow);

            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            ctx.Renderer.LeaveHtmlPanes();
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            var width = Math.Max(20, options.TerminalWidth - 2);
            offset = RoutineEditorScreen.Draw(helpers, palette, width, options.TerminalHeight, recipe.Name, rows, selectedRow, offset);

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            if (command.RawKeyChar == 'x')
            {
                await RemoveAsync(ctx, recipe, rows[selectedRow], palette, ct).ConfigureAwait(false);
            }
            else if (command.RawKeyChar == 'r')
            {
                await RunNowAsync(ctx, recipe).ConfigureAwait(false);
            }
            else if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                return;
            }
            else if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
            }
            else if (command.Type == CommandType.MoveDown)
            {
                selectedRow = RoutineEditorScreen.Move(rows, selectedRow, 1);
            }
            else if (command.Type == CommandType.MoveUp)
            {
                selectedRow = RoutineEditorScreen.Move(rows, selectedRow, -1);
            }
            else if (command.Type == CommandType.OpenCommandLine)
            {
                var input = await ctx.InputHandler.PromptForInputAsync(":", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(input))
                {
                    await SearchCommandHandler.HandleCommandLineInput(ctx, input.Trim(), options, ct).ConfigureAwait(false);
                }
            }
            else if (command.Type == CommandType.ActivateLink)
            {
                await ActivateAsync(ctx, options, recipe, rows[selectedRow], palette, ct).ConfigureAwait(false);
            }

            var current = rows[Math.Clamp(selectedRow, 0, rows.Count - 1)];
            selectedKind = current.Kind;
            selectedSite = current.SiteUrl;
            selectedStep = current.StepIndex;
        }
    }

    private static async Task ActivateAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, RoutineRow row, ThemePalette palette, CancellationToken ct)
    {
        switch (row.Kind)
        {
            case RoutineRowKind.Name:
                await EditNameAsync(ctx, options, recipe, ghostDefault: false, ct).ConfigureAwait(false);
                break;
            case RoutineRowKind.Podcast:
                await EditPodcastAsync(ctx, options, recipe, ct).ConfigureAwait(false);
                break;
            case RoutineRowKind.Enabled:
                if (recipe.Enabled)
                {
                    recipe.Disable();
                }
                else
                {
                    recipe.Enable();
                }

                await SaveAsync(ctx, recipe).ConfigureAwait(false);
                break;
            case RoutineRowKind.Site or RoutineRowKind.Section:
                await RoutinePickModeHandler.OpenAsync(
                    ctx, options, new RoutinePickRequest(recipe.Id, row.SiteUrl!, null), ct).ConfigureAwait(false);
                break;
            case RoutineRowKind.Frequency:
                await EditFrequencyAsync(ctx, options, recipe, palette, ct).ConfigureAwait(false);
                break;
            case RoutineRowKind.AddSite:
                await AddSiteAsync(ctx, options, recipe, ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task EditFrequencyAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, ThemePalette palette, CancellationToken ct)
    {
        var labels = new List<string> { "Every day", "Weekdays (Mon–Fri)", "Weekends (Sat/Sun)", "Pick days…" };
        var pick = await RoutineEditorCards.PickAsync(ctx, options, palette, "How often?", "↑/↓ move · Enter select · Esc back", labels, ct).ConfigureAwait(false);
        if (pick is null)
        {
            return;
        }

        IReadOnlyList<DayOfWeek> days;
        switch (pick.Value)
        {
            case 0:
                days = Enum.GetValues<DayOfWeek>();
                break;
            case 1:
                days = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
                break;
            case 2:
                days = new[] { DayOfWeek.Saturday, DayOfWeek.Sunday };
                break;
            default:
                var picked = await RoutineEditorCards.PickDaysAsync(ctx, options, palette, recipe.Cadence.Days, ct).ConfigureAwait(false);
                if (picked is null || picked.Count == 0)
                {
                    return;
                }

                days = picked;
                break;
        }

        var text = await EditInRowAsync(
            ctx,
            options,
            recipe,
            RoutineRowKind.Frequency,
            "at ",
            RoutineText.TimeEditHint,
            recipe.Cadence.LocalTime.ToString("HH:mm"),
            null,
            v => TimeOnly.TryParse(v, out _) ? null : RoutineText.TimeInvalid,
            ct).ConfigureAwait(false);
        if (text is null)
        {
            return;
        }

        if (!TimeOnly.TryParse(text, out var time))
        {
            return;
        }

        try
        {
            recipe.SetCadence(ScheduleEditing.BuildCadence(days, time));
        }
        catch (ArgumentException ex)
        {
            ctx.NavigationService.SetStatusMessage($"Invalid cadence: {ex.Message}", TimeSpan.FromSeconds(6), StatusSeverity.Error);
            return;
        }

        await SaveAsync(ctx, recipe).ConfigureAwait(false);
    }

    private static async Task AddSiteAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, CancellationToken ct)
    {
        var url = await RoutineSitePicker.PickAsync(ctx, recipe.Name, ct).ConfigureAwait(false);
        if (url is null)
        {
            return;
        }

        await RoutinePickModeHandler.OpenAsync(ctx, options, new RoutinePickRequest(recipe.Id, url, null), ct).ConfigureAwait(false);
    }

    private static async Task RemoveAsync(
        CommandContext ctx, ScheduleRecipe recipe, RoutineRow row, ThemePalette palette, CancellationToken ct)
    {
        List<int> victims;
        string label;
        if (row.Kind == RoutineRowKind.Section)
        {
            victims = new List<int> { row.StepIndex };
            label = $"{row.Label} · {row.Value}";
        }
        else if (row.Kind == RoutineRowKind.Site)
        {
            var first = recipe.Steps.FirstOrDefault(s => s.SourceUrl == row.SiteUrl);
            if (first is null)
            {
                return;
            }

            victims = Enumerable.Range(0, recipe.Steps.Count)
                .Where(i => recipe.Steps[i].Domain == first.Domain && recipe.Steps[i].ConfigUrlPattern == first.ConfigUrlPattern)
                .ToList();
            label = row.Label;
        }
        else
        {
            return;
        }

        var confirmed = await ConfirmationDialog.ConfirmAsync(
            ctx.InputHandler, "Remove", RoutineText.RemoveConfirm(label), palette, ct, isDestructive: true).ConfigureAwait(false);
        if (!confirmed)
        {
            return;
        }

        foreach (var index in victims.OrderByDescending(i => i))
        {
            recipe.RemoveStep(index);
        }

        await SaveAsync(ctx, recipe).ConfigureAwait(false);
    }

    private static async Task RunNowAsync(CommandContext ctx, ScheduleRecipe recipe)
    {
        ctx.NavigationService.SetStatusMessage(RoutineText.Running(recipe.Name), TimeSpan.FromMinutes(10));
        RunNowResult result;
        using (var scope = ctx.ScopeFactory.CreateScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<IScheduleRunNow>().RunAsync(recipe, CancellationToken.None).ConfigureAwait(false);
        }

        if (result.Outcome == RunNowOutcome.Busy)
        {
            ctx.NavigationService.SetStatusMessage(RoutineText.Busy, TimeSpan.FromSeconds(6));
            return;
        }

        var reloaded = await LoadAsync(ctx, recipe.Id).ConfigureAwait(false) ?? recipe;
        ctx.NavigationService.SetStatusMessage(RoutineText.LastResult(reloaded, DateTimeOffset.Now), TimeSpan.FromSeconds(10));
    }

    /// <summary>Edits the Name row in place; true when a name was committed (a valid typed name).</summary>
    private static async Task<bool> EditNameAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, bool ghostDefault, CancellationToken ct)
    {
        var taken = (await GetAllAsync(ctx).ConfigureAwait(false))
            .Where(r => r.Id != recipe.Id)
            .Select(r => r.Name)
            .ToList();
        var input = await EditInRowAsync(
            ctx,
            options,
            recipe,
            RoutineRowKind.Name,
            string.Empty,
            RoutineText.NameEditHint,
            ghostDefault ? null : recipe.Name,
            ghostDefault ? recipe.Name : null,
            v => NameProblem(v, taken),
            ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var newName = input.Trim()[..Math.Min(input.Trim().Length, 80)];
        var podcast = string.Equals(recipe.OutputCollectionName, recipe.Name, StringComparison.Ordinal)
            ? newName
            : recipe.OutputCollectionName;
        recipe.Rename(newName);
        await SaveAsync(ctx, Rebuild(recipe, podcast)).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Draws the editor with <paramref name="kind"/>'s row in edit mode and reads the text right there in that row.
    /// Re-asks (error shown under the row) while <paramref name="validate"/> fails. Null on Esc / empty (keep).
    /// </summary>
    private static async Task<string?> EditInRowAsync(
        CommandContext ctx,
        RenderOptions options,
        ScheduleRecipe recipe,
        RoutineRowKind kind,
        string prefix,
        string hint,
        string? initial,
        string? ghostDefault,
        Func<string, string?> validate,
        CancellationToken ct)
    {
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var view = new RoutineEditView(kind, prefix, hint);
        var offset = 0;
        while (!ct.IsCancellationRequested)
        {
            var rows = RoutineEditorScreen.BuildRows(recipe, DateTimeOffset.Now);
            var selected = Math.Max(0, rows.FindIndex(r => r.Kind == kind));
            ctx.Renderer.LeaveHtmlPanes();
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            var width = Math.Max(20, options.TerminalWidth - 2);
            offset = RoutineEditorScreen.Draw(helpers, palette, width, options.TerminalHeight, recipe.Name, rows, selected, offset, view);

            var clearWidth = Math.Max(8, width - view.InputCol - 2);
            string? value;
            if (ctx.InputHandler is TerminalInputHandler terminal)
            {
                value = await terminal.PromptForRowInputAsync(ct, view.ScreenRow, view.InputCol, initial, ghostDefault, clearWidth).ConfigureAwait(false);
            }
            else
            {
                value = await ctx.InputHandler.PromptForInputAsync(string.Empty, ct, false, view.ScreenRow, view.InputCol, initial, null).ConfigureAwait(false);
            }

            if (value is null)
            {
                return null;
            }

            view.Error = validate(value);
            if (view.Error is null)
            {
                return value;
            }

            initial = value;
            ghostDefault = null;
        }

        return null;
    }

    private static string? NameProblem(string value, List<string> taken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RoutineText.NameEmpty;
        }

        return taken.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? RoutineText.NameTaken : null;
    }

    private static async Task EditPodcastAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, CancellationToken ct)
    {
        var input = await EditInRowAsync(
            ctx,
            options,
            recipe,
            RoutineRowKind.Podcast,
            string.Empty,
            RoutineText.PodcastEditHint,
            recipe.OutputCollectionName,
            null,
            v => string.IsNullOrWhiteSpace(v) ? RoutineText.PodcastEmpty : null,
            ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        await SaveAsync(ctx, Rebuild(recipe, input.Trim())).ConfigureAwait(false);
    }

    /// <summary>The recipe with a new OutputCollectionName (no setter exists, so it is rehydrated; id, steps and run state carry over).</summary>
    private static ScheduleRecipe Rebuild(ScheduleRecipe r, string outputCollectionName) =>
        ScheduleRecipe.Rehydrate(
            r.Id, r.Name, r.Enabled, r.Cadence, r.Steps, outputCollectionName, r.RunState, r.Version);

    private static async Task<ScheduleRecipe?> LoadAsync(CommandContext ctx, Guid id)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IScheduleStore>().GetAsync(id).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ScheduleRecipe>> GetAllAsync(CommandContext ctx)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IScheduleStore>().GetAllAsync().ConfigureAwait(false);
    }

    private static async Task SaveAsync(CommandContext ctx, ScheduleRecipe recipe)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IScheduleStore>().SaveAsync(recipe).ConfigureAwait(false);
    }

    /// <summary>After a reload, keep the cursor on the same logical row (kind + site + step), else the old index, else the first selectable.</summary>
    private static int RestoreSelection(IReadOnlyList<RoutineRow> rows, RoutineRowKind kind, string? site, int step, int previous)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind == kind && rows[i].SiteUrl == site && rows[i].StepIndex == step)
            {
                return i;
            }
        }

        if (previous >= 0 && previous < rows.Count && rows[previous].Selectable)
        {
            return previous;
        }

        return Math.Max(0, RoutineEditorScreen.FirstSelectable(rows));
    }
}
