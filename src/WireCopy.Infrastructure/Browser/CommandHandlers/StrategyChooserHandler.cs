// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Animations;
using WireCopy.Infrastructure.Browser.UI.Components;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Per-domain scraping strategy chooser. Replaces the old visual-variant
/// "alternate layouts" cycling. Lists Document Order / AI Curated / RSS Feed,
/// previews each, and persists the chosen strategy via
/// <see cref="IHierarchyConfigStore"/>.
/// </summary>
internal static class StrategyChooserHandler
{
    /// <summary>
    /// workspace-ujxu: per-strategy preflight default (all selected). User
    /// can deselect strategies they don't want to probe via Space in the
    /// pre-flight overlay phase. State doesn't persist across invocations —
    /// the chooser opens with all rows selected each time.
    /// </summary>
    private const bool PreflightDefaultSelected = true;

    /// <summary>
    /// Per-strategy availability probe budget (workspace-in59). Previously a
    /// slow RSS probe could stall the whole chooser for ~3 minutes on
    /// macleans.ca and similar sites without an advertised feed. With a
    /// 5-second cap per strategy, the worst case is bounded at
    /// <c>5s × strategy count</c> (~15s for 3 strategies).
    /// </summary>
    private static readonly TimeSpan StrategyProbeBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Opens the scraping-strategy chooser for the current page.
    /// Pre-flight phase: user picks which strategies to probe via Space.
    /// Probing phase: live overlay shows ⠋/✓/✗ per row.
    /// Preview phase: ◀/▶ cycles, overlay highlights the active row.
    /// </summary>
    public static async Task HandleOpenChooserAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var navContext = ctx.NavigationService.CurrentContext;
        if (navContext.ViewMode != ViewMode.Hierarchical || navContext.CurrentPage == null)
        {
            return;
        }

        if (ctx.NavigationService.IsInPreviewMode)
        {
            ClearOverlay(ctx);
            ctx.NavigationService.CancelPreview();
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        using var scope = ctx.ScopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetService<IScrapingStrategyRegistry>();
        if (registry == null || registry.All.Count == 0)
        {
            ctx.NavigationService.SetStatusMessage("Strategy chooser unavailable");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var page = navContext.CurrentPage;
        var html = page.RawHtml ?? string.Empty;
        var buildCache = ctx.PageCache.TryGetBuildCache(page.Url);
        var links = (IReadOnlyList<LinkInfo>)(buildCache?.Links ?? new List<LinkInfo>());

        // workspace-1wm7: screenshot capture (0.5-3s) and savedConfig disk
        // read are only needed by the per-strategy probes that fire AFTER
        // the user confirms pre-flight. Defer both until after RunPreflightAsync
        // returns true so the modal renders <100ms after Ctrl+L.
        // Build the overlay's row list up-front so the user sees every
        // registered strategy in the pre-flight checkbox modal — including
        // strategies that would be unavailable today (no key, etc.). This
        // is the discoverability surface workspace-33jw aimed at.
        var overlay = new StrategyChooserOverlay.State
        {
            CurrentPhase = StrategyChooserOverlay.Phase.Preflight,
            Footnote = "RSS probe can take up to 5s on sites without an advertised feed.",
        };
        foreach (var strategy in registry.All)
        {
            overlay.Rows.Add(new StrategyChooserOverlay.Row
            {
                Id = strategy.Id,
                DisplayName = strategy.DisplayName,
                Detail = strategy.Description,
                Selected = PreflightDefaultSelected,
                State = StrategyChooserOverlay.RowState.Pending,
            });
        }

        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        ctx.SetOverlayPainter(opts =>
        {
            if (overlay.CurrentPhase == StrategyChooserOverlay.Phase.Preview)
            {
                var idx = ctx.NavigationService.GetCurrentPreviewIndex();
                if (idx >= 0 && idx < overlay.Rows.Count)
                {
                    overlay.Cursor = idx;
                }
            }

            StrategyChooserOverlay.Render(overlay, palette, opts.TerminalWidth, opts.TerminalHeight);
        });

        var enteredPreviewMode = false;
        try
        {
            // PHASE 1: pre-flight selection
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            var confirmed = await RunPreflightAsync(ctx, overlay, options, ct).ConfigureAwait(false);
            if (!confirmed)
            {
                return;
            }

            // PHASE 2: probe only the selected strategies
            overlay.CurrentPhase = StrategyChooserOverlay.Phase.Probing;
            var selectedRows = overlay.Rows.Where(r => r.Selected).ToList();

            // Reshape the overlay rows to match the candidates list 1:1 —
            // unselected strategies are dropped from the modal entirely
            // (the user explicitly opted out, so they shouldn't appear in
            // the preview-phase comparison list either).
            overlay.Rows.Clear();
            foreach (var r in selectedRows)
            {
                overlay.Rows.Add(r);
            }

            // workspace-1wm7: kick off screenshot + savedConfig IO in
            // parallel here (deferred from the pre-flight phase). Render a
            // "Preparing…" footnote so the user sees activity while the
            // ~1-3s screenshot capture completes.
            overlay.Footnote = "Preparing — capturing screenshot and loading saved config…";
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            var screenshotTask = CaptureScreenshotSafelyAsync(scope, ctx.Logger);
            var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
            var savedConfigTask = configStore != null
                ? configStore.GetConfigAsync(page.Url)
                : Task.FromResult<SiteHierarchyConfig?>(null);

            await Task.WhenAll(screenshotTask, savedConfigTask).ConfigureAwait(false);
            var screenshot = await screenshotTask.ConfigureAwait(false);
            var savedConfig = await savedConfigTask.ConfigureAwait(false);

            var stContext = new ScrapingStrategyContext
            {
                PageUrl = page.Url,
                Html = html,
                Links = links,
                Screenshot = screenshot,
                SavedConfig = savedConfig,
            };

            overlay.Footnote = $"Probing {selectedRows.Count} strategy(ies) — up to {StrategyProbeBudget.TotalSeconds:0}s each.";
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            var candidates = await ProbeSelectedAsync(
                ctx,
                registry,
                stContext,
                page,
                savedConfig,
                overlay,
                options,
                ct).ConfigureAwait(false);

            if (candidates.Count == 0)
            {
                ctx.NavigationService.SetStatusMessage("Scraping strategies: no candidates available");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                return;
            }

            // PHASE 3: enter preview mode; painter stays installed and the
            // cursor row tracks NavigationService.GetCurrentPreviewIndex.
            overlay.CurrentPhase = StrategyChooserOverlay.Phase.Preview;
            overlay.Cursor = 0;
            overlay.Footnote = $"{candidates.Count} candidates — ◀/▶ to compare, Enter to save the highlighted one.";

            ctx.NavigationService.EnterPreviewMode(candidates);
            enteredPreviewMode = true;
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // user cancelled; clean exit
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Strategy chooser failed");
            ctx.NavigationService.SetStatusMessage("Strategy chooser failed");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
        finally
        {
            // If we never reached the Preview phase, drop the painter now.
            // Otherwise the orchestrator's preview-mode Apply/Cancel handlers
            // clear it via ClearOverlay below.
            if (!enteredPreviewMode)
            {
                ClearOverlay(ctx);
            }
        }
    }

    /// <summary>
    /// Apply the selected strategy preview, run the AI analyzer if the
    /// candidate was a stub (AI Curated, no cached result yet), and
    /// persist the resulting config.
    /// </summary>
    public static async Task HandleApplyAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        // workspace-ujxu: clear the anchored chooser overlay as soon as
        // the user commits — keeps the screen clean during the apply path
        // (which may run the AI analyzer for 30-60s under its own status
        // message) and on the post-apply re-render of the link tree.
        ClearOverlay(ctx);

        var selected = ctx.NavigationService.ApplyPreview();
        if (selected == null)
        {
            return;
        }

        // workspace-33jw: refuse to save unavailable strategies. The user pressed
        // Enter on a disabled row (e.g., AI Curated without an API key) — show a
        // hint instead of mutating the saved config.
        if (selected.IsUnavailable)
        {
            var hint = selected.UnavailableReason ?? "strategy unavailable";
            ctx.NavigationService.SetStatusMessage($"Cannot apply · {hint}");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
            var registry = scope.ServiceProvider.GetService<IScrapingStrategyRegistry>();

            var configToSave = selected.Config;

            // workspace-5oe9.8: if the user selected the AI-Curated stub, run the
            // question-driven setup wizard now (replaces the old one-shot guidance
            // prompt). The wizard proposes a pattern, asks a bounded set of
            // confirm-the-pattern questions (each showing the durable identifier),
            // and infers the final durable config.
            if (registry != null
                && configToSave.Strategy == ScrapingStrategies.AiCuratedStrategy.StrategyId
                && configToSave.AiResult == null)
            {
                var page = ctx.NavigationService.CurrentContext.CurrentPage;
                if (page != null)
                {
                    var wizardResult = await RunSetupWizardAsync(ctx, scope, page, options, ct).ConfigureAwait(false);
                    if (wizardResult.Cancelled)
                    {
                        ctx.NavigationService.SetStatusMessage("Cancelled — strategy not applied");
                        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                        return;
                    }

                    var treeBuilder = scope.ServiceProvider.GetService<INavigationTreeBuilder>();
                    var links = ctx.PageCache.TryGetBuildCache(page.Url)?.Links ?? new List<LinkInfo>();

                    if (wizardResult.UseDocumentOrder)
                    {
                        var docStrategy = registry.GetById(ScrapingStrategies.DocumentOrderStrategy.StrategyId);
                        if (docStrategy != null)
                        {
                            var docContext = new ScrapingStrategyContext
                            {
                                PageUrl = page.Url,
                                Html = page.RawHtml ?? string.Empty,
                                Links = links,
                            };
                            var docResult = await docStrategy.BuildTreeAsync(docContext, ct).ConfigureAwait(false);
                            configToSave = docResult.Config;
                            page.SetLinkTree(docResult.Tree);
                        }
                    }
                    else if (wizardResult.Config != null && treeBuilder != null)
                    {
                        configToSave = wizardResult.Config;
                        var tree = await treeBuilder.BuildTreeAsync(links, configToSave, ct).ConfigureAwait(false);
                        page.SetLinkTree(tree);
                    }
                }
            }

            if (configStore != null)
            {
                await configStore.SaveConfigAsync(configToSave).ConfigureAwait(false);
                ctx.NavigationService.SetStatusMessage($"✔ Strategy saved · {selected.Summary}");
            }
            else
            {
                ctx.NavigationService.SetStatusMessage($"✔ Applied · {selected.Summary}");
            }

            // Design-system spec: 500ms centered "Layout flash" on apply.
            // Runs after the persistent status message is queued so the
            // post-flash render picks up the new layout + status text.
            LayoutFlashAnimation.Play(
                selected.Summary,
                BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme),
                options.TerminalWidth,
                options.TerminalHeight);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to apply scraping strategy");
            ctx.NavigationService.SetStatusMessage($"Apply failed · {ex.Message}");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// workspace-ujxu: removes the anchored overlay painter. Called by the
    /// preview-mode Apply / Cancel handlers and on early exits inside the
    /// chooser itself.
    /// </summary>
    public static void ClearOverlay(CommandContext ctx)
    {
        ctx.SetOverlayPainter(null);
    }

    /// <summary>
    /// workspace-z5qz: handler for the `i` key pressed while a layout
    /// preview is active. If the currently-selected candidate is AI Curated,
    /// surface a "coming soon" status message pointing at workspace-99ve;
    /// otherwise no-op. The affordance is hinted in
    /// <see cref="AppendGuidanceHintIfAi"/>'s row-summary suffix.
    /// </summary>
    public static async Task HandleGuidanceRequestAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var candidate = ctx.NavigationService.GetCurrentPreviewCandidate();
        if (candidate?.Config.Strategy == ScrapingStrategies.AiCuratedStrategy.StrategyId)
        {
            // Keep the message short so it survives status-bar truncation
            // alongside the preview label + cache indicator. The detailed
            // affordance lives in the AI Curated row's summary suffix
            // (AppendGuidanceHintIfAi).
            ctx.NavigationService.SetStatusMessage("AI guidance — coming soon");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// workspace-z5qz: surfaces the upcoming "i for guidance" affordance on
    /// the AI Curated row's summary so the user knows they can pass
    /// editorial guidance to the analyzer. The actual prompt input ships in
    /// workspace-99ve; until then the key handler emits a "coming soon"
    /// status message.
    /// </summary>
    /// <summary>
    /// workspace-99ve: short text-field prompt for optional editorial
    /// guidance to the AI Curated analyzer. Returns:
    ///   <list type="bullet">
    ///     <item>null — user pressed Esc, abort the apply.</item>
    ///     <item>empty string — user accepted without typing, run with the
    ///       default prompt.</item>
    ///     <item>non-empty string — user-supplied guidance to pass through.</item>
    ///   </list>
    /// </summary>
    private static async Task<string?> PromptForGuidanceAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var palette = Themes.BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var field = new UI.Components.FormFieldConfig
        {
            Label = "Anything you'd like to tell the AI? (optional)",
            Subtitle = "e.g. 'exclude opinion pieces', 'put COVID first', 'group by section'",
            Placeholder = "Press Enter to use the default curation prompt, Esc to cancel.",
        };

        var fieldWidth = Math.Min(80, Math.Max(40, options.TerminalWidth - 6));
        var fieldHeight = UI.Components.FormField.HeightFor(field);
        var startRow = Math.Max(0, options.TerminalHeight - fieldHeight - 1);

        var input = await UI.Components.FormField.PromptAsync(
            ctx.InputHandler,
            field,
            palette,
            startRow,
            fieldWidth,
            ct).ConfigureAwait(false);

        return input;
    }

    /// <summary>
    /// workspace-5oe9.8: runs the question-driven AI setup wizard for the
    /// current page, painting its card overlay. Returns the wizard's outcome
    /// (durable config, document-order escape, or cancel).
    /// </summary>
    private static async Task<SetupWizard.Result> RunSetupWizardAsync(
        CommandContext ctx,
        IServiceScope scope,
        Domain.Entities.Browser.Page page,
        RenderOptions options,
        CancellationToken ct)
    {
        var analyzer = scope.ServiceProvider.GetService<IHierarchyAnalyzer>();
        if (analyzer == null || !analyzer.IsConfigured)
        {
            ctx.NavigationService.SetStatusMessage("AI Curated needs an OpenAI key · press c on the launcher");
            return new SetupWizard.Result { Cancelled = true };
        }

        var buildCache = ctx.PageCache.TryGetBuildCache(page.Url);
        var links = buildCache?.Links ?? new List<LinkInfo>();
        var screenshot = await CaptureScreenshotSafelyAsync(scope, ctx.Logger).ConfigureAwait(false);

        var overlay = new UI.Components.SetupWizardOverlay.State();
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        ctx.SetOverlayPainter(opts =>
            UI.Components.SetupWizardOverlay.Render(overlay, palette, opts.TerminalWidth, opts.TerminalHeight));

        try
        {
            var budget = new ModelRoundTripBudget();
            return await SetupWizard.RunAsync(
                analyzer,
                ctx.InputHandler,
                c => ctx.RenderCurrentPageAsync(options, c),
                overlay,
                links,
                page.Url,
                screenshot,
                budget,
                c => PromptForGuidanceAsync(ctx, options, c),
                c => PickLeadFromTreeAsync(ctx, page, options, c),
                ct).ConfigureAwait(false);
        }
        finally
        {
            ClearOverlay(ctx);
        }
    }

    /// <summary>
    /// workspace-5oe9.9: lets the user point at the main story in the previewed
    /// link tree. j/k move the tree cursor; Enter sets the highlighted link as
    /// the lead (rejecting synthetic section headers); Esc cancels the pick.
    /// Returns the chosen <see cref="LinkInfo"/> or null.
    /// </summary>
    private static async Task<LinkInfo?> PickLeadFromTreeAsync(
        CommandContext ctx,
        Domain.Entities.Browser.Page page,
        RenderOptions options,
        CancellationToken ct)
    {
        var tree = page.LinkTree;
        if (tree == null)
        {
            return null;
        }

        // Visibly distinct footer so the pick mode is unmistakable.
        ClearOverlay(ctx);
        ctx.NavigationService.SetStatusMessage("Pick the main story — j/k move · Enter set · Esc cancel", TimeSpan.FromMinutes(2));
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            switch (command.Type)
            {
                case CommandType.MoveDown or CommandType.ExpandNode:
                    tree.SelectNext();
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
                case CommandType.MoveUp or CommandType.CollapseNode:
                    tree.SelectPrevious();
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
                case CommandType.ActivateLink:
                    var node = tree.GetSelectedNode();
                    if (node == null || node.IsGroupHeader)
                    {
                        ctx.NavigationService.SetStatusMessage("Pick a story, not a section header — j/k move · Enter set · Esc cancel", TimeSpan.FromMinutes(2));
                        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                        break;
                    }

                    return node.Link;
                case CommandType.GoBack or CommandType.Quit:
                    return null;
                case CommandType.TerminalResized:
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// workspace-ujxu: pre-flight loop in the anchored modal. Space toggles
    /// the selection at the cursor; ↑/↓ moves the cursor; Enter confirms
    /// the selection and proceeds to probing; Esc cancels the chooser.
    /// Returns true if the user confirmed, false on cancel.
    /// </summary>
    private static async Task<bool> RunPreflightAsync(
        CommandContext ctx,
        StrategyChooserOverlay.State overlay,
        RenderOptions options,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            switch (command.Type)
            {
                case CommandType.MoveDown or CommandType.ExpandNode:
                    overlay.Cursor = (overlay.Cursor + 1) % overlay.Rows.Count;
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
                case CommandType.MoveUp or CommandType.CollapseNode:
                    overlay.Cursor = (overlay.Cursor - 1 + overlay.Rows.Count) % overlay.Rows.Count;
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
                case CommandType.ToggleSelection:
                    overlay.Rows[overlay.Cursor].Selected = !overlay.Rows[overlay.Cursor].Selected;
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
                case CommandType.ActivateLink:
                    if (overlay.Rows.Any(r => r.Selected))
                    {
                        return true;
                    }

                    overlay.Footnote = "Select at least one strategy with Space, then Enter.";
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
                case CommandType.GoBack or CommandType.Quit:
                    return false;
                case CommandType.TerminalResized:
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// workspace-ujxu: probes each selected strategy, updating the overlay
    /// row in place (Probing → Available/Unavailable). Mirrors the original
    /// workspace-in59 + workspace-33jw semantics — every selected strategy
    /// produces a LayoutCandidate (even unavailable ones get a disabled
    /// stub so the user sees the row in the preview phase).
    /// </summary>
    private static async Task<List<LayoutCandidate>> ProbeSelectedAsync(
        CommandContext ctx,
        IScrapingStrategyRegistry registry,
        ScrapingStrategyContext stContext,
        Domain.Entities.Browser.Page page,
        SiteHierarchyConfig? savedConfig,
        StrategyChooserOverlay.State overlay,
        RenderOptions options,
        CancellationToken ct)
    {
        var candidates = new List<LayoutCandidate>();

        for (var rowIdx = 0; rowIdx < overlay.Rows.Count; rowIdx++)
        {
            var row = overlay.Rows[rowIdx];
            var strategy = registry.GetById(row.Id);
            if (strategy == null)
            {
                row.State = StrategyChooserOverlay.RowState.Unavailable;
                row.Detail = "not registered";
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                continue;
            }

            row.State = StrategyChooserOverlay.RowState.Probing;
            row.Detail = "probing…";
            row.SpinnerFrame = 0;
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            ScrapingStrategyAvailability availability;
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(StrategyProbeBudget);
                availability = await strategy.IsAvailableAsync(stContext, probeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ctx.Logger.LogWarning(
                    "Strategy probe for {Id} exceeded {Budget}s budget",
                    strategy.Id,
                    StrategyProbeBudget.TotalSeconds);
                availability = new ScrapingStrategyAvailability
                {
                    IsAvailable = false,
                    ReasonWhenUnavailable = $"probe timed out after {StrategyProbeBudget.TotalSeconds:0}s",
                };
            }

            if (!availability.IsAvailable)
            {
                row.State = StrategyChooserOverlay.RowState.Unavailable;
                row.Detail = availability.ReasonWhenUnavailable ?? "unavailable";

                // workspace-33jw stub so the user can DISCOVER the strategy
                // in the preview phase (cycling past it shows the reason).
                var currentTreeForStub = page.LinkTree;
                if (currentTreeForStub != null)
                {
                    var disabledStub = new SiteHierarchyConfig
                    {
                        Domain = ExtractDomain(page.Url),
                        UrlPattern = ScrapingStrategies.DocumentOrderStrategy.BuildUrlPattern(page.Url),
                        Sections = new List<HierarchySection>(),
                        CreatedAt = DateTime.UtcNow,
                        ModelVersion = "unavailable",
                        Kind = LayoutKind.DocumentOrder,
                        Version = 2,
                        Strategy = strategy.Id,
                    };
                    candidates.Add(new LayoutCandidate
                    {
                        Config = disabledStub,
                        Summary = $"✗ {strategy.DisplayName} · {availability.ReasonWhenUnavailable ?? "unavailable"}",
                        PreviewTree = currentTreeForStub,
                        IsUnavailable = true,
                        UnavailableReason = availability.ReasonWhenUnavailable,
                    });
                }

                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                continue;
            }

            // Document order is fast — always preview live.
            // AI Curated only previews if cached; otherwise selecting it runs the analyzer.
            // RSS previews live (parse-feed cost is acceptable).
            bool canPreviewNow = strategy.Id != ScrapingStrategies.AiCuratedStrategy.StrategyId
                || (savedConfig?.AiResult != null);

            if (!canPreviewNow)
            {
                var currentTree = page.LinkTree;
                if (currentTree == null)
                {
                    row.State = StrategyChooserOverlay.RowState.Unavailable;
                    row.Detail = "no link tree";
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    continue;
                }

                var stubConfig = new SiteHierarchyConfig
                {
                    Domain = ExtractDomain(page.Url),
                    UrlPattern = ScrapingStrategies.DocumentOrderStrategy.BuildUrlPattern(page.Url),
                    Sections = new List<HierarchySection>(),
                    CreatedAt = DateTime.UtcNow,
                    ModelVersion = "ai-curated-pending",
                    Kind = LayoutKind.AiCurated,
                    Version = 2,
                    Strategy = strategy.Id,
                };

                candidates.Add(new LayoutCandidate
                {
                    Config = stubConfig,
                    Summary = AppendGuidanceHintIfAi(
                        $"{strategy.DisplayName} · {availability.StatusDetail ?? strategy.Description}",
                        strategy.Id),
                    PreviewTree = currentTree,
                });

                row.State = StrategyChooserOverlay.RowState.Available;
                row.Detail = availability.StatusDetail ?? strategy.Description;
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var result = await strategy.BuildTreeAsync(stContext, ct).ConfigureAwait(false);
                candidates.Add(new LayoutCandidate
                {
                    Config = result.Config,
                    Summary = AppendGuidanceHintIfAi(
                        result.Summary
                            ?? $"{strategy.DisplayName} · {availability.StatusDetail ?? strategy.Description}",
                        strategy.Id),
                    PreviewTree = result.Tree,
                });

                row.State = StrategyChooserOverlay.RowState.Available;
                row.Detail = result.Summary ?? availability.StatusDetail ?? strategy.Description;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Strategy {Id} failed to build tree", strategy.Id);
                row.State = StrategyChooserOverlay.RowState.Unavailable;
                row.Detail = ex.Message;
            }

            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
        }

        return candidates;
    }

    private static string AppendGuidanceHintIfAi(string summary, string strategyId)
    {
        if (string.Equals(strategyId, ScrapingStrategies.AiCuratedStrategy.StrategyId, StringComparison.Ordinal))
        {
            // Shift+I (capitalised to match the existing Shift+I:open
            // convention in StatusBarRenderer / KeybindingPopup) opens the
            // upcoming guidance prompt. The 'i' shortcut isn't free —
            // lowercase i has no global binding today and mapping it here
            // would shadow the (currently NoOp) raw key elsewhere.
            return $"{summary} · I:guidance";
        }

        return summary;
    }

    /// <summary>
    /// workspace-1wm7: best-effort screenshot capture from the active
    /// browser session. Returns null if no session is registered or the
    /// capture throws — failures are logged at debug since the chooser can
    /// proceed without a screenshot (only the AI Curated strategy uses it).
    /// </summary>
    private static async Task<byte[]?> CaptureScreenshotSafelyAsync(
        IServiceScope scope,
        ILogger logger)
    {
        if (scope.ServiceProvider.GetService<IBrowserSessionControl>() is not IBrowserSession session)
        {
            return null;
        }

        try
        {
            return await session.CaptureScreenshotAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Strategy chooser: screenshot capture failed");
            return null;
        }
    }

    private static string ExtractDomain(string url)
    {
        try
        {
            return new Uri(url).Host.ToLowerInvariant();
        }
        catch
        {
            return "unknown";
        }
    }
}
