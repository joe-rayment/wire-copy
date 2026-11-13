// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Infrastructure.Podcast.AudioEngine;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-6ifpp.3 — the run view for one routine, as launcher tiles: watch a run live, stop it,
/// or act on the result of the last one. Opening it (and every run that finishes while it is open)
/// acknowledges the routine's finished runs.
/// </summary>
internal static class RoutineRunDetailsHandler
{
    private const int MaxFixHops = 3;

    public static async Task OpenAsync(CommandContext ctx, RenderOptions options, Guid recipeId, CancellationToken ct)
    {
        using var runScope = ctx.ScopeFactory.CreateScope();
        var runNow = runScope.ServiceProvider.GetRequiredService<IScheduleRunNow>();
        var tracker = runScope.ServiceProvider.GetRequiredService<IRoutineRunTracker>();
        var signal = new Signal();
        void OnChanged(object? sender, EventArgs e) => signal.Current.TrySetResult();
        tracker.Changed += OnChanged;
        try
        {
            await LoopAsync(ctx, options, recipeId, runNow, tracker, signal, ct).ConfigureAwait(false);
        }
        finally
        {
            tracker.Changed -= OnChanged;
        }
    }

    /// <summary>Acknowledges every unacknowledged finished run matching <paramref name="match"/>.</summary>
    internal static async Task AcknowledgeAsync(CommandContext ctx, Func<ScheduledRun, bool> match)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduledRunRepository>();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var pending = (await repo.GetUnacknowledgedFinishedRunsAsync().ConfigureAwait(false)).Where(match).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var run in pending)
        {
            run.Acknowledge();
            await repo.UpdateAsync(run).ConfigureAwait(false);
        }

        await unit.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task LoopAsync(
        CommandContext ctx,
        RenderOptions options,
        Guid recipeId,
        IScheduleRunNow runNow,
        IRoutineRunTracker tracker,
        Signal signal,
        CancellationToken ct)
    {
        Task<NavigationCommand>? inputTask = null;
        string? status = null;
        string? selId = null;
        Guid? viewRunId = null;
        var wasRunning = false;

        await AcknowledgeAsync(ctx, r => r.RecipeId == recipeId).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            var wakeUp = signal.Reset();
            var (recipe, runs) = await LoadAsync(ctx, recipeId).ConfigureAwait(false);
            if (recipe is null)
            {
                return;
            }

            var progress = tracker.Get(recipeId);
            var running = progress is not null;
            if (wasRunning && !running)
            {
                await AcknowledgeAsync(ctx, r => r.RecipeId == recipeId).ConfigureAwait(false);
                viewRunId = null;
                selId = null;
            }

            wasRunning = running;
            var viewRun = runs.FirstOrDefault(r => r.Id == viewRunId) ?? runs.FirstOrDefault();
            var isLatest = viewRun is not null && viewRun.Id == runs[0].Id;
            var earlier = viewRun is null ? new List<ScheduledRun>() : runs.Skip(runs.IndexOf(viewRun) + 1).ToList();
            var diagnosis = viewRun is null ? null : RoutineRunDiagnosis.Diagnose(viewRun);
            var screen = BuildScreen(recipe, progress, viewRun, earlier, isLatest, status);
            if (running)
            {
                selId = null;
            }
            else
            {
                var sel = TileGrid.IndexOfId(screen, selId, 0);
                screen = screen with { Selected = sel };
                selId = TileGrid.ItemAt(screen, sel)?.Id;
            }

            ctx.Renderer.RenderTileScreen(screen, ctx.GetCurrentRenderOptions());

            inputTask ??= ctx.InputHandler.WaitForInputAsync(ct);
            var wake = new List<Task> { inputTask, wakeUp };
            if (running)
            {
                wake.Add(Task.Delay(TimeSpan.FromSeconds(1), ct));
            }

            try
            {
                await Task.WhenAny(wake).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!inputTask.IsCompleted)
            {
                continue;
            }

            var command = await inputTask.ConfigureAwait(false);
            inputTask = null;
            status = null;
            var key = command.RawKeyChar;
            var activate = command.Type == CommandType.ActivateLink;

            if (command.Type == CommandType.ShowHelp)
            {
                await TileHelp.ShowAsync(ctx, "Run", HelpKeys(running), ct).ConfigureAwait(false);
                continue;
            }

            if (command.Type is CommandType.GoBack or CommandType.Quit)
            {
                if (!running && !isLatest)
                {
                    viewRunId = null;
                    selId = null;
                    continue;
                }

                return;
            }

            if (command.Type == CommandType.TerminalResized)
            {
                options = ctx.GetCurrentRenderOptions();
                continue;
            }

            if (key == 'l' || (activate && selId == "log" && !running))
            {
                ctx.Renderer.HideLauncherPane();
                await LogViewerCommandHandler.HandleAsync(ctx, options, ct).ConfigureAwait(false);
                continue;
            }

            if (running)
            {
                if (key == 'x' && await TileConfirm.AskAsync(ctx, screen, "Stop this run?", ct).ConfigureAwait(false))
                {
                    tracker.Cancel(recipeId);
                }

                continue;
            }

            if (key is >= '1' and <= '9' && command.Type != CommandType.JumpToIndex)
            {
                selId = TileGrid.ItemAt(screen, TileGrid.Jump(screen, key.Value - '0'))?.Id;
                continue;
            }

            if (command.Type == CommandType.JumpToIndex)
            {
                selId = TileGrid.ItemAt(screen, TileGrid.Jump(screen, command.Count))?.Id;
                continue;
            }

            if (command.Type is CommandType.MoveLeft or CommandType.CollapseNode
                or CommandType.MoveRight or CommandType.ExpandNode
                or CommandType.MoveUp or CommandType.MoveDown)
            {
                var moveType = command.Type switch
                {
                    CommandType.CollapseNode => CommandType.MoveLeft,
                    CommandType.ExpandNode => CommandType.MoveRight,
                    _ => command.Type,
                };
                selId = TileGrid.ItemAt(screen, TileGrid.Move(screen, moveType))?.Id;
                continue;
            }

            if (key == 'r' || (activate && selId == "again"))
            {
                status = runNow.StartInBackground(recipe) == RunNowOutcome.Busy ? RoutineText.Busy : null;
                selId = null;
            }
            else if (activate && selId == "fix" && viewRun is not null && diagnosis is not null)
            {
                ctx.Renderer.HideLauncherPane();
                var paintRun = viewRun;
                var paintRecipe = recipe;
                var paintEarlier = earlier;
                var paintLatest = isLatest;
                void Paint(string interim) => ctx.Renderer.RenderTileScreen(
                    RoutineRunPage.Finished(paintRecipe, paintRun, paintEarlier, paintLatest, DateTimeOffset.Now, 0, interim),
                    ctx.GetCurrentRenderOptions());

                status = await FixAsync(ctx, options, recipe, viewRun, diagnosis.Fix, Paint, ct).ConfigureAwait(false);
                selId = "again";
            }
            else if (activate && viewRun is not null && selId is not null)
            {
                if (selId.StartsWith("run:", StringComparison.Ordinal) && Guid.TryParse(selId.AsSpan(4), out var earlierId))
                {
                    viewRunId = earlierId;
                    selId = null;
                }
                else
                {
                    status = ActivateResult(ctx, selId, viewRun);
                }
            }
        }
    }

    private static TileScreen BuildScreen(
        ScheduleRecipe recipe, RoutineRunProgress? progress, ScheduledRun? viewRun, IReadOnlyList<ScheduledRun> earlier, bool isLatest, string? status)
    {
        var now = DateTimeOffset.Now;
        if (progress is not null)
        {
            return RoutineRunPage.Running(recipe, progress, now);
        }

        if (viewRun is null)
        {
            return RoutineRunPage.NeverRan(recipe) with { HintRow = status };
        }

        return RoutineRunPage.Finished(recipe, viewRun, earlier, isLatest, now, 0, status);
    }

    private static IReadOnlyList<(string Key, string What)> HelpKeys(bool running)
    {
        if (running)
        {
            return
            [
                ("X", "Stop the run"),
                ("ESC", "Leave it running"),
                ("L", "Open the log"),
            ];
        }

        return
        [
            ("ENTER", "Do what is lit"),
            ("1-9", "Jump to a tile"),
            ("R", "Run again"),
            ("L", "Open the log"),
            ("← ↑ ↓ →", "Move"),
            ("ESC", "Back"),
        ];
    }

    /// <summary>Performs the Enter action of a finished-run tile; returns the hint row text, if any.</summary>
    private static string? ActivateResult(CommandContext ctx, string id, ScheduledRun run)
    {
        switch (id)
        {
            case "play" when !string.IsNullOrWhiteSpace(run.TargetLocalPath):
                SettingsCommandHandler.OpenExternal(run.TargetLocalPath, ctx.Logger);
                return "Opening the episode…";
            case "copyFeed" when !string.IsNullOrWhiteSpace(run.TargetFeedUrl):
                Osc52Clipboard.Copy(run.TargetFeedUrl);
                return "Feed address copied";
            case "showFile" when !string.IsNullOrWhiteSpace(run.TargetLocalPath):
                SettingsCommandHandler.OpenExternal(Path.GetDirectoryName(run.TargetLocalPath) ?? run.TargetLocalPath, ctx.Logger);
                return run.TargetLocalPath;
            default:
                return null;
        }
    }

    private static async Task<(ScheduleRecipe? Recipe, List<ScheduledRun> Runs)> LoadAsync(CommandContext ctx, Guid recipeId)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduledRunRepository>();
        var recipe = (await store.GetAllAsync().ConfigureAwait(false)).FirstOrDefault(r => r.Id == recipeId);
        if (recipe is null)
        {
            return (null, new List<ScheduledRun>());
        }

        var runs = (await repo.GetRecentForRecipeAsync(recipeId, 8).ConfigureAwait(false))
            .Where(r => r.Status is not (ScheduledRunStatus.Pending or ScheduledRunStatus.Running))
            .OrderByDescending(r => r.StartedAtUtc)
            .ToList();
        return (recipe, runs);
    }

    private static async Task<string> FixAsync(CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, ScheduledRun run, RunFix fix, Action<string> paint, CancellationToken ct)
    {
        if (fix.Kind == RunFixKind.OpenEngineSettings)
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var url = SettingsCommandHandler.ResolveAudioEngineUrl(scope);
            return SettingsCommandHandler.OpenExternal(url, ctx.Logger)
                ? $"Opened audio-engine at {url} — run again when it's set up."
                : $"Couldn't open a browser. audio-engine is at {url}";
        }

        if (fix.Kind == RunFixKind.OpenSite && fix.SiteUrl is not null)
        {
            await RoutinePickModeHandler.OpenAsync(ctx, options, new RoutinePickRequest(recipe.Id, fix.SiteUrl, null), ct).ConfigureAwait(false);
            return RoutineText.FixedRunAgain(recipe.Name);
        }

        if (fix.Kind != RunFixKind.SetupRow || fix.Row is not { } first)
        {
            return RoutineText.FixedRunAgain(recipe.Name);
        }

        var handled = new HashSet<SettingsCommandHandler.SetupRow> { first };
        if (first == SettingsCommandHandler.SetupRow.AudioEngineProgram)
        {
            // Try to get it running without asking anything; only ask where it is when it can't be found.
            var status = await EnsureEngineAsync(ctx, recipe, paint, RoutineText.LookingForAudioEngine, ct).ConfigureAwait(false);
            if (status.Ready)
            {
                return AudioEngineRunning(ctx, recipe);
            }

            var reason = PodcastFailureClassifier.Classify(status.Message, Array.Empty<WireCopy.Application.DTOs.Podcast.ArticleFailure>()).RelevantSetupRow;
            if (reason == SettingsCommandHandler.SetupRow.AudioEngineProgram)
            {
                var (outcome, message) = await SettingsCommandHandler.PromptAudioEngineLocationAsync(ctx, ct, s => paint(s ?? string.Empty)).ConfigureAwait(false);
                if (outcome == SettingsCommandHandler.AudioEngineLocationOutcome.Cancelled)
                {
                    return RoutineText.NothingChosen;
                }

                if (outcome == SettingsCommandHandler.AudioEngineLocationOutcome.NotAnEngine)
                {
                    return message ?? RoutineText.NothingChosen;
                }
            }
            else if (reason is { } other)
            {
                handled.Add(other);
                await SettingsCommandHandler.DispatchRowAsync(ctx, options, other, ct).ConfigureAwait(false);
            }
            else
            {
                return NotReadyStatus(ctx, recipe, status);
            }
        }
        else
        {
            await SettingsCommandHandler.DispatchRowAsync(ctx, options, first, ct).ConfigureAwait(false);
            if (run.ErrorClass is not ("NarrationNotReady" or "GenerationFailed"))
            {
                return RoutineText.FixedRunAgain(recipe.Name);
            }
        }

        // Chained fix: a fresh install can miss both the address and the key — walk them in one pass,
        // actually starting the engine from the new location/address/key rather than only checking.
        var hop = 0;
        while (true)
        {
            hop++;
            var check = await EnsureEngineAsync(ctx, recipe, paint, RoutineText.StartingAudioEngine, ct).ConfigureAwait(false);
            if (check.Ready)
            {
                return AudioEngineRunning(ctx, recipe);
            }

            var next = PodcastFailureClassifier.Classify(check.Message, Array.Empty<WireCopy.Application.DTOs.Podcast.ArticleFailure>()).RelevantSetupRow;
            if (next is not { } row || hop >= MaxFixHops || !handled.Add(row))
            {
                return NotReadyStatus(ctx, recipe, check);
            }

            await SettingsCommandHandler.DispatchRowAsync(ctx, options, row, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Repaints with an interim status, then checks audio-engine and starts it when it is a local one that is down.</summary>
    private static async Task<WireCopy.Application.DTOs.Podcast.NarratorStatus> EnsureEngineAsync(
        CommandContext ctx, ScheduleRecipe recipe, Action<string> paint, string interim, CancellationToken ct)
    {
        _ = recipe;
        paint(interim);
        using var scope = ctx.ScopeFactory.CreateScope();
        var narrator = scope.ServiceProvider.GetRequiredService<IPodcastNarrator>();
        var launcher = scope.ServiceProvider.GetRequiredService<IAudioEngineLauncher>();
        return await NarratorPreflight.EnsureReadyAsync(narrator, launcher, ct).ConfigureAwait(false);
    }

    /// <summary>The closing status when narration is still not ready: says so when the engine itself is up now.</summary>
    private static string NotReadyStatus(CommandContext ctx, ScheduleRecipe recipe, WireCopy.Application.DTOs.Podcast.NarratorStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.Message))
        {
            return RoutineText.FixedRunAgain(recipe.Name);
        }

        if (status.Reachable && !status.Ready)
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var launcher = scope.ServiceProvider.GetRequiredService<IAudioEngineLauncher>();
            return RoutineText.AudioEngineRunningButNotReady(launcher.Discover().Program?.Display, status.Message);
        }

        return status.Message;
    }

    private static string AudioEngineRunning(CommandContext ctx, ScheduleRecipe recipe)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IAudioEngineLauncher>();
        return RoutineText.AudioEngineRunning(recipe.Name, launcher.Discover().Program?.Display);
    }

    private sealed class Signal
    {
        private TaskCompletionSource _current = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Current => Volatile.Read(ref _current);

        /// <summary>Arms a fresh signal and returns the task that completes when the tracker next changes.</summary>
        public Task Reset()
        {
            var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _current, fresh);
            return fresh.Task;
        }
    }
}
