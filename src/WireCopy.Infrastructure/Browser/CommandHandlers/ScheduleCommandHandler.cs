// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-frpl.14 (B12a) — the keyboard-only Schedules screen, reached via
/// ':schedules'. Lists recipes (enabled · name · cadence · last result) and edits
/// them with the same overlay-card + form-field components the rest of the TUI uses.
/// Building a step pins a DURABLE section out of a site's saved layout config; a
/// site without a usable config is BLOCKED from being added (never persists an
/// unpinned section), and an existing recipe whose config was deleted degrades to a
/// visible "needs reconfigure" line rather than crashing. Run-now reuses the
/// scheduler's gate + Running-row admission protocol (B12a/B6) via <see cref="IScheduleRunNow"/>.
/// </summary>
internal static class ScheduleCommandHandler
{
    public static async Task HandleSchedulesAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
        var configStore = scope.ServiceProvider.GetRequiredService<IHierarchyConfigStore>();
        var bookmarkService = scope.ServiceProvider.GetRequiredService<IBookmarkService>();
        var runNow = scope.ServiceProvider.GetRequiredService<IScheduleRunNow>();

        // workspace-frpl.13 (B11): opening the screen means the user has now SEEN the
        // results, so acknowledge unacknowledged finished runs — this clears the
        // launcher badge. Best-effort; a failure here must not block the screen.
        await AcknowledgeFinishedRunsAsync(scope, ct).ConfigureAwait(false);
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var overlay = new SetupWizardOverlay.State();
        ctx.SetOverlayPainter(opts => SetupWizardOverlay.Render(overlay, palette, opts.TerminalWidth, opts.TerminalHeight));

        try
        {
            var recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
            var cursor = 0;

            while (!ct.IsCancellationRequested)
            {
                cursor = Math.Clamp(cursor, 0, Math.Max(0, recipes.Count - 1));
                await RenderListAsync(ctx, overlay, recipes, cursor, configStore, options, ct).ConfigureAwait(false);
                var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
                var key = command.RawKeyChar;

                if (key is 'a')
                {
                    var created = await EditRecipeAsync(ctx, overlay, palette, options, configStore, bookmarkService, store, existing: null, ct).ConfigureAwait(false);
                    recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                    if (created != null)
                    {
                        cursor = Math.Max(0, recipes.FindIndex(r => r.Id == created.Id));
                    }

                    continue;
                }

                if (recipes.Count > 0 && (key is 'e' || command.Type == CommandType.ActivateLink))
                {
                    await EditRecipeAsync(ctx, overlay, palette, options, configStore, bookmarkService, store, recipes[cursor], ct).ConfigureAwait(false);
                    recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                    continue;
                }

                if (recipes.Count > 0 && key is ' ')
                {
                    var r = recipes[cursor];
                    if (r.Enabled)
                    {
                        r.Disable();
                    }
                    else
                    {
                        r.Enable();
                    }

                    await store.SaveAsync(r).ConfigureAwait(false);
                    continue;
                }

                if (recipes.Count > 0 && key is 'R')
                {
                    var outcome = runNow.StartInBackground(recipes[cursor]);
                    ctx.NavigationService.SetStatusMessage(
                        outcome == RunNowOutcome.Started
                            ? $"Running '{recipes[cursor].Name}' now — watch the launcher badge for the result"
                            : "A generation is already in progress — try again when it finishes",
                        TimeSpan.FromSeconds(8));
                    continue;
                }

                if (recipes.Count > 0 && key is 'x')
                {
                    var victim = recipes[cursor];
                    var confirmed = await ConfirmationDialog.ConfirmAsync(
                        ctx.InputHandler, "Delete schedule", $"Delete '{victim.Name}'? This cannot be undone.", palette, ct, isDestructive: true).ConfigureAwait(false);
                    if (confirmed)
                    {
                        await store.DeleteAsync(victim.Id).ConfigureAwait(false);
                        recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                    }

                    continue;
                }

                switch (command.Type)
                {
                    case CommandType.GoBack or CommandType.Quit:
                        return;
                    case CommandType.MoveDown or CommandType.ExpandNode:
                        if (recipes.Count > 0)
                        {
                            cursor = (cursor + 1) % recipes.Count;
                        }

                        break;
                    case CommandType.MoveUp or CommandType.CollapseNode:
                        if (recipes.Count > 0)
                        {
                            cursor = (cursor - 1 + recipes.Count) % recipes.Count;
                        }

                        break;
                }
            }
        }
        finally
        {
            ctx.SetOverlayPainter(null);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
    }

    private static async Task RenderListAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        List<ScheduleRecipe> recipes,
        int cursor,
        IHierarchyConfigStore configStore,
        RenderOptions options,
        CancellationToken ct)
    {
        var card = new SetupWizardOverlay.WizardCard
        {
            Title = "Schedules",
            Prompt = recipes.Count == 0
                ? "No schedules yet. A schedule pulls sections from your sites on a cadence and auto-publishes a podcast while WireCopy is open."
                : "Recurring section recipes → auto-published podcast (runs while WireCopy is open).",
            Hint = recipes.Count == 0
                ? "a: new · Esc: close"
                : "↑/↓ move · a:new · e:edit · space:on/off · R:run now · x:delete · Esc:close",
        };

        if (recipes.Count == 0)
        {
            card.Options.Add(new SetupWizardOverlay.CardOption { Label = "(press a to create your first schedule)" });
        }
        else
        {
            foreach (var r in recipes)
            {
                var enabled = r.Enabled ? "●" : "○";
                var last = DescribeLastResult(r);
                var reconfigure = await AnyStepNeedsReconfigureAsync(r, configStore).ConfigureAwait(false) ? "  ⚠ needs reconfigure" : string.Empty;
                card.Options.Add(new SetupWizardOverlay.CardOption
                {
                    Label = $"{enabled} {r.Name}  ·  {ScheduleEditing.DescribeCadence(r.Cadence)}  ·  {last}{reconfigure}",
                });
            }
        }

        card.Cursor = recipes.Count == 0 ? 0 : cursor;
        overlay.Mode = SetupWizardOverlay.Mode.Card;
        overlay.Card = card;
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static string DescribeLastResult(ScheduleRecipe recipe) => recipe.RunState.LastStatus switch
    {
        RunStatus.Success => "✓ last run ok",
        RunStatus.PartialSuccess => "◐ last run partial",
        RunStatus.Failed => "✗ last run failed",
        RunStatus.Skipped => "— last run skipped",
        _ => "— never run",
    };

    private static async Task AcknowledgeFinishedRunsAsync(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var repo = scope.ServiceProvider.GetService<IScheduledRunRepository>();
            if (repo is null)
            {
                return;
            }

            var unacked = await repo.GetUnacknowledgedFinishedRunsAsync(ct).ConfigureAwait(false);
            if (unacked.Count == 0)
            {
                return;
            }

            foreach (var run in unacked)
            {
                run.Acknowledge();
                await repo.UpdateAsync(run, ct).ConfigureAwait(false);
            }

            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // best-effort: never block the screen on a badge-clear failure
        }
    }

    private static async Task<bool> AnyStepNeedsReconfigureAsync(ScheduleRecipe recipe, IHierarchyConfigStore configStore)
    {
        foreach (var step in recipe.Steps)
        {
            var config = await configStore.GetConfigAsync(step.SourceUrl).ConfigureAwait(false);
            if (ScheduleEditing.StepNeedsReconfigure(config, step))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Create (existing == null) or edit a recipe. Returns the saved recipe, or null if cancelled.</summary>
    private static async Task<ScheduleRecipe?> EditRecipeAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        ThemePalette palette,
        RenderOptions options,
        IHierarchyConfigStore configStore,
        IBookmarkService bookmarkService,
        IScheduleStore store,
        ScheduleRecipe? existing,
        CancellationToken ct)
    {
        var name = await PromptTextAsync(ctx, palette, "Schedule name", existing?.Name, ct, v => string.IsNullOrWhiteSpace(v) ? "Name cannot be empty" : null).ConfigureAwait(false);
        if (name == null)
        {
            return null;
        }

        var steps = existing?.Steps.ToList() ?? new List<RecipeStep>();
        var stepsResult = await BuildStepsAsync(ctx, overlay, palette, options, configStore, bookmarkService, steps, ct).ConfigureAwait(false);
        if (stepsResult == null)
        {
            return null; // cancelled
        }

        var cadence = await PickCadenceAsync(ctx, overlay, palette, options, existing?.Cadence, ct).ConfigureAwait(false);
        if (cadence == null)
        {
            return null;
        }

        var outputName = await PromptTextAsync(ctx, palette, "Podcast / collection name", existing?.OutputCollectionName ?? name, ct, v => string.IsNullOrWhiteSpace(v) ? "Cannot be empty" : null).ConfigureAwait(false);
        if (outputName == null)
        {
            return null;
        }

        ScheduleRecipe recipe;
        try
        {
            recipe = existing == null
                ? ScheduleRecipe.Create(name, cadence, stepsResult, outputName)
                : ScheduleRecipe.Rehydrate(existing.Id, name, existing.Enabled, cadence, stepsResult, outputName, existing.RunState, existing.Version);
        }
        catch (ArgumentException ex)
        {
            ctx.NavigationService.SetStatusMessage($"Could not save: {ex.Message}", TimeSpan.FromSeconds(8));
            return null;
        }

        await store.SaveAsync(recipe).ConfigureAwait(false);
        ctx.NavigationService.SetStatusMessage($"Saved schedule '{recipe.Name}'.", TimeSpan.FromSeconds(5));
        return recipe;
    }

    private static async Task<List<RecipeStep>?> BuildStepsAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        ThemePalette palette,
        RenderOptions options,
        IHierarchyConfigStore configStore,
        IBookmarkService bookmarkService,
        List<RecipeStep> steps,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var labels = steps.Select((s, i) => $"{i + 1}. {s.SectionName}  ({TakeModeLabel(s)}{(s.Required ? ", required" : ", optional")})  — {s.Domain}").ToList();
            labels.Add("＋ Add a source/section");
            labels.Add(steps.Count > 0 ? "✓ Done — choose cadence" : "✓ Done (needs at least one required step)");

            var choice = await PickAsync(ctx, overlay, options, "Sources", "Each source pins a durable section. Top → first, etc. ↑/↓ move · Enter select · x remove · Esc cancel", labels, 0, ct, allowRemoveAt: steps.Count).ConfigureAwait(false);

            if (choice.Cancelled)
            {
                return null;
            }

            if (choice.RemovedIndex is { } removeAt && removeAt < steps.Count)
            {
                steps.RemoveAt(removeAt);
                continue;
            }

            var index = choice.Index;
            if (index < steps.Count)
            {
                continue; // selecting an existing step is a no-op for now (reorder is x+re-add in v1)
            }

            if (index == steps.Count)
            {
                var added = await AddStepAsync(ctx, overlay, palette, options, configStore, bookmarkService, ct).ConfigureAwait(false);
                if (added != null)
                {
                    steps.Add(added);
                }

                continue;
            }

            // Done
            if (steps.Count == 0 || !steps.Any(s => s.Required))
            {
                ctx.NavigationService.SetStatusMessage("Add at least one REQUIRED step before finishing.", TimeSpan.FromSeconds(6));
                continue;
            }

            return steps;
        }

        return null;
    }

    private static async Task<RecipeStep?> AddStepAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        ThemePalette palette,
        RenderOptions options,
        IHierarchyConfigStore configStore,
        IBookmarkService bookmarkService,
        CancellationToken ct)
    {
        var bookmarks = (await bookmarkService.GetAllBookmarksAsync(ct).ConfigureAwait(false)).ToList();
        if (bookmarks.Count == 0)
        {
            ctx.NavigationService.SetStatusMessage("No bookmarks yet — bookmark a site first, then add it here.", TimeSpan.FromSeconds(6));
            return null;
        }

        var pick = await PickAsync(ctx, overlay, options, "Pick a site", "Choose a bookmarked site to pull a section from. Enter select · Esc back", bookmarks.Select(b => $"{b.Name}  ·  {b.Url}").ToList(), 0, ct).ConfigureAwait(false);
        if (pick.Cancelled)
        {
            return null;
        }

        var bookmark = bookmarks[pick.Index];
        var config = await configStore.GetConfigAsync(bookmark.Url).ConfigureAwait(false);
        if (!ScheduleEditing.CanPinSection(config))
        {
            // Never persist an unpinned section — the site must be configured first.
            ctx.NavigationService.SetStatusMessage(
                $"'{bookmark.Name}' has no usable layout yet — open it and run AI setup (Ctrl+L), then add it here. (Inline setup: B12b)",
                TimeSpan.FromSeconds(10));
            return null;
        }

        var sections = config!.Sections.OrderBy(s => s.SortOrder).ToList();
        var sectionPick = await PickAsync(ctx, overlay, options, "Pick a section", $"Durable sections saved for {config.Domain}. Enter select · Esc back", sections.Select(DescribeSection).ToList(), 0, ct).ConfigureAwait(false);
        if (sectionPick.Cancelled)
        {
            return null;
        }

        var section = sections[sectionPick.Index];

        var modePick = await PickAsync(ctx, overlay, options, "How many stories?", "Enter select · Esc back", new List<string> { "Whole section", "Just the top story", "Top N stories…" }, 0, ct).ConfigureAwait(false);
        if (modePick.Cancelled)
        {
            return null;
        }

        var takeMode = modePick.Index switch
        {
            1 => TakeMode.SingleTopStory,
            2 => TakeMode.TopN,
            _ => TakeMode.WholeSection,
        };
        int? takeCount = null;
        if (takeMode == TakeMode.TopN)
        {
            var countText = await PromptTextAsync(ctx, palette, "How many stories (N)?", "3", ct, v => int.TryParse(v, out var n) && n >= 1 ? null : "Enter a whole number ≥ 1").ConfigureAwait(false);
            if (countText == null)
            {
                return null;
            }

            takeCount = int.Parse(countText);
        }

        var requiredPick = await PickAsync(ctx, overlay, options, "Required?", "A required section that yields nothing fails the run loudly (never a silent empty episode). Enter select · Esc back", new List<string> { "Required (must contribute)", "Optional (skip if empty)" }, 0, ct).ConfigureAwait(false);
        if (requiredPick.Cancelled)
        {
            return null;
        }

        return ScheduleEditing.BuildStep(bookmark.Url, new Uri(bookmark.Url).Host, config.UrlPattern, section, takeMode, takeCount, required: requiredPick.Index == 0);
    }

    private static async Task<Cadence?> PickCadenceAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        ThemePalette palette,
        RenderOptions options,
        Cadence? existing,
        CancellationToken ct)
    {
        var presetPick = await PickAsync(ctx, overlay, options, "How often?", "Enter select · Esc back", new List<string> { "Every day", "Weekdays (Mon–Fri)", "Weekends (Sat/Sun)", "Pick days…" }, 0, ct).ConfigureAwait(false);
        if (presetPick.Cancelled)
        {
            return null;
        }

        IReadOnlyList<DayOfWeek> days;
        switch (presetPick.Index)
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
                var picked = await PickDaysAsync(ctx, overlay, options, existing?.Days, ct).ConfigureAwait(false);
                if (picked == null || picked.Count == 0)
                {
                    return null;
                }

                days = picked.ToList();
                break;
        }

        var defaultTime = existing?.LocalTime.ToString("HH:mm") ?? "07:00";
        var timeText = await PromptTextAsync(ctx, palette, "Time of day (24h, HH:mm)", defaultTime, ct, v => TimeOnly.TryParse(v, out _) ? null : "Enter a time like 07:00").ConfigureAwait(false);
        if (timeText == null)
        {
            return null;
        }

        var time = TimeOnly.Parse(timeText);
        try
        {
            return ScheduleEditing.BuildCadence(days, time);
        }
        catch (ArgumentException ex)
        {
            ctx.NavigationService.SetStatusMessage($"Invalid cadence: {ex.Message}", TimeSpan.FromSeconds(6));
            return null;
        }
    }

    private static async Task<List<DayOfWeek>?> PickDaysAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        RenderOptions options,
        IReadOnlySet<DayOfWeek>? initial,
        CancellationToken ct)
    {
        var all = Enum.GetValues<DayOfWeek>();
        var selected = new HashSet<DayOfWeek>(initial ?? new HashSet<DayOfWeek>());
        var cursor = 0;

        while (!ct.IsCancellationRequested)
        {
            var labels = all.Select(d => $"[{(selected.Contains(d) ? "x" : " ")}] {d}").ToList();
            labels.Add("✓ Done");
            var card = new SetupWizardOverlay.WizardCard
            {
                Title = "Pick days",
                Prompt = "Space toggles a day · Enter on Done · Esc back",
                Hint = "↑/↓ move · space toggle · Enter Done · Esc back",
                Cursor = cursor,
            };
            foreach (var l in labels)
            {
                card.Options.Add(new SetupWizardOverlay.CardOption { Label = l });
            }

            overlay.Mode = SetupWizardOverlay.Mode.Card;
            overlay.Card = card;
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            var count = labels.Count;
            switch (command.Type)
            {
                case CommandType.GoBack or CommandType.Quit:
                    return null;
                case CommandType.MoveDown or CommandType.ExpandNode:
                    cursor = (cursor + 1) % count;
                    break;
                case CommandType.MoveUp or CommandType.CollapseNode:
                    cursor = (cursor - 1 + count) % count;
                    break;
                case CommandType.ActivateLink:
                    if (cursor == all.Length)
                    {
                        return selected.ToList();
                    }

                    Toggle(selected, all[cursor]);
                    break;
            }

            if (command.RawKeyChar == ' ' && cursor < all.Length)
            {
                Toggle(selected, all[cursor]);
            }
        }

        return null;

        static void Toggle(HashSet<DayOfWeek> set, DayOfWeek day)
        {
            if (!set.Add(day))
            {
                set.Remove(day);
            }
        }
    }

    private static string DescribeSection(HierarchySection section)
    {
        string sig;
        if (section.ParentSelectors.Count > 0)
        {
            sig = section.ParentSelectors[0];
        }
        else if (section.UrlPatterns.Count > 0)
        {
            sig = section.UrlPatterns[0];
        }
        else
        {
            sig = "heading match";
        }

        return $"{section.Name}   ⟨{sig}⟩";
    }

    private static string TakeModeLabel(RecipeStep step) => step.TakeMode switch
    {
        TakeMode.SingleTopStory => "top story",
        TakeMode.TopN => $"top {step.TakeCount}",
        _ => "whole section",
    };

    // ---- shared overlay-card picker (mirrors StrategyChooserHandler.RunEntryCardAsync) ----
    private static async Task<PickResult> PickAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        RenderOptions options,
        string title,
        string prompt,
        List<string> labels,
        int cursor,
        CancellationToken ct,
        int allowRemoveAt = -1)
    {
        var card = new SetupWizardOverlay.WizardCard { Title = title, Prompt = prompt, Hint = "↑/↓ move · Enter select · Esc back", Cursor = cursor };
        foreach (var l in labels)
        {
            card.Options.Add(new SetupWizardOverlay.CardOption { Label = l });
        }

        var count = Math.Max(1, labels.Count);
        while (!ct.IsCancellationRequested)
        {
            overlay.Mode = SetupWizardOverlay.Mode.Card;
            overlay.Card = card;
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            if (command.RawKeyChar == 'x' && allowRemoveAt > 0 && card.Cursor < allowRemoveAt)
            {
                return new PickResult(card.Cursor, false, card.Cursor);
            }

            switch (command.Type)
            {
                case CommandType.GoBack or CommandType.Quit:
                    return new PickResult(-1, true, null);
                case CommandType.MoveDown or CommandType.ExpandNode:
                    card.Cursor = (card.Cursor + 1) % count;
                    break;
                case CommandType.MoveUp or CommandType.CollapseNode:
                    card.Cursor = (card.Cursor - 1 + count) % count;
                    break;
                case CommandType.ActivateLink:
                    return new PickResult(card.Cursor, false, null);
            }
        }

        return new PickResult(-1, true, null);
    }

    private static async Task<string?> PromptTextAsync(
        CommandContext ctx,
        ThemePalette palette,
        string label,
        string? initial,
        CancellationToken ct,
        Func<string, string?>? validate = null)
    {
        var field = new FormFieldConfig
        {
            Label = label,
            InitialValue = initial,
            Validate = validate,
        };
        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);
        var fieldWidth = Math.Min(Math.Max(20, Console.WindowWidth - 6), 70);
        return await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);
    }

    private readonly record struct PickResult(int Index, bool Cancelled, int? RemovedIndex);
}
