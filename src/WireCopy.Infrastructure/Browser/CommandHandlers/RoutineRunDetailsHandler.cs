// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using WireCopy.Infrastructure.Podcast.AudioEngine;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-9undz — the "last run" screen for one routine: why it failed, the run's log, a one-key
/// path to the fix, and run-again. Painted in the terminal like <see cref="RoutineEditorScreen"/>.
/// Opening it (and every finished run while it is open) acknowledges the routine's finished runs.
/// </summary>
internal static class RoutineRunDetailsHandler
{
    private const string Reset = "\x1b[0m";
    private const int LabelCol = 14;
    private const int MaxFixHops = 3;

    private enum ActionKind
    {
        Fix,
        Again,
        Log,
    }

    public static async Task OpenAsync(CommandContext ctx, RenderOptions options, Guid recipeId, CancellationToken ct)
    {
        using var runScope = ctx.ScopeFactory.CreateScope();
        var runNow = runScope.ServiceProvider.GetRequiredService<IScheduleRunNow>();
        Task<RunNowResult>? runTask = null;
        Task<NavigationCommand>? inputTask = null;
        string? status = null;
        var selected = 0;
        var offset = 0;
        var follow = true;

        await AcknowledgeAsync(ctx, r => r.RecipeId == recipeId).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            var (recipe, runs) = await LoadAsync(ctx, recipeId).ConfigureAwait(false);
            if (recipe is null)
            {
                return;
            }

            var latest = runs.FirstOrDefault();
            var diagnosis = latest is null ? null : RoutineRunDiagnosis.Diagnose(latest);
            var actions = BuildActions(diagnosis, latest is null);
            selected = Math.Clamp(selected, 0, actions.Count - 1);

            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            ctx.Renderer.LeaveHtmlPanes();
            var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
            var width = Math.Max(20, options.TerminalWidth - 2);
            offset = Draw(helpers, palette, width, options.TerminalHeight, recipe, runs, diagnosis, actions, selected, offset, follow, runTask != null, status);

            inputTask ??= ctx.InputHandler.WaitForInputAsync(ct);
            await Task.WhenAny(runTask is null ? new Task[] { inputTask } : new Task[] { inputTask, runTask }).ConfigureAwait(false);

            if (runTask is { IsCompleted: true })
            {
                var finished = runTask;
                runTask = null;
                status = finished.IsCompletedSuccessfully && finished.Result.Outcome == RunNowOutcome.Busy ? RoutineText.Busy : null;
                await AcknowledgeAsync(ctx, r => r.RecipeId == recipeId).ConfigureAwait(false);
                follow = true;
            }

            if (!inputTask.IsCompleted)
            {
                continue;
            }

            var command = await inputTask.ConfigureAwait(false);
            inputTask = null;
            follow = true;
            var action = actions[selected];
            var activate = command.Type == CommandType.ActivateLink;
            if (command.RawKeyChar == 'r' || (activate && action == ActionKind.Again))
            {
                if (runTask is null)
                {
                    status = null;
                    runTask = runNow.RunAsync(recipe, CancellationToken.None);
                    selected = Math.Max(0, actions.IndexOf(ActionKind.Again));
                }
            }
            else if (command.RawKeyChar == 'l' || (activate && action == ActionKind.Log))
            {
                await LogViewerCommandHandler.HandleAsync(ctx, options, ct).ConfigureAwait(false);
            }
            else if (activate && action == ActionKind.Fix && latest is not null && diagnosis is not null)
            {
                status = await FixAsync(ctx, options, recipe, latest, diagnosis.Fix, ct).ConfigureAwait(false);
                selected = Math.Max(0, actions.IndexOf(ActionKind.Again));
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
                selected = Math.Min(actions.Count - 1, selected + 1);
            }
            else if (command.Type == CommandType.MoveUp)
            {
                selected = Math.Max(0, selected - 1);
            }
            else if (command.Type == CommandType.PageDown)
            {
                offset += Math.Max(1, options.TerminalHeight - 8);
                follow = false;
            }
            else if (command.Type == CommandType.PageUp)
            {
                offset = Math.Max(0, offset - Math.Max(1, options.TerminalHeight - 8));
                follow = false;
            }
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

    private static List<ActionKind> BuildActions(RunDiagnosis? diagnosis, bool neverRan)
    {
        var list = new List<ActionKind>();
        if (!neverRan && diagnosis is { Fix.Kind: RunFixKind.SetupRow or RunFixKind.OpenSite })
        {
            list.Add(ActionKind.Fix);
        }

        list.Add(ActionKind.Again);
        if (!neverRan)
        {
            list.Add(ActionKind.Log);
        }

        return list;
    }

    private static async Task<string> FixAsync(CommandContext ctx, RenderOptions options, ScheduleRecipe recipe, ScheduledRun run, RunFix fix, CancellationToken ct)
    {
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
        await SettingsCommandHandler.DispatchRowAsync(ctx, options, first, ct).ConfigureAwait(false);
        if (run.ErrorClass is not ("NarrationNotReady" or "GenerationFailed"))
        {
            return RoutineText.FixedRunAgain(recipe.Name);
        }

        // Chained fix: a fresh install can miss both the address and the key — walk them in one pass.
        var hop = 0;
        while (true)
        {
            hop++;
            using var scope = ctx.ScopeFactory.CreateScope();
            var narrator = scope.ServiceProvider.GetRequiredService<IPodcastNarrator>();
            var check = await NarratorPreflight.CheckAsync(narrator, ct).ConfigureAwait(false);
            if (check.Ready)
            {
                return RoutineText.NarrationReady(recipe.Name);
            }

            var next = PodcastFailureClassifier.Classify(check.Message, Array.Empty<WireCopy.Application.DTOs.Podcast.ArticleFailure>()).RelevantSetupRow;
            if (next is not { } row || hop >= MaxFixHops || !handled.Add(row))
            {
                return string.IsNullOrWhiteSpace(check.Message) ? RoutineText.FixedRunAgain(recipe.Name) : check.Message;
            }

            await SettingsCommandHandler.DispatchRowAsync(ctx, options, row, ct).ConfigureAwait(false);
        }
    }

    private static int Draw(
        RenderHelpers helpers,
        ThemePalette palette,
        int width,
        int terminalHeight,
        ScheduleRecipe recipe,
        IReadOnlyList<ScheduledRun> runs,
        RunDiagnosis? diagnosis,
        IReadOnlyList<ActionKind> actions,
        int selected,
        int offset,
        bool follow,
        bool running,
        string? status)
    {
        var dim = palette.GetDimFg().AnsiFg;
        var primary = palette.PrimaryText.AnsiFg;
        var now = DateTimeOffset.Now;
        helpers.Clear();
        helpers.WriteLine();
        helpers.WriteLine($"  {palette.GetRoutineAccentFg().AnsiFg}{RenderHelpers.TruncateText(RoutineText.DetailsEyebrow(recipe.Name), width - 4)}{Reset}");
        helpers.WriteLine();

        var body = new List<string>();
        var selectedLine = 0;
        var latest = runs.FirstOrDefault();
        if (latest is null || diagnosis is null)
        {
            body.Add($"  {dim}{RoutineText.NeverRanYet}{Reset}");
            if (running)
            {
                body.Add($"  {palette.GetWarningFg().AnsiFg}{RoutineText.DetailsRunning}{Reset}");
            }

            body.Add(string.Empty);
            selectedLine = body.Count;
            body.Add(ActionLine(palette, width, true, RoutineText.RunNowRow));
        }
        else
        {
            var color = running ? palette.GetWarningFg().AnsiFg : HeadlineColor(palette, diagnosis.Headline);
            var headline = running ? RoutineText.DetailsRunning : diagnosis.Headline;
            var took = latest.FinishedAtUtc is { } fin ? " · took " + RoutineText.Took(fin - latest.StartedAtUtc) : string.Empty;
            body.Add($"  {color}{RenderHelpers.TruncateText($"{headline} · {RoutineText.When(latest.StartedAtUtc, now)}{took}", width - 4)}{Reset}");
            body.Add(string.Empty);
            AddLabelled(body, width, dim, primary, RoutineText.WhatHappened, diagnosis.What);
            AddLabelled(body, width, dim, primary, RoutineText.AtStep, diagnosis.Step);
            AddLabelled(body, width, dim, primary, RoutineText.HowToFix, diagnosis.HowToFix);
            body.Add(string.Empty);
            for (var i = 0; i < actions.Count; i++)
            {
                if (i == selected)
                {
                    selectedLine = body.Count;
                }

                var label = actions[i] switch
                {
                    ActionKind.Fix => diagnosis.Fix.Label,
                    ActionKind.Again => RoutineText.RunAgainRow,
                    _ => RoutineText.OpenLogRow,
                };
                body.Add(ActionLine(palette, width, i == selected, label));
            }

            AddSteps(body, palette, width, RoutineRunDiagnosis.Steps(latest));
            body.Add(string.Empty);
            body.Add($"  {palette.SecondaryText.AnsiFg}{RoutineText.LogHeading}{Reset}");
            var log = RoutineRunLog.Read(latest.Id);
            if (log.Count == 0)
            {
                body.Add($"    {dim}{RoutineText.NoLogKept}{Reset}");
            }

            foreach (var line in log)
            {
                var lineColor = LogColor(palette, line, dim);
                AddWrapped(body, line, lineColor, width);
            }

            if (runs.Count > 1)
            {
                body.Add(string.Empty);
                body.Add($"  {palette.SecondaryText.AnsiFg}{RoutineText.EarlierRunsHeading}{Reset}");
                foreach (var older in runs.Skip(1).Take(5))
                {
                    var d = RoutineRunDiagnosis.Diagnose(older);
                    var text = $"{d.Headline} · {RoutineText.When(older.StartedAtUtc, now)}" + (d.Short.Length > 0 ? " · " + d.Short : string.Empty);
                    AddWrapped(body, text, HeadlineColor(palette, d.Headline), width);
                }
            }
        }

        var statusLines = status is null
            ? new List<string>()
            : RenderHelpers.WrapText(status, Math.Max(10, width - 4)).ToList();
        var budget = Math.Max(3, terminalHeight - 3 - 3 - Math.Max(0, statusLines.Count - 1));
        var maxOffset = Math.Max(0, body.Count - budget);
        if (follow)
        {
            if (selectedLine < offset)
            {
                offset = selectedLine;
            }
            else if (selectedLine >= offset + budget)
            {
                offset = selectedLine - budget + 1;
            }
        }

        offset = Math.Clamp(offset, 0, maxOffset);
        foreach (var line in body.Skip(offset).Take(budget))
        {
            helpers.WriteLine(line);
        }

        helpers.WriteLine();
        if (statusLines.Count == 0)
        {
            helpers.WriteLine(string.Empty);
        }

        foreach (var statusLine in statusLines)
        {
            helpers.WriteLine($"  {palette.GetWarningFg().AnsiFg}{statusLine}{Reset}");
        }

        helpers.WriteLine($"  {dim}{RenderHelpers.TruncateText(RoutineText.DetailsFooter, width - 4)}{Reset}");
        helpers.ClearRemainingLines();
        return offset;
    }

    private static string HeadlineColor(ThemePalette palette, string headline)
    {
        if (headline.StartsWith('✗'))
        {
            return palette.ErrorFg.AnsiFg;
        }

        if (headline.StartsWith('⚠'))
        {
            return palette.GetWarningFg().AnsiFg;
        }

        return headline.StartsWith('✓') ? palette.GetSuccessFg().AnsiFg : palette.GetDimFg().AnsiFg;
    }

    // The drill-down never cuts anything: wrap to the width, continuation lines indented 4 more.
    private static void AddWrapped(List<string> body, string text, string color, int width)
    {
        var first = true;
        foreach (var part in RenderHelpers.WrapText(text, Math.Max(10, width - 10)))
        {
            body.Add($"{(first ? "    " : "        ")}{color}{part}{Reset}");
            first = false;
        }
    }

    private static string LogColor(ThemePalette palette, string line, string dim)
    {
        if (line.Contains("ERR", StringComparison.Ordinal))
        {
            return palette.ErrorFg.AnsiFg;
        }

        return line.Contains("WRN", StringComparison.Ordinal) ? palette.GetWarningFg().AnsiFg : dim;
    }

    private static void AddLabelled(List<string> body, int width, string dim, string primary, string label, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var lines = RenderHelpers.WrapText(text, Math.Max(10, width - 4 - LabelCol));
        for (var i = 0; i < lines.Count; i++)
        {
            var head = i == 0 ? label.PadRight(LabelCol) : new string(' ', LabelCol);
            body.Add($"  {dim}{head}{Reset}{primary}{lines[i]}{Reset}");
        }
    }

    private static void AddSteps(List<string> body, ThemePalette palette, int width, IReadOnlyList<WireCopy.Application.DTOs.Scheduling.RunStepOutcome> steps)
    {
        if (steps.Count == 0)
        {
            return;
        }

        body.Add(string.Empty);
        body.Add($"  {palette.SecondaryText.AnsiFg}{RoutineText.SectionsHeading}{Reset}");
        foreach (var step in steps)
        {
            var ok = step.ItemsContributed > 0 && step.Status is "Resolved" or "Recovered";
            var (glyph, color) = ("✓", palette.GetSuccessFg().AnsiFg);
            if (!ok)
            {
                (glyph, color) = ("✗", palette.ErrorFg.AnsiFg);
            }
            else if (step.Status == "Recovered")
            {
                (glyph, color) = ("⚠", palette.GetWarningFg().AnsiFg);
            }

            var host = Uri.TryCreate(step.SourceUrl, UriKind.Absolute, out var u) ? u.Host : step.SourceUrl;
            var stories = step.ItemsContributed == 1 ? "story" : "stories";
            body.Add($"    {color}{glyph}{Reset} {palette.PrimaryText.AnsiFg}{RenderHelpers.TruncateText($"{step.SectionName} · {step.ItemsContributed} {stories} — {host}", width - 8)}{Reset}");
            if (!string.IsNullOrWhiteSpace(step.Diagnostic))
            {
                foreach (var l in RenderHelpers.WrapText(step.Diagnostic, Math.Max(10, width - 10)))
                {
                    body.Add($"      {palette.GetDimFg().AnsiFg}{l}{Reset}");
                }
            }
        }
    }

    private static string ActionLine(ThemePalette palette, int width, bool selected, string text)
    {
        var plain = RenderHelpers.TruncateText(text, Math.Max(4, width - 8)).PadRight(Math.Max(4, width - 8));
        var gutter = $"{palette.GetMutedFg().AnsiFg}{(selected ? "▌" : " ")}{Reset}";
        return selected
            ? $"  {gutter} {palette.SelectedItemBg.AnsiBg}{palette.SelectedItemFg.AnsiFg}{plain}{Reset}"
            : $"  {gutter} {palette.GetRoutineAccentFg().AnsiFg}{plain.TrimEnd()}{Reset}";
    }
}
