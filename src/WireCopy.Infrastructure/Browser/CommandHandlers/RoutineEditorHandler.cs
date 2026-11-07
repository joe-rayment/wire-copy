// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
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
    public static async Task OpenAsync(CommandContext ctx, RenderOptions options, Guid routineId, CancellationToken ct)
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

            var rows = RoutineEditorScreen.BuildRows(recipe, DateTimeOffset.Now);
            selectedRow = RestoreSelection(rows, selectedKind, selectedSite, selectedStep, selectedRow);

            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            ctx.Renderer.LeaveHtmlPanes();
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            var width = Math.Max(20, options.TerminalWidth - 2);
            offset = RoutineEditorScreen.Draw(helpers, palette, width, options.TerminalHeight, recipe.Name, rows, selectedRow, offset);

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                return;
            }

            if (command.Type == CommandType.TerminalResized)
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
                await EditNameAsync(ctx, options, recipe, palette, ct).ConfigureAwait(false);
                break;
            case RoutineRowKind.Podcast:
                await EditPodcastAsync(ctx, options, recipe, palette, ct).ConfigureAwait(false);
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
            default:
                // Frequency and "+ Add a site" are wired by the next slice of this editor.
                break;
        }
    }

    private static async Task EditNameAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, ThemePalette palette, CancellationToken ct)
    {
        var taken = (await GetAllAsync(ctx).ConfigureAwait(false))
            .Where(r => r.Id != recipe.Id)
            .Select(r => r.Name)
            .ToList();
        var field = new FormFieldConfig
        {
            Label = "Name",
            HelpText = "Enter to save · Esc to cancel",
            MaxLength = 80,
            InitialValue = recipe.Name,
            Validate = v => NameProblem(v, taken),
        };
        var input = await PromptAsync(ctx, options, field, palette, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        var newName = input.Trim();
        var podcast = string.Equals(recipe.OutputCollectionName, recipe.Name, StringComparison.Ordinal)
            ? newName
            : recipe.OutputCollectionName;
        recipe.Rename(newName);
        await SaveAsync(ctx, Rebuild(recipe, podcast)).ConfigureAwait(false);
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
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, ThemePalette palette, CancellationToken ct)
    {
        var field = new FormFieldConfig
        {
            Label = "Podcast",
            HelpText = "The podcast the episodes go into · Enter to save · Esc to cancel",
            MaxLength = 80,
            InitialValue = recipe.OutputCollectionName,
            Validate = v => string.IsNullOrWhiteSpace(v) ? "Podcast name can't be empty" : null,
        };
        var input = await PromptAsync(ctx, options, field, palette, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        await SaveAsync(ctx, Rebuild(recipe, input.Trim())).ConfigureAwait(false);
    }

    /// <summary>Inline text prompt (same placement as the Settings rows); null on Esc.</summary>
    private static Task<string?> PromptAsync(
        CommandContext ctx, RenderOptions options, FormFieldConfig field, ThemePalette palette, CancellationToken ct)
    {
        var startRow = Math.Max(1, (options.TerminalHeight / 2) - 3);
        var fieldWidth = Math.Min(UI.OverlayViewport.Width - 6, 60);
        return FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct);
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
