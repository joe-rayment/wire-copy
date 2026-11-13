// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.Enums.Scheduling;
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
    public static async Task OpenAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
        var runNow = scope.ServiceProvider.GetRequiredService<IScheduleRunNow>();
        var modeService = scope.ServiceProvider.GetRequiredService<IUiModeService>();

        modeService.Set(UiMode.Routine);
        var running = new HashSet<Guid>();
        var runs = new Dictionary<Guid, Task<RunNowResult>>();
        Task<NavigationCommand>? inputTask = null;
        try
        {
            var recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
            var sel = 0;
            string? selId = null;
            await RoutineRunDetailsHandler.AcknowledgeAsync(ctx, r => recipes.All(x => x.Id != r.RecipeId)).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var (alerts, badge) = await LoadAttentionAsync(ctx, recipes).ConfigureAwait(false);
                var now = DateTimeOffset.Now;
                var screen = RoutinesHomePage.Build(recipes, running, alerts, badge, now, sel);
                if (recipes.Count == 0)
                {
                    sel = TileGrid.Bar;
                }
                else if (sel != TileGrid.Bar)
                {
                    sel = TileGrid.IndexOfId(screen, selId, sel);
                }

                screen = screen with { Selected = sel };
                selId = sel == TileGrid.Bar ? null : TileGrid.ItemAt(screen, sel)?.Id;
                ctx.Renderer.RenderTileScreen(screen, ctx.GetCurrentRenderOptions());

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
                var current = sel == TileGrid.Bar ? null : recipes.FirstOrDefault(r => selId == "routine:" + r.Id);

                // KEY READING RULE: raw characters first (the mapper may bind them to other commands).
                switch (key)
                {
                    case 'n':
                        sel = TileGrid.Bar;
                        continue;
                    case >= '1' and <= '9' when command.Type != CommandType.JumpToIndex:
                        sel = TileGrid.Jump(screen, (key ?? '0') - '0');
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
                        var ok = await TileConfirm.AskAsync(ctx, screen, RoutineText.DeleteConfirm(current.Name), ct).ConfigureAwait(false);
                        if (ok)
                        {
                            await store.DeleteAsync(current.Id).ConfigureAwait(false);
                            var deletedId = current.Id;
                            await RoutineRunDetailsHandler.AcknowledgeAsync(ctx, r => r.RecipeId == deletedId).ConfigureAwait(false);
                            recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                        }

                        continue;
                    case 'h':
                        sel = TileGrid.Move(screen, CommandType.MoveLeft);
                        continue;
                    case 'l':
                        sel = TileGrid.Move(screen, CommandType.MoveRight);
                        continue;
                    case 'k':
                        sel = TileGrid.Move(screen, CommandType.MoveUp);
                        continue;
                    case 'j':
                        sel = TileGrid.Move(screen, CommandType.MoveDown);
                        continue;
                    default:
                        break;
                }

                switch (command.Type)
                {
                    case CommandType.GoBack or CommandType.Quit:
                        return;
                    case CommandType.MoveLeft or CommandType.CollapseNode
                        or CommandType.MoveRight or CommandType.ExpandNode
                        or CommandType.MoveUp or CommandType.MoveDown:
                        sel = TileGrid.Move(screen, command.Type switch
                        {
                            CommandType.CollapseNode => CommandType.MoveLeft,
                            CommandType.ExpandNode => CommandType.MoveRight,
                            _ => command.Type,
                        });
                        break;
                    case CommandType.JumpToIndex:
                        sel = TileGrid.Jump(screen, command.Count);
                        break;
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
                        if (made is { } madeId)
                        {
                            selId = "routine:" + madeId;
                            sel = TileGrid.IndexOfId(RoutinesHomePage.Build(recipes, running, null, null, DateTimeOffset.Now, 0), selId, 0);
                        }

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

    /// <summary>
    /// Re-read on every frame (cheap, local store) so it follows creates, deletes, finished runs
    /// and acknowledgements: per-card alert lines from each routine's newest run, and the header
    /// badge in Routines-home wording.
    /// </summary>
    private static async Task<(Dictionary<Guid, string> Alerts, string? Badge)> LoadAttentionAsync(
        CommandContext ctx, IReadOnlyList<ScheduleRecipe> recipes)
    {
        var alerts = new Dictionary<Guid, string>();
        using var scope = ctx.ScopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetService<IScheduledRunRepository>();
        if (repo is null)
        {
            return (alerts, null);
        }

        foreach (var recipeId in recipes.Select(r => r.Id))
        {
            var newest = (await repo.GetRecentForRecipeAsync(recipeId, 1).ConfigureAwait(false)).FirstOrDefault();
            if (newest is not null && ScheduledRunBadge.NeedsAttention(newest))
            {
                alerts[recipeId] = RoutineText.CardAlert(
                    RoutineRunDiagnosis.Diagnose(newest).Short, newest.Status == ScheduledRunStatus.PartialSuccess);
            }
        }

        var unacked = await repo.GetUnacknowledgedFinishedRunsAsync().ConfigureAwait(false);
        return (alerts, ScheduledRunBadge.Describe(unacked, onRoutinesHome: true));
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
