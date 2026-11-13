// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Scheduling;
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
/// "+ New routine" card, then the routines sorted by name).
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
        var tracker = scope.ServiceProvider.GetRequiredService<IRoutineRunTracker>();
        Task<NavigationCommand>? inputTask = null;
        string? busyHint = null;
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
                var running = tracker.Active.ToDictionary(x => x.RecipeId);
                var screen = RoutinesHomePage.Build(recipes, running, alerts, badge, now, sel);
                if (busyHint is not null)
                {
                    screen = screen with { HintRow = busyHint };
                    busyHint = null;
                }

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

                inputTask ??= RoutineEditorHandler.TakeParked(ctx) ?? ctx.InputHandler.WaitForInputAsync(ct);
                await RoutineEditorHandler.WakeAsync(inputTask, tracker, running.Count > 0, ct).ConfigureAwait(false);

                if (!inputTask.IsCompleted)
                {
                    continue;
                }

                var command = await inputTask.ConfigureAwait(false);
                inputTask = null;
                var key = command.RawKeyChar;
                var current = sel == TileGrid.Bar ? null : recipes.FirstOrDefault(r => selId == "routine:" + r.Id);

                if (command.Type == CommandType.ShowHelp)
                {
                    await TileHelp.ShowAsync(
                        ctx,
                        "Routines",
                        [
                            ("ENTER", "Open the routine"),
                            ("1-9", "Jump to a routine"),
                            ("N", "Name a new routine"),
                            ("R", "Run it now"),
                            ("SPACE", "Turn it on or off"),
                            ("D", "Delete it"),
                            ("E", "See its last run"),
                            ("← ↑ ↓ →", "Move"),
                            ("ESC", "Back to the launcher"),
                        ],
                        ct).ConfigureAwait(false);
                    continue;
                }

                if (sel == TileGrid.Bar && RoutineEditorHandler.IsBarTypingKey(command, out var seed))
                {
                    var made = await CreateAsync(ctx, options, store, recipes, seed, ct).ConfigureAwait(false);
                    recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                    if (made is { } madeId)
                    {
                        selId = "routine:" + madeId;
                        sel = TileGrid.IndexOfId(RoutinesHomePage.Build(recipes, new Dictionary<Guid, RoutineRunProgress>(), null, null, DateTimeOffset.Now, 0), selId, 0);
                    }

                    continue;
                }

                // KEY READING RULE: raw characters first (the mapper may bind them to other commands).
                switch (key)
                {
                    case 'n':
                        sel = TileGrid.Bar;
                        selId = null; // an explicit move: do not snap back to the previous tile
                        continue;
                    case >= '1' and <= '9' when command.Type != CommandType.JumpToIndex:
                        sel = TileGrid.Jump(screen, (key ?? '0') - '0');
                        selId = null; // an explicit move: do not snap back to the previous tile
                        continue;
                    case 'r' when current != null:
                        if (runNow.StartInBackground(current) == RunNowOutcome.Busy)
                        {
                            busyHint = RoutineText.Busy;
                        }

                        await RoutineRunDetailsHandler.OpenAsync(ctx, options, current.Id, ct).ConfigureAwait(false);
                        recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                        continue;
                    case 'e' when current != null:
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
                        selId = null; // an explicit move: do not snap back to the previous tile
                        continue;
                    case 'l':
                        sel = TileGrid.Move(screen, CommandType.MoveRight);
                        selId = null; // an explicit move: do not snap back to the previous tile
                        continue;
                    case 'k':
                        sel = TileGrid.Move(screen, CommandType.MoveUp);
                        selId = null; // an explicit move: do not snap back to the previous tile
                        continue;
                    case 'j':
                        sel = TileGrid.Move(screen, CommandType.MoveDown);
                        selId = null; // an explicit move: do not snap back to the previous tile
                        continue;
                    default:
                        break;
                }

                switch (command.Type)
                {
                    case CommandType.GoBack when sel == TileGrid.Bar && recipes.Count > 0:
                        sel = 0;
                        selId = null;
                        break;
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
                        selId = null; // an explicit move: do not snap back to the previous tile
                        break;
                    case CommandType.JumpToIndex:
                        sel = TileGrid.Jump(screen, command.Count);
                        selId = null; // an explicit move: do not snap back to the previous tile
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
                    case CommandType.ActivateLink when current != null && (alerts.ContainsKey(current.Id) || running.ContainsKey(current.Id)):
                        await RoutineRunDetailsHandler.OpenAsync(ctx, options, current.Id, ct).ConfigureAwait(false);
                        recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                        break;
                    case CommandType.ActivateLink when current != null:
                        await RoutineEditorHandler.OpenAsync(ctx, options, current.Id, ct).ConfigureAwait(false);
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
        string seed,
        CancellationToken ct)
    {
        var taken = recipes.Select(r => r.Name).ToList();
        var hint = "Name the routine, then pick what it reads";
        var typed = seed;
        string name;
        while (true)
        {
            var input = await RoutineEditorHandler.ReadInBarAsync(
                ctx,
                ctx.GetCurrentRenderOptions(),
                string.Empty,
                typed,
                hint,
                (_, hintRow) => RoutinesHomePage.Build(recipes, new Dictionary<Guid, RoutineRunProgress>(), null, null, DateTimeOffset.Now, TileGrid.Bar) with { HintRow = hintRow },
                ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            name = input.Trim();
            var problem = name.Length > 80 ? "Keep the name to 80 characters" : RoutineEditorHandler.NameProblem(name, taken);
            if (problem is null)
            {
                break;
            }

            hint = problem;
            typed = input;
        }

        ctx.Renderer.HideLauncherPane();
        var recipe = ScheduleRecipe.Create(name, Cadence.Daily(new TimeOnly(5, 0)), Array.Empty<RecipeStep>(), name, true);
        await store.SaveAsync(recipe).ConfigureAwait(false);
        var url = await RoutineSitePicker.PickAsync(ctx, name, ct, recipe).ConfigureAwait(false);
        string? openProblem = null;
        if (url is not null)
        {
            openProblem = await RoutinePickModeHandler.OpenReportingAsync(ctx, options, new RoutinePickRequest(recipe.Id, url, null), ct).ConfigureAwait(false);
        }

        await RoutineEditorHandler.OpenAsync(ctx, options, recipe.Id, ct, "Run now to hear it — press r", openProblem).ConfigureAwait(false);
        return recipe.Id;
    }
}
