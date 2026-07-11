// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Entities.Browser;
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

            // workspace-br7w.3: after a run-now kicks off, re-read the recipes on every
            // subsequent keypress so the row's "last run" cell updates in place when the
            // background run finishes — no restart of the screen required.
            var refreshAfterRunNow = false;

            while (!ct.IsCancellationRequested)
            {
                if (refreshAfterRunNow)
                {
                    recipes = (await store.GetAllAsync().ConfigureAwait(false)).ToList();
                }

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

                    // workspace-br7w.3: plain language (no "launcher badge" jargon) and
                    // keep the list refreshing so the result appears in this row.
                    refreshAfterRunNow |= outcome == RunNowOutcome.Started;
                    ctx.NavigationService.SetStatusMessage(
                        outcome == RunNowOutcome.Started
                            ? $"Running '{recipes[cursor].Name}' in the background — the result will appear on its row here when it finishes"
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

    /// <summary>
    /// workspace-42q8.4 — 'g s': add a section of the CURRENT page (or the whole
    /// page) to a schedule without leaving the page. The card pre-fills the section
    /// the cursor is in, always offers "All stories on this page", reuses the same
    /// take/cadence pickers as the Schedules screen, and pins against the SAME
    /// config that grouped the tree the user is looking at — no re-lookup ambiguity,
    /// so what you see is what gets scheduled. A page with no saved layout gets a
    /// flat DocumentOrder config saved as part of the add (rendering-identical on
    /// revisit: flat configs route back through the grouped/ordered builder).
    /// </summary>
    public static async Task HandleAddToScheduleAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var page = ctx.NavigationService.CurrentPage;
        var tree = page?.LinkTree;
        if (ctx.NavigationService.CurrentContext.ViewMode != ViewMode.Hierarchical || page == null || tree == null || tree.TotalLinks == 0)
        {
            ctx.NavigationService.SetStatusMessage(
                "Open a site's link list first — g s schedules a section of the page you're on.",
                TimeSpan.FromSeconds(6));

            // Repaint NOW or the guidance stays invisible until the next keypress.
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        using var scope = ctx.ScopeFactory.CreateScope();
        var configStore = scope.ServiceProvider.GetRequiredService<IHierarchyConfigStore>();
        var scheduleStore = scope.ServiceProvider.GetRequiredService<IScheduleStore>();

        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var overlay = new SetupWizardOverlay.State();
        ctx.SetOverlayPainter(opts => SetupWizardOverlay.Render(overlay, palette, opts.TerminalWidth, opts.TerminalHeight));
        try
        {
            await RunQuickAddAsync(ctx, overlay, palette, options, configStore, scheduleStore, page, tree, ct).ConfigureAwait(false);
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
                ? "No schedules yet. A schedule pulls sections (like \"Top Stories\") from your sites on a cadence and auto-publishes a podcast while WireCopy is open. Quickest way in: open a site and press g s on the section you want."
                : "Recurring section recipes → auto-published podcast (runs while WireCopy is open). Tip: g s on any page adds its sections from there.",
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

                // workspace-br7w.2: name the affected steps so the warning is actionable
                // ("⚠ needs reconfigure: News, Top 5") instead of an unexplained symbol.
                var brokenSteps = await StepsNeedingReconfigureAsync(r, configStore).ConfigureAwait(false);
                var reconfigure = brokenSteps.Count > 0
                    ? $"  ⚠ needs reconfigure: {string.Join(", ", brokenSteps)}"
                    : string.Empty;
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

    /// <summary>
    /// workspace-br7w.2: returns the section names of the steps whose durable section can
    /// no longer be resolved (config deleted / re-analysis flagged / section gone), so the
    /// list row can NAME the broken steps instead of showing a bare warning symbol.
    /// </summary>
    private static async Task<List<string>> StepsNeedingReconfigureAsync(ScheduleRecipe recipe, IHierarchyConfigStore configStore)
    {
        var names = new List<string>();
        foreach (var step in recipe.Steps)
        {
            // workspace-42q8.1: durable-key-first lookup — the old URL-regex-only
            // lookup flagged healthy steps "needs reconfigure" whenever the bookmark
            // URL drifted from the URL the layout was saved on.
            var lookup = await ScheduleConfigResolution.ForStepAsync(configStore, step).ConfigureAwait(false);
            if (ScheduleEditing.StepNeedsReconfigure(lookup.Config, step))
            {
                names.Add(step.SectionName);
            }
        }

        return names;
    }

    private static async Task RunQuickAddAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        ThemePalette palette,
        RenderOptions options,
        IHierarchyConfigStore configStore,
        IScheduleStore scheduleStore,
        Page page,
        NavigationTree tree,
        CancellationToken ct)
    {
        var draft = await DraftStepFromCurrentPageAsync(ctx, overlay, palette, options, configStore, page, tree, askRequired: false, ct).ConfigureAwait(false);
        if (draft == null)
        {
            return;
        }

        // WHICH SCHEDULE — an existing recipe, or create one inline with just a name
        // and cadence. The output name defaults to the schedule name and stays
        // editable later from the Schedules screen.
        var recipes = (await scheduleStore.GetAllAsync().ConfigureAwait(false)).ToList();
        var scheduleLabels = recipes
            .Select(r => $"{r.Name}  ·  {ScheduleEditing.DescribeCadence(r.Cadence)}  ·  {r.Steps.Count} source{(r.Steps.Count == 1 ? string.Empty : "s")}")
            .ToList();
        scheduleLabels.Add("＋ New schedule…");
        var schedulePick = await PickAsync(
            ctx,
            overlay,
            options,
            "Which schedule?",
            "Enter select · Esc back",
            scheduleLabels,
            0,
            ct).ConfigureAwait(false);
        if (schedulePick.Cancelled)
        {
            return;
        }

        var step = draft.Step;
        ScheduleRecipe recipe;
        if (schedulePick.Index < recipes.Count)
        {
            var target = recipes[schedulePick.Index];
            recipe = ScheduleRecipe.Rehydrate(
                target.Id,
                target.Name,
                target.Enabled,
                target.Cadence,
                target.Steps.Append(step),
                target.OutputCollectionName,
                target.RunState,
                target.Version);
        }
        else
        {
            var defaultName = string.IsNullOrWhiteSpace(page.Metadata.Title)
                ? $"{new Uri(page.Url).Host} digest"
                : Truncate(page.Metadata.Title!, 40);
            var name = await PromptTextAsync(ctx, palette, "Schedule name", defaultName, ct, v => string.IsNullOrWhiteSpace(v) ? "Name cannot be empty" : null).ConfigureAwait(false);
            if (name == null)
            {
                return;
            }

            var cadence = await PickCadenceAsync(ctx, overlay, palette, options, null, ct).ConfigureAwait(false);
            if (cadence == null)
            {
                return;
            }

            recipe = ScheduleRecipe.Create(name, cadence, new[] { step }, name);
        }

        // Persist — the page's config first (a step must never reference a layout
        // that does not exist), then the recipe.
        if (!await PersistDraftConfigAsync(ctx, configStore, draft).ConfigureAwait(false))
        {
            return;
        }

        await scheduleStore.SaveAsync(recipe).ConfigureAwait(false);

        var nextAt = NextDueCalculator.Decide(recipe.Cadence, recipe.RunState, DateTimeOffset.Now).NextAt;
        var nextRun = nextAt is { } slot ? $" — next run {slot:ddd HH:mm}" : string.Empty;
        ctx.NavigationService.Announce(
            "✓",
            $"Added {step.SectionName} ({TakeModeLabel(step)}) to '{recipe.Name}'{nextRun} · :schedules to manage",
            shortText: $"✓ {step.SectionName} → {recipe.Name}");
    }

    /// <summary>
    /// workspace-42q8.4/.6 — the shared "pick what to pull from the CURRENT page"
    /// sequence (WHAT: cursor section / other sections / whole page, then HOW MANY,
    /// then optionally Required) used by both the g s card and the Schedules
    /// screen's "This page" source. Returns null when the user backed out. The
    /// draft's config is NOT persisted here — callers persist via
    /// <see cref="PersistDraftConfigAsync"/> once the add is actually committed.
    /// </summary>
    private static async Task<PageStepDraft?> DraftStepFromCurrentPageAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        ThemePalette palette,
        RenderOptions options,
        IHierarchyConfigStore configStore,
        Page page,
        NavigationTree tree,
        bool askRequired,
        CancellationToken ct)
    {
        // The SAME lookup the load pipeline used to build the visible tree, so the
        // sections offered here are the sections on screen.
        var lookup = await ScheduleConfigResolution.ForUrlAsync(configStore, page.Url).ConfigureAwait(false);
        var config = lookup.Config;
        if (config is { NeedsReanalyze: true })
        {
            ctx.NavigationService.SetStatusMessage(
                "This site's saved layout is flagged for re-analysis — run the setup wizard (g l) first, then schedule.",
                TimeSpan.FromSeconds(8));
            return null;
        }

        // No saved layout: the sections the user SEES (DOM auto-groups) become
        // durable name-matched sections (workspace-42q8.5); a genuinely flat page
        // schedules as a single list. Either way the config is only PERSISTED once
        // the caller commits the add (never on Esc).
        var configIsNew = config == null;
        if (config == null)
        {
            var derivedSections = AutoGroupedSectionDerivation.FromTree(tree);
            config = new SiteHierarchyConfig
            {
                Domain = HierarchyDomainKey.FromUrl(page.Url),
                UrlPattern = ScrapingStrategies.DocumentOrderStrategy.BuildUrlPattern(page.Url),
                Sections = derivedSections,
                CreatedAt = DateTime.UtcNow,
                ModelVersion = derivedSections.Count > 0 ? "auto-grouped" : "document-order",
                Kind = LayoutKind.DocumentOrder,
                Version = 2,
                Strategy = ScrapingStrategies.DocumentOrderStrategy.StrategyId,
                StructuralSignature = $"doc-order:{tree.TotalLinks}",
            };
        }

        // WHAT — cursor's section first, then the rest, then always the whole page.
        var cursorSectionName = NavigationTree.GetOwningSectionHeader(tree.CurrentSelection)?.Link.DisplayText;
        var choices = BuildQuickAddChoices(config, cursorSectionName);
        QuickAddChoice chosen;
        if (choices.Count == 1)
        {
            chosen = choices[0]; // single-list page — nothing to pick
        }
        else
        {
            var whatPick = await PickAsync(
                ctx,
                overlay,
                options,
                "Add to schedule",
                "A schedule pulls this every run and auto-publishes a podcast. Enter select · Esc back",
                choices.Select(c => c.Label).ToList(),
                0,
                ct).ConfigureAwait(false);
            if (whatPick.Cancelled)
            {
                return null;
            }

            chosen = choices[whatPick.Index];
        }

        // HOW MANY — same picker as the Schedules screen.
        var take = await PickTakeAsync(ctx, overlay, palette, options, ct).ConfigureAwait(false);
        if (take == null)
        {
            return null;
        }

        // Required: the quick-add default is "must contribute" (that is what adding
        // to a digest means); the Schedules editor asks, matching its bookmark path.
        var required = true;
        if (askRequired)
        {
            var requiredPick = await PickAsync(ctx, overlay, options, "Required?", "A required section that yields nothing fails the run loudly (never a silent empty episode). Enter select · Esc back", new List<string> { "Required (must contribute)", "Optional (skip if empty)" }, 0, ct).ConfigureAwait(false);
            if (requiredPick.Cancelled)
            {
                return null;
            }

            required = requiredPick.Index == 0;
        }

        var host = new Uri(page.Url).Host;
        var step = chosen.Section != null
            ? ScheduleEditing.BuildStep(page.Url, host, config.UrlPattern, chosen.Section, take.Value.Mode, take.Value.Count, required)
            : ScheduleEditing.BuildWholePageStep(page.Url, host, config.UrlPattern, take.Value.Mode, take.Value.Count, required);
        return new PageStepDraft(step, config, configIsNew);
    }

    /// <summary>Persists a draft's freshly-created page config; true when the add may proceed.</summary>
    private static async Task<bool> PersistDraftConfigAsync(CommandContext ctx, IHierarchyConfigStore configStore, PageStepDraft draft)
    {
        if (!draft.ConfigIsNew)
        {
            return true;
        }

        if (await configStore.SaveConfigAsync(draft.Config).ConfigureAwait(false))
        {
            return true;
        }

        ctx.NavigationService.SetStatusMessage(
            "Could not save this page's layout config (disk?) — the schedule step was NOT added.",
            TimeSpan.FromSeconds(8),
            StatusSeverity.Error);
        return false;
    }

    /// <summary>
    /// workspace-42q8.4 — the ordered WHAT options for the quick-add card: the
    /// cursor's section first (marked), the layout's other sections in order, and
    /// ALWAYS "All stories on this page" last. Pure so it is unit-tested directly.
    /// </summary>
#pragma warning disable SA1202 // helper kept adjacent to its sole caller
    internal static List<QuickAddChoice> BuildQuickAddChoices(SiteHierarchyConfig config, string? cursorSectionName)
    {
        ArgumentNullException.ThrowIfNull(config);
        var sections = config.Sections.OrderBy(s => s.SortOrder).ToList();
        var cursorSection = cursorSectionName == null
            ? null
            : sections.FirstOrDefault(s => string.Equals(s.Name, cursorSectionName, StringComparison.OrdinalIgnoreCase));

        var choices = new List<QuickAddChoice>();
        if (cursorSection != null)
        {
            // No "▸" here — that is the card CURSOR's glyph, and a label carrying
            // it reads as a phantom second cursor on unselected rows.
            choices.Add(new QuickAddChoice($"{cursorSection.Name}   ⟨the section you're on⟩", cursorSection));
        }

        choices.AddRange(sections
            .Where(s => !ReferenceEquals(s, cursorSection))
            .Select(s => new QuickAddChoice(DescribeSection(s), s)));
        choices.Add(new QuickAddChoice($"{RecipeStep.WholePageSectionName}   ⟨everything on the page⟩", null));
        return choices;
    }
#pragma warning restore SA1202

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

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
            ctx.NavigationService.SetStatusMessage($"Could not save: {ex.Message}", TimeSpan.FromSeconds(8), StatusSeverity.Error);
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

            // workspace-br7w.4: the Done label reflects the SAME condition the Done
            // validation below checks (≥1 REQUIRED step) — not merely "any step".
            labels.Add(steps.Any(s => s.Required) ? "✓ Done — choose cadence" : "✓ Done (needs ≥1 required step)");

            var choice = await PickAsync(ctx, overlay, options, "Sources", "Each source pins a durable section. Top → first, etc. ↑/↓ move · Enter edit/select · x remove · Esc cancel", labels, 0, ct, allowRemoveAt: steps.Count).ConfigureAwait(false);

            if (choice.Cancelled)
            {
                return null;
            }

            if (choice.RemovedIndex is { } removeAt && removeAt < steps.Count)
            {
                // workspace-br7w.7: confirm the removal in the status line.
                ctx.NavigationService.SetStatusMessage($"Removed step: {steps[removeAt].SectionName}", TimeSpan.FromSeconds(5));
                steps.RemoveAt(removeAt);
                continue;
            }

            var index = choice.Index;
            if (index < steps.Count)
            {
                // workspace-br7w.1: selecting an existing step opens a small per-step
                // menu (change take mode / toggle required / remove) instead of being
                // a silent no-op. Reorder remains x + re-add in v1.
                var (remove, updated) = await EditStepAsync(ctx, overlay, palette, options, steps[index], ct).ConfigureAwait(false);
                if (remove)
                {
                    ctx.NavigationService.SetStatusMessage($"Removed step: {steps[index].SectionName}", TimeSpan.FromSeconds(5));
                    steps.RemoveAt(index);
                }
                else if (updated != null)
                {
                    steps[index] = updated;
                }

                continue;
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

    /// <summary>
    /// workspace-frpl.15 (B12b) — set an UNCONFIGURED bookmarked site up inline: confirm,
    /// load it unattended through B5's preload-context-serialized path, run the SAME
    /// SetupWizard the g l flow uses (unattended adapters, null screenshot), and persist
    /// the resulting durable config so its section becomes pickable. Returns the saved
    /// config, or null if AI is unavailable / the user cancelled / no config was produced.
    /// workspace-42q8.1: the confirm prompt is honest about WHY setup is needed — a site
    /// whose layout simply doesn't cover this bookmark's page is told that, never the
    /// blanket (often false) "has no saved layout".
    /// </summary>
    private static async Task<SiteHierarchyConfig?> TryInlineSetupAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        ThemePalette palette,
        RenderOptions options,
        Bookmark bookmark,
        ScheduleConfigLookup lookup,
        CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var analyzer = scope.ServiceProvider.GetService<IHierarchyAnalyzer>();
        var loader = scope.ServiceProvider.GetService<IUnattendedSectionLoader>();
        var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
        if (analyzer is null || loader is null || configStore is null)
        {
            return null;
        }

        if (!analyzer.IsConfigured)
        {
            ctx.NavigationService.SetStatusMessage(
                $"'{bookmark.Name}' isn't set up yet — auto-setup needs an OpenAI API key (press c on the launcher to add it), or open the site and run the Setup Wizard (g l).",
                TimeSpan.FromSeconds(10));
            return null;
        }

        // Truthful situation line (workspace-42q8.1): the blanket "has no saved
        // layout" was often a lie — a layout existed for another page of the site,
        // or existed as a single list.
        var situation = lookup switch
        {
            // With whole-page steps (42q8.2) a flat layout never lands here, so a
            // non-null config can only mean the re-analysis flag.
            { Config: not null } =>
                $"'{bookmark.Name}' has a saved layout, but it is flagged for re-analysis (the site changed).",
            { SiteHasAnyConfig: true } =>
                $"'{bookmark.Name}' has a saved layout, but it covers {ScheduleConfigResolution.DescribeSitePatterns(lookup.SiteConfigs)} — not this exact page.",
            _ => $"'{bookmark.Name}' has no saved layout yet.",
        };

        var setup = await PickAsync(ctx, overlay, options, "Set up this site?", $"{situation} Set this page up now with AI over a background load? Enter select · Esc back", new List<string> { "Set up with AI now", "Cancel" }, 0, ct).ConfigureAwait(false);
        if (setup.Cancelled || setup.Index != 0)
        {
            return null;
        }

        // Unattended background load (headful browser; B5 serializes against the preload loop); show a static spinner card.
        overlay.Mode = SetupWizardOverlay.Mode.Analyzing;
        overlay.AnalyzingLabel = $"Loading {bookmark.Name}…";
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        var load = await loader.LoadLinksAndConfigAsync(bookmark.Url, ct).ConfigureAwait(false);
        if (load.Outcome != LoadOutcome.Ok || load.Links.Count == 0)
        {
            ctx.NavigationService.SetStatusMessage(
                $"Couldn't load {bookmark.Name} in the background ({load.Outcome}) — try opening it once in WireCopy first.",
                TimeSpan.FromSeconds(8));
            return null;
        }

        var result = await SetupWizard.RunAsync(
            analyzer,
            ctx.InputHandler,
            c => ctx.RenderCurrentPageAsync(options, c),
            overlay,
            load.Links.ToList(),
            bookmark.Url,
            screenshot: null,
            new ModelRoundTripBudget(),
            c => PromptTextAsync(ctx, palette, "Tell the AI what to change", null, c),
            applyPreview: null,
            lens: null,
            ct).ConfigureAwait(false);

        if (result.Cancelled || result.Config is null)
        {
            return null;
        }

        await configStore.SaveConfigAsync(result.Config).ConfigureAwait(false);
        ctx.NavigationService.SetStatusMessage($"✔ Saved a layout for {bookmark.Name} — pick its section.", TimeSpan.FromSeconds(5));
        return result.Config;
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
        // workspace-42q8.6: the page the user is LOOKING AT is the most likely
        // source — offer it first, ahead of the bookmark list.
        var currentPage = ctx.NavigationService.CurrentPage;
        var currentTree = currentPage?.LinkTree;
        var hasCurrentPage = ctx.NavigationService.CurrentContext.ViewMode == ViewMode.Hierarchical
                             && currentPage != null && currentTree is { TotalLinks: > 0 };

        var bookmarks = (await bookmarkService.GetAllBookmarksAsync(ct).ConfigureAwait(false)).ToList();
        if (bookmarks.Count == 0 && !hasCurrentPage)
        {
            ctx.NavigationService.SetStatusMessage("No bookmarks yet — open or bookmark a site first, then add it here.", TimeSpan.FromSeconds(6));
            return null;
        }

        var siteLabels = new List<string>();
        if (hasCurrentPage)
        {
            var pageName = string.IsNullOrWhiteSpace(currentPage!.Metadata.Title) ? currentPage.Url : Truncate(currentPage.Metadata.Title!, 40);
            siteLabels.Add($"This page — {pageName}");
        }

        siteLabels.AddRange(bookmarks.Select(b => $"{b.Name}  ·  {b.Url}"));
        var pick = await PickAsync(ctx, overlay, options, "Pick a site", "Choose where to pull a section from. Enter select · Esc back", siteLabels, 0, ct).ConfigureAwait(false);
        if (pick.Cancelled)
        {
            return null;
        }

        if (hasCurrentPage && pick.Index == 0)
        {
            var draft = await DraftStepFromCurrentPageAsync(ctx, overlay, palette, options, configStore, currentPage!, currentTree!, askRequired: true, ct).ConfigureAwait(false);
            if (draft == null)
            {
                return null;
            }

            // Commit the page's config now — the step this returns is only held in
            // the editor's working list, and it must never reference a layout that
            // does not exist (mirrors how inline AI setup persists immediately).
            return await PersistDraftConfigAsync(ctx, configStore, draft).ConfigureAwait(false) ? draft.Step : null;
        }

        var bookmark = bookmarks[pick.Index - (hasCurrentPage ? 1 : 0)];
        var lookup = await ScheduleConfigResolution.ForUrlAsync(configStore, bookmark.Url).ConfigureAwait(false);
        var config = lookup.Config;

        // workspace-42q8.6: the bookmark's exact URL may miss while the SITE has
        // perfectly good saved layouts (host/path drift) — offer them before any
        // AI re-setup, naming where each applies.
        if (!ScheduleEditing.UsableConfig(config))
        {
            var usableSiteConfigs = lookup.SiteConfigs.Where(c => ScheduleEditing.UsableConfig(c)).ToList();
            if (usableSiteConfigs.Count > 0)
            {
                var offerLabels = usableSiteConfigs
                    .Select(c => $"Use saved layout for {ScheduleConfigResolution.HumanizePattern(c.UrlPattern)}")
                    .ToList();
                offerLabels.Add("Set this page up with AI instead");
                var offer = await PickAsync(
                    ctx,
                    overlay,
                    options,
                    "Site is set up — different page",
                    $"'{bookmark.Name}' has a saved layout, just not for this exact page. Enter select · Esc back",
                    offerLabels,
                    0,
                    ct).ConfigureAwait(false);
                if (offer.Cancelled)
                {
                    return null;
                }

                if (offer.Index < usableSiteConfigs.Count)
                {
                    config = usableSiteConfigs[offer.Index];
                }
            }
        }

        if (!ScheduleEditing.UsableConfig(config))
        {
            // workspace-frpl.15 (B12b): rather than dead-end, offer to set the site up
            // INLINE over a background load. Returns a freshly-saved config or null if
            // the user backed out / setup didn't produce one. workspace-42q8.2: a FLAT
            // result is fine now — it schedules as a whole-page step — so the gate is
            // "usable config", no longer "has sections".
            config = await TryInlineSetupAsync(ctx, overlay, palette, options, bookmark, lookup, ct).ConfigureAwait(false);
            if (!ScheduleEditing.UsableConfig(config))
            {
                return null;
            }
        }

        // workspace-42q8.2: sections when the layout has them, and ALWAYS the
        // whole-page option — a single-list site simply has only that one.
        var sections = config!.Sections.OrderBy(s => s.SortOrder).ToList();
        var sectionLabels = sections.Select(DescribeSection).ToList();
        sectionLabels.Add($"{RecipeStep.WholePageSectionName}   ⟨everything on the page⟩");
        var prompt = sections.Count > 0
            ? $"Durable sections saved for {config.Domain}. Enter select · Esc back"
            : $"The saved layout for {config.Domain} is a single list — schedule all of its stories. Enter select · Esc back";
        var sectionPick = await PickAsync(ctx, overlay, options, "Pick a section", prompt, sectionLabels, 0, ct).ConfigureAwait(false);
        if (sectionPick.Cancelled)
        {
            return null;
        }

        var wholePage = sectionPick.Index >= sections.Count;

        var take = await PickTakeAsync(ctx, overlay, palette, options, ct).ConfigureAwait(false);
        if (take == null)
        {
            return null;
        }

        var requiredPick = await PickAsync(ctx, overlay, options, "Required?", "A required section that yields nothing fails the run loudly (never a silent empty episode). Enter select · Esc back", new List<string> { "Required (must contribute)", "Optional (skip if empty)" }, 0, ct).ConfigureAwait(false);
        if (requiredPick.Cancelled)
        {
            return null;
        }

        return wholePage
            ? ScheduleEditing.BuildWholePageStep(bookmark.Url, new Uri(bookmark.Url).Host, config.UrlPattern, take.Value.Mode, take.Value.Count, required: requiredPick.Index == 0)
            : ScheduleEditing.BuildStep(bookmark.Url, new Uri(bookmark.Url).Host, config.UrlPattern, sections[sectionPick.Index], take.Value.Mode, take.Value.Count, required: requiredPick.Index == 0);
    }

    /// <summary>
    /// Shared "how many stories?" picker (add-step + per-step edit). Returns the chosen
    /// mode with a NORMALISED count (WholeSection ⇒ null, SingleTopStory ⇒ 1, TopN ⇒ N),
    /// or null when the user backed out.
    /// </summary>
    private static async Task<(TakeMode Mode, int? Count)?> PickTakeAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        ThemePalette palette,
        RenderOptions options,
        CancellationToken ct)
    {
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
        int? takeCount = takeMode == TakeMode.SingleTopStory ? 1 : null;
        if (takeMode == TakeMode.TopN)
        {
            var countText = await PromptTextAsync(ctx, palette, "How many stories (N)?", "3", ct, v => int.TryParse(v, out var n) && n >= 1 ? null : "Enter a whole number ≥ 1").ConfigureAwait(false);
            if (countText == null)
            {
                return null;
            }

            takeCount = int.Parse(countText);
        }

        return (takeMode, takeCount);
    }

    /// <summary>
    /// workspace-br7w.1 — the small per-step menu opened by selecting an existing step
    /// in the sources list: change the take mode, toggle required/optional, or remove
    /// the step. Returns (Remove, Updated); both false/null when the user backed out.
    /// </summary>
    private static async Task<(bool Remove, RecipeStep? Updated)> EditStepAsync(
        CommandContext ctx,
        SetupWizardOverlay.State overlay,
        ThemePalette palette,
        RenderOptions options,
        RecipeStep step,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var menu = await PickAsync(
                ctx,
                overlay,
                options,
                $"Edit step — {step.SectionName}",
                $"{step.SectionName} ({step.Domain}). To reorder, remove (x) and re-add. Enter select · Esc back",
                new List<string>
                {
                    $"How many stories: {TakeModeLabel(step)} — change…",
                    $"{(step.Required ? "Required (must contribute)" : "Optional (skip if empty)")} — toggle",
                    "Remove this step",
                    "← Back",
                },
                0,
                ct).ConfigureAwait(false);

            if (menu.Cancelled || menu.Index == 3)
            {
                return (false, null);
            }

            switch (menu.Index)
            {
                case 0:
                    var take = await PickTakeAsync(ctx, overlay, palette, options, ct).ConfigureAwait(false);
                    if (take != null)
                    {
                        // PickTakeAsync already normalised the count for the mode, so the
                        // record copy keeps RecipeStep.Create's TakeMode/TakeCount invariants.
                        return (false, step with { TakeMode = take.Value.Mode, TakeCount = take.Value.Count });
                    }

                    break; // cancelled the take picker → back to the menu
                case 1:
                    return (false, step with { Required = !step.Required });
                case 2:
                    return (true, null);
            }
        }

        return (false, null);
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
        var timeText = await PromptTextAsync(ctx, palette, "Time of day (24h, HH:mm, local time)", defaultTime, ct, v => TimeOnly.TryParse(v, out _) ? null : "Enter a time like 07:00").ConfigureAwait(false);
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
            ctx.NavigationService.SetStatusMessage($"Invalid cadence: {ex.Message}", TimeSpan.FromSeconds(6), StatusSeverity.Error);
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
        _ => step.Scope == StepScope.WholePage ? "whole page" : "whole section",
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
        var fieldWidth = Math.Min(Math.Max(20, UI.OverlayViewport.Width - 6), 70);
        return await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);
    }

    private readonly record struct PickResult(int Index, bool Cancelled, int? RemovedIndex);

    /// <summary>One WHAT option on the quick-add card (null Section = whole page, workspace-42q8.4).</summary>
    internal sealed record QuickAddChoice(string Label, HierarchySection? Section);

    /// <summary>
    /// A step drafted from the current page plus the config it pins against;
    /// ConfigIsNew marks a freshly-created flat config not yet persisted (workspace-42q8.4).
    /// </summary>
    private sealed record PageStepDraft(RecipeStep Step, SiteHierarchyConfig Config, bool ConfigIsNew);
}
