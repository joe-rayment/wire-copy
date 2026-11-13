// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// The routine page is a tile screen built by RoutinePage and drawn through RenderTileScreen.
/// Every change saves at once.
/// </summary>
internal static class RoutineEditorHandler
{
    public static async Task OpenAsync(CommandContext ctx, RenderOptions options, Guid routineId, CancellationToken ct, string? firstHint = null, string? openingProblem = null)
    {
        var selected = 0;
        string? selectedId = null;
        string? barError = openingProblem;
        using var trackerScope = ctx.ScopeFactory.CreateScope();
        var tracker = trackerScope.ServiceProvider.GetRequiredService<IRoutineRunTracker>();
        Task<NavigationCommand>? inputTask = null;

        while (!ct.IsCancellationRequested)
        {
            var recipe = await LoadAsync(ctx, routineId).ConfigureAwait(false);
            if (recipe is null)
            {
                return;
            }

            var lastRun = await LoadLastRunAsync(ctx, routineId).ConfigureAwait(false);
            var screen = RoutinePage.Build(recipe, lastRun, DateTimeOffset.Now, ctx.Bookmarks?.ToList() ?? new List<Bookmark>(), 0, barError ?? (recipe.Steps.Count > 0 ? firstHint : null), tracker.Get(routineId));
            barError = null;
            selected = TileGrid.IndexOfId(screen, selectedId, selected);
            screen = screen with { Selected = selected };
            ctx.Renderer.RenderTileScreen(screen, ctx.GetCurrentRenderOptions());

            inputTask ??= ctx.InputHandler.WaitForInputAsync(ct);
            await WakeAsync(inputTask, tracker, tracker.Get(routineId) is not null, ct).ConfigureAwait(false);
            if (!inputTask.IsCompleted)
            {
                continue;
            }

            var command = await inputTask.ConfigureAwait(false);
            inputTask = null;
            if (command.Type != CommandType.TerminalResized)
            {
                firstHint = null;
            }

            var tile = TileGrid.ItemAt(screen, selected);
            if (command.Type == CommandType.ShowHelp)
            {
                await TileHelp.ShowAsync(
                    ctx,
                    $"Routines › {recipe.Name}",
                    [
                        ("ENTER", "Open or change what is lit"),
                        ("1-9", "Jump to a tile"),
                        ("A", "Add a site"),
                        ("D", "Remove the lit site"),
                        ("J / K", "Move the lit site later / earlier"),
                        ("R", "Run now"),
                        ("SPACE", "Turn the routine on or off"),
                        ("↑", "To the bar, then type a site's address"),
                        ("ESC", "Back to Routines"),
                    ],
                    ct).ConfigureAwait(false);
                continue;
            }

            if (selected == TileGrid.Bar && IsBarTypingKey(command, out var seed))
            {
                var before = SiteIds(screen);
                barError = await AddSiteFromBarAsync(ctx, options, recipe, seed, ct).ConfigureAwait(false);
                if (barError is null && await NewSiteIdAsync(ctx, routineId, before, tracker).ConfigureAwait(false) is { } added)
                {
                    selectedId = added;
                    selected = 0;
                    continue;
                }
            }
            else if (command.RawKeyChar is 'J' or 'K' && tile is not null && tile.Id.StartsWith("site:", StringComparison.Ordinal))
            {
                await MoveSiteAsync(ctx, recipe, tile.Id["site:".Length..], command.RawKeyChar == 'K' ? -1 : 1).ConfigureAwait(false);
            }
            else if (command.Type == CommandType.ToggleSelection)
            {
                if (recipe.Enabled)
                {
                    recipe.Disable();
                }
                else
                {
                    recipe.Enable();
                }

                await SaveAsync(ctx, recipe).ConfigureAwait(false);
            }
            else if (command.RawKeyChar == 'd' && tile is not null && tile.Id.StartsWith("site:", StringComparison.Ordinal))
            {
                await RemoveAsync(ctx, screen, recipe, tile, ct).ConfigureAwait(false);
            }
            else if (command.RawKeyChar == 'r')
            {
                await StartRunAndOpenAsync(ctx, options, recipe, ct).ConfigureAwait(false);
            }
            else if (command.RawKeyChar == 'a')
            {
                var before = SiteIds(screen);
                barError = await AddSiteAsync(ctx, options, recipe, ct).ConfigureAwait(false);
                if (await NewSiteIdAsync(ctx, routineId, before, tracker).ConfigureAwait(false) is { } added)
                {
                    selectedId = added;
                    selected = 0;
                    continue;
                }
            }
            else if (selected == TileGrid.Bar && command.Type == CommandType.GoBack && TileGrid.Items(screen).Count > 0)
            {
                selected = 0;
            }
            else if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                return;
            }
            else if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
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
            else if (command.Type == CommandType.OpenCommandLine)
            {
                var input = await ctx.InputHandler.PromptForInputAsync(":", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(input))
                {
                    await SearchCommandHandler.HandleCommandLineInput(ctx, input.Trim(), options, ct).ConfigureAwait(false);
                }
            }
            else if (command.Type == CommandType.ActivateLink && tile is not null)
            {
                await ActivateTileAsync(ctx, options, recipe, tile, ct).ConfigureAwait(false);
            }

            selectedId = TileGrid.ItemAt(screen, selected)?.Id;
        }
    }

    /// <summary>Waits for the pending input, a tracker change, or (while a run is in flight) one second.</summary>
    /// <param name="input">The one pending input task.</param>
    /// <param name="tracker">The run tracker.</param>
    /// <param name="running">True while any run is in flight.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes on the first wake-up.</returns>
    internal static async Task WakeAsync(Task input, IRoutineRunTracker tracker, bool running, CancellationToken ct)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e) => changed.TrySetResult();
        tracker.Changed += OnChanged;
        try
        {
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wake = new List<Task> { input, changed.Task };
            if (running)
            {
                wake.Add(Task.Delay(TimeSpan.FromSeconds(1), tick.Token));
            }

            await Task.WhenAny(wake).ConfigureAwait(false);
            await tick.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            tracker.Changed -= OnChanged;
        }
    }

    internal static bool IsBarTypingKey(NavigationCommand command, out string seed)
    {
        seed = string.Empty;
        if (command.Type == CommandType.ActivateLink)
        {
            return true;
        }

        if (command.RawKeyChar is { } typed && typed > ' ' && typed != (char)127)
        {
            seed = typed.ToString();
            return true;
        }

        return false;
    }

    internal static string? NameProblem(string value, List<string> taken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RoutineText.NameEmpty;
        }

        return taken.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) ? RoutineText.NameTaken : null;
    }

    internal static async Task<string?> ReadInBarAsync(
        CommandContext ctx,
        RenderOptions options,
        string label,
        string? initial,
        string? hintRow,
        Func<LauncherUrlTyping?, string?, TileScreen> page,
        CancellationToken ct)
    {
        var typed = initial ?? string.Empty;
        TileScreen Page(string text, int cursor)
        {
            var typing = new LauncherUrlTyping { Buffer = label + text, Cursor = label.Length + cursor };
            return page(typing, hintRow) with
            {
                Selected = TileGrid.Bar,
                BarTyping = typing,
                Hints = new List<LauncherHint>
                {
                    new() { Key = "Enter", Label = "save" },
                    new() { Key = "Esc", Label = "keep it as it was" },
                },
            };
        }

        var screen = Page(typed, typed.Length);
        ctx.Renderer.RenderTileScreen(screen, options, paneOnly: false);
        var (row, col) = TileScreenPainter.BarInputPosition(screen, options);
        col += label.Length;
        if (ctx.InputHandler is TerminalInputHandler terminal)
        {
            return await terminal.PromptForInputWithChangeAsync(
                string.Empty,
                ct,
                row,
                col,
                typed,
                (buffer, cursor) => ctx.Renderer.RenderTileScreen(Page(buffer, cursor), options, paneOnly: true)).ConfigureAwait(false);
        }

        return await ctx.InputHandler.PromptForInputAsync(string.Empty, ct, row: row, col: col, initialInput: typed).ConfigureAwait(false);
    }

    internal static async Task<ScheduleRecipe?> LoadAsync(CommandContext ctx, Guid id)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IScheduleStore>().GetAsync(id).ConfigureAwait(false);
    }

    internal static async Task SaveAsync(CommandContext ctx, ScheduleRecipe recipe)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IScheduleStore>().SaveAsync(recipe).ConfigureAwait(false);
    }

    private static async Task ActivateTileAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, Tile tile, CancellationToken ct)
    {
        if (tile.Id.StartsWith("site:", StringComparison.Ordinal))
        {
            await RoutinePickModeHandler.OpenAsync(
                ctx, options, new RoutinePickRequest(recipe.Id, tile.Id["site:".Length..], null), ct).ConfigureAwait(false);
            return;
        }

        if (tile.Id == "addSite")
        {
            _ = await AddSiteAsync(ctx, options, recipe, ct).ConfigureAwait(false);
            return;
        }

        switch (tile.Id)
        {
            case "schedule":
                await RoutineSchedulePage.OpenAsync(ctx, recipe.Id, ct).ConfigureAwait(false);
                break;
            case "lastRun":
                await RoutineRunDetailsHandler.OpenAsync(ctx, options, recipe.Id, ct).ConfigureAwait(false);
                break;
            case "name":
                await EditNameAsync(ctx, options, recipe, ct).ConfigureAwait(false);
                break;
            case "podcast":
                await EditPodcastAsync(ctx, options, recipe, ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task<string?> AddSiteAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, CancellationToken ct)
    {
        var url = await RoutineSitePicker.PickAsync(ctx, recipe.Name, ct, recipe).ConfigureAwait(false);
        if (url is null)
        {
            return null;
        }

        return await RoutinePickModeHandler.OpenReportingAsync(ctx, options, new RoutinePickRequest(recipe.Id, url, null), ct).ConfigureAwait(false);
    }

    private static async Task RemoveAsync(
        CommandContext ctx, TileScreen screen, ScheduleRecipe recipe, Tile tile, CancellationToken ct)
    {
        var siteUrl = tile.Id["site:".Length..];
        var first = recipe.Steps.FirstOrDefault(s => s.SourceUrl == siteUrl);
        if (first is null)
        {
            return;
        }

        var victims = Enumerable.Range(0, recipe.Steps.Count)
            .Where(i => recipe.Steps[i].Domain == first.Domain && recipe.Steps[i].ConfigUrlPattern == first.ConfigUrlPattern)
            .ToList();
        if (!await TileConfirm.AskAsync(ctx, screen, RoutineText.RemoveConfirm(tile.Title), ct).ConfigureAwait(false))
        {
            return;
        }

        foreach (var index in victims.OrderByDescending(i => i))
        {
            recipe.RemoveStep(index);
        }

        await SaveAsync(ctx, recipe).ConfigureAwait(false);
    }

    /// <summary>Moves a site (all its steps together) one place earlier (-1) or later (+1) among the sites.</summary>
    private static async Task MoveSiteAsync(CommandContext ctx, ScheduleRecipe recipe, string siteUrl, int direction)
    {
        var groups = recipe.Steps
            .GroupBy(s => (s.Domain, s.ConfigUrlPattern))
            .Select(g => g.ToList())
            .ToList();
        var at = groups.FindIndex(g => g[0].SourceUrl == siteUrl);
        var to = at + direction;
        if (at < 0 || to < 0 || to >= groups.Count)
        {
            return;
        }

        (groups[at], groups[to]) = (groups[to], groups[at]);
        var steps = groups.SelectMany(g => g).ToList();
        await SaveAsync(
            ctx,
            ScheduleRecipe.Rehydrate(recipe.Id, recipe.Name, recipe.Enabled, recipe.Cadence, steps, recipe.OutputCollectionName, recipe.RunState, recipe.Version))
            .ConfigureAwait(false);
    }

    private static async Task<ScheduledRun?> LoadLastRunAsync(CommandContext ctx, Guid recipeId)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var runs = await scope.ServiceProvider.GetRequiredService<IScheduledRunRepository>().GetRecentForRecipeAsync(recipeId, 1).ConfigureAwait(false);
        return runs.FirstOrDefault();
    }

    private static async Task StartRunAndOpenAsync(CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, CancellationToken ct)
    {
        RunNowOutcome outcome;
        using (var scope = ctx.ScopeFactory.CreateScope())
        {
            outcome = scope.ServiceProvider.GetRequiredService<IScheduleRunNow>().StartInBackground(recipe);
        }

        if (outcome == RunNowOutcome.Busy)
        {
            ctx.NavigationService.SetStatusMessage(RoutineText.Busy, TimeSpan.FromSeconds(6));
        }

        await RoutineRunDetailsHandler.OpenAsync(ctx, options, recipe.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Edits the Name row in place; true when a name was committed (a valid typed name).</summary>
    private static async Task<bool> EditNameAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, CancellationToken ct)
    {
        var taken = (await GetAllAsync(ctx).ConfigureAwait(false))
            .Where(r => r.Id != recipe.Id)
            .Select(r => r.Name)
            .ToList();
        var input = await EditInRowAsync(
            ctx,
            options,
            recipe,
            "Name: ",
            $"Type a new name · Esc keeps “{recipe.Name}”",
            null,
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
    /// Reads the text in the page's bar (typing happens in the bar, workspace-6ifpp.1). Re-asks (error shown as the
    /// page's hint row) while <paramref name="validate"/> fails. Null on Esc / empty (keep).
    /// </summary>
    private static async Task<string?> EditInRowAsync(
        CommandContext ctx,
        RenderOptions options,
        ScheduleRecipe recipe,
        string label,
        string hint,
        string? initial,
        Func<string, string?> validate,
        CancellationToken ct)
    {
        var hintRow = hint;
        while (!ct.IsCancellationRequested)
        {
            var value = await ReadInRoutineBarAsync(ctx, options, recipe, label, initial, hintRow, ct).ConfigureAwait(false);
            if (value is null)
            {
                return null;
            }

            var error = validate(value);
            if (error is null)
            {
                return value;
            }

            hintRow = error;
            initial = value;
        }

        return null;
    }

    /// <summary>Types a site's address in the bar; opens its section picker. Returns an error for the next paint, else null.</summary>
    private static async Task<string?> AddSiteFromBarAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, string seed, CancellationToken ct)
    {
        var input = await ReadInRoutineBarAsync(ctx, options, recipe, string.Empty, seed, "Type a site's address, Enter to pick its sections", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (!NavigationUrl.TryNormalize(input, out var url, out var urlError))
        {
            return urlError;
        }

        return await RoutinePickModeHandler.OpenReportingAsync(ctx, options, new RoutinePickRequest(recipe.Id, url, null), ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReadInRoutineBarAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, string label, string? initial, string? hintRow, CancellationToken ct)
    {
        var lastRun = await LoadLastRunAsync(ctx, recipe.Id).ConfigureAwait(false);
        var bookmarks = ctx.Bookmarks?.ToList() ?? new List<Bookmark>();
        return await ReadInBarAsync(
            ctx,
            options,
            label,
            initial,
            hintRow,
            (_, hint) => RoutinePage.Build(recipe, lastRun, DateTimeOffset.Now, bookmarks, TileGrid.Bar, hint),
            ct).ConfigureAwait(false);
    }

    private static async Task EditPodcastAsync(
        CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, CancellationToken ct)
    {
        var input = await EditInRowAsync(
            ctx,
            options,
            recipe,
            "Podcast: ",
            $"Type a new podcast name · Esc keeps “{recipe.OutputCollectionName}”",
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

    private static async Task<IReadOnlyList<ScheduleRecipe>> GetAllAsync(CommandContext ctx)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IScheduleStore>().GetAllAsync().ConfigureAwait(false);
    }

    private static HashSet<string> SiteIds(TileScreen screen) =>
        TileGrid.Items(screen).Where(t => t.Id.StartsWith("site:", StringComparison.Ordinal)).Select(t => t.Id).ToHashSet();

    private static async Task<string?> NewSiteIdAsync(CommandContext ctx, Guid routineId, HashSet<string> before, IRoutineRunTracker tracker)
    {
        var recipe = await LoadAsync(ctx, routineId).ConfigureAwait(false);
        if (recipe is null)
        {
            return null;
        }

        var page = RoutinePage.Build(recipe, null, DateTimeOffset.Now, ctx.Bookmarks?.ToList() ?? new List<Bookmark>(), 0, null, tracker.Get(routineId));
        return SiteIds(page).FirstOrDefault(id => !before.Contains(id));
    }
}
