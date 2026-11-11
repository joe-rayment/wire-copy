// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-wyvga.4 (R4) — the Routines home: a 2-column card grid (index 0 is the
/// "+ New routine" card, then the routines in <see cref="LauncherRenderer.SortedRoutines"/> order).
/// </summary>
internal static class RoutinesHomeHandler
{
    private const int Columns = 2;

    public static async Task OpenAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
        var runNow = scope.ServiceProvider.GetRequiredService<IScheduleRunNow>();
        var modeService = scope.ServiceProvider.GetRequiredService<IUiModeService>();
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

        modeService.Set(UiMode.Routine);
        var running = new HashSet<Guid>();
        var runs = new Dictionary<Guid, Task<RunNowResult>>();
        Task<NavigationCommand>? inputTask = null;
        try
        {
            var recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
            var sel = 0;
            await RoutineRunDetailsHandler.AcknowledgeAsync(ctx, r => recipes.All(x => x.Id != r.RecipeId)).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                sel = Math.Clamp(sel, 0, recipes.Count);
                ctx.Renderer.RenderRoutinesHome(recipes, running, sel, DateTimeOffset.Now, ctx.GetCurrentRenderOptions());

                inputTask ??= ctx.InputHandler.WaitForInputAsync(ct);
                await Task.WhenAny(new Task[] { inputTask }.Concat(runs.Values)).ConfigureAwait(false);

                // Any finished run: drop it from "running", surface Busy, refresh from the store.
                var done = runs.Where(kv => kv.Value.IsCompleted).ToList();
                if (done.Count > 0)
                {
                    foreach (var (id, task) in done)
                    {
                        runs.Remove(id);
                        running.Remove(id);
                        if (task.IsCompletedSuccessfully && task.Result.Outcome == RunNowOutcome.Busy)
                        {
                            ctx.NavigationService.SetStatusMessage(RoutineText.Busy, TimeSpan.FromSeconds(6));
                        }
                        else if (task.IsCompletedSuccessfully && task.Result.Run is { } finished)
                        {
                            var d = RoutineRunDiagnosis.Diagnose(finished);
                            if (d.NeedsAttention)
                            {
                                var runName = recipes.FirstOrDefault(r => r.Id == id)?.Name ?? string.Empty;
                                ctx.NavigationService.SetStatusMessage(
                                    RoutineText.RunNeedsAttention(runName, d.Short), TimeSpan.FromSeconds(10), StatusSeverity.Error);
                            }
                        }
                    }

                    recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                }

                if (!inputTask.IsCompleted)
                {
                    continue;
                }

                var command = await inputTask.ConfigureAwait(false);
                inputTask = null;
                var key = command.RawKeyChar;
                var sorted = LauncherRenderer.SortedRoutines(recipes);
                var current = sel > 0 && sel <= sorted.Count ? sorted[sel - 1] : null;
                var total = recipes.Count + 1;

                // KEY READING RULE: raw characters first (the mapper may bind them to other commands).
                switch (key)
                {
                    case 'n':
                        var made = await CreateAsync(ctx, options, store, recipes, ct).ConfigureAwait(false);
                        recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                        sel = Reselect(recipes, made, sel);
                        continue;
                    case 'r' when current != null:
                        if (running.Add(current.Id))
                        {
                            runs[current.Id] = runNow.RunAsync(current, CancellationToken.None);
                        }

                        continue;
                    case 'e' when current != null:
                        ctx.Renderer.HideLauncherPane();
                        await RoutineRunDetailsHandler.OpenAsync(ctx, options, current.Id, ct).ConfigureAwait(false);
                        recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                        continue;
                    case 'd' when current != null:
                        ctx.Renderer.HideLauncherPane();
                        var ok = await ConfirmationDialog.ConfirmAsync(
                            ctx.InputHandler, "Delete routine", RoutineText.DeleteConfirm(current.Name), palette, ct, isDestructive: true).ConfigureAwait(false);
                        if (ok)
                        {
                            await store.DeleteAsync(current.Id).ConfigureAwait(false);
                            var deletedId = current.Id;
                            await RoutineRunDetailsHandler.AcknowledgeAsync(ctx, r => r.RecipeId == deletedId).ConfigureAwait(false);
                            recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                        }

                        continue;
                    case 'h': sel = Math.Max(0, sel - 1); continue;
                    case 'l': sel = Math.Min(total - 1, sel + 1); continue;
                    case 'k': sel = sel - Columns >= 0 ? sel - Columns : sel; continue;
                    case 'j': sel = sel + Columns < total ? sel + Columns : sel; continue;
                    default:
                        break;
                }

                switch (command.Type)
                {
                    case CommandType.GoBack or CommandType.Quit:
                        return;
                    case CommandType.MoveLeft or CommandType.CollapseNode: sel = Math.Max(0, sel - 1); break;
                    case CommandType.MoveRight or CommandType.ExpandNode: sel = Math.Min(total - 1, sel + 1); break;
                    case CommandType.MoveUp: sel = sel - Columns >= 0 ? sel - Columns : sel; break;
                    case CommandType.MoveDown: sel = sel + Columns < total ? sel + Columns : sel; break;
                    case CommandType.ToggleSelection when current != null:
                        if (current.Enabled)
                        {
                            current.Disable();
                        }
                        else
                        {
                            current.Enable();
                        }

                        await store.SaveAsync(current).ConfigureAwait(false);
                        recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                        break;
                    case CommandType.ActivateLink when current == null:
                        var made = await CreateAsync(ctx, options, store, recipes, ct).ConfigureAwait(false);
                        recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                        sel = Reselect(recipes, made, sel);
                        break;
                    case CommandType.ActivateLink:
                        await RoutineEditorHandler.OpenAsync(ctx, options, current!.Id, ct).ConfigureAwait(false);
                        recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                        break;
                    default:
                        break;
                }
            }
        }
        finally
        {
            modeService.Set(UiMode.Normal);
            if (ctx.NavigationService.InLauncherMode)
            {
                ctx.NavigationService.LauncherSelectedIndex = LauncherSlotMap.RoutinesIndex(ctx.Bookmarks?.Count ?? 0);
            }

            await ctx.RenderCurrentPageAsync(ctx.GetCurrentRenderOptions(), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static int Reselect(IReadOnlyList<ScheduleRecipe> recipes, Guid? created, int sel)
    {
        if (created is null)
        {
            return sel;
        }

        var idx = LauncherRenderer.SortedRoutines(recipes).FindIndex(r => r.Id == created);
        return idx >= 0 ? idx + 1 : sel;
    }

    private static async Task<Guid?> CreateAsync(
        CommandContext ctx,
        RenderOptions options,
        IScheduleStore store,
        List<ScheduleRecipe> recipes,
        CancellationToken ct)
    {
        ctx.Renderer.HideLauncherPane();
        var name = RoutineText.UniqueDefaultName(recipes.Select(r => r.Name));
        var recipe = ScheduleRecipe.Create(name, Cadence.Daily(new TimeOnly(5, 0)), Array.Empty<RecipeStep>(), name, true);
        await store.SaveAsync(recipe).ConfigureAwait(false);
        await RoutineEditorHandler.OpenAsync(ctx, options, recipe.Id, ct, nameNew: true).ConfigureAwait(false);
        return recipe.Id;
    }
}
