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
    /// workspace-5oe9.9: Ctrl+L entry. An UNCONFIGURED link-list page opens
    /// AI-first ("Set up this site"); an already-configured page opens a compact
    /// summary. The multi-strategy preflight/probe/preview modal lives behind the
    /// entry's "Compare all strategies" option (<see cref="RunCompareModeAsync"/>).
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
        var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
        var savedConfig = configStore != null
            ? await configStore.GetConfigAsync(page.Url).ConfigureAwait(false)
            : null;

        if (ChooserEntry.Decide(savedConfig) == ChooserEntry.Mode.ConfiguredSummary && savedConfig != null)
        {
            await RunConfiguredSummaryAsync(ctx, scope, page, savedConfig, options, ct).ConfigureAwait(false);
            return;
        }

        await RunSetupEntryAsync(ctx, scope, page, options, ct).ConfigureAwait(false);
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

            // workspace-6yb7.3: if the user selected the AI-Curated stub, run the
            // preview-first setup wizard now. The wizard proposes a pattern, asks
            // the model's clarifying questions, and previews the resulting tree on
            // the real page before anything is saved.
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

                    if (wizardResult.Config != null && treeBuilder != null)
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
    /// workspace-5oe9.10: Shift+I while previewing the AI Curated row launches
    /// the question-driven setup wizard (identical to pressing Enter on that
    /// row). Replaces the old stub — the wizard shipped in workspace-5oe9.8.
    /// No-op for non-AI rows.
    /// </summary>
    public static async Task HandleGuidanceRequestAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var candidate = ctx.NavigationService.GetCurrentPreviewCandidate();
        if (candidate?.Config.Strategy == ScrapingStrategies.AiCuratedStrategy.StrategyId)
        {
            await HandleApplyAsync(ctx, options, ct).ConfigureAwait(false);
        }
    }

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
    /// Opens the scraping-strategy chooser for the current page.
    /// Pre-flight phase: user picks which strategies to probe via Space.
    /// Probing phase: live overlay shows ⠋/✓/✗ per row.
    /// Preview phase: ◀/▶ cycles, overlay highlights the active row.
    /// </summary>
    private static async Task RunCompareModeAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var navContext = ctx.NavigationService.CurrentContext;
        if (navContext.ViewMode != ViewMode.Hierarchical || navContext.CurrentPage == null)
        {
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
    /// workspace-6yb7.3: runs the preview-first AI setup wizard for the current
    /// page, painting its card overlay. The wizard previews each candidate config
    /// on the REAL link tree (via the applyPreview hook); when the user cancels,
    /// the original tree is restored so an abandoned wizard leaves no trace.
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

        // workspace-5oe9.11: the wizard's single screenshot, bounded so a slow
        // Playwright capture never stalls the AI path — go text-only past the cap.
        var screenshot = await ScreenshotCapture.WithCapAsync(
            () => CaptureScreenshotSafelyAsync(scope, ctx.Logger),
            ScreenshotCapture.DefaultCap,
            ct).ConfigureAwait(false);

        var overlay = new UI.Components.SetupWizardOverlay.State();
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        ctx.SetOverlayPainter(opts =>
            UI.Components.SetupWizardOverlay.Render(overlay, palette, opts.TerminalWidth, opts.TerminalHeight));

        // workspace-wylw: focused wizard options highlight their matched links
        // live on the sidecar lens (same surface as the article tuner).
        var session = scope.ServiceProvider.GetService<IBrowserSession>();
        var lens = BuildWizardLens(session, ctx.Logger);

        // workspace-6yb7.3: the preview hook builds candidate configs into the
        // page's real tree so the wizard's preview card sits over the actual
        // result. The original tree is restored on cancel/failure below.
        var treeBuilder = scope.ServiceProvider.GetService<INavigationTreeBuilder>();
        var originalTree = page.LinkTree;
        Func<SiteHierarchyConfig, CancellationToken, Task>? applyPreview = treeBuilder == null
            ? null
            : async (config, c) =>
            {
                var tree = await treeBuilder.BuildTreeAsync(links, config, c).ConfigureAwait(false);
                page.SetLinkTree(tree);
            };

        var completed = false;
        try
        {
            var budget = new ModelRoundTripBudget();
            var result = await SetupWizard.RunAsync(
                analyzer,
                ctx.InputHandler,
                c => ctx.RenderCurrentPageAsync(options, c),
                overlay,
                links,
                page.Url,
                screenshot,
                budget,
                c => PromptForGuidanceAsync(ctx, options, c),
                c => PickLeadFromTreeAsync(ctx, scope, page, options, c),
                applyPreview,
                lens,
                ct).ConfigureAwait(false);
            completed = !result.Cancelled;
            return result;
        }
        finally
        {
            if (!completed && originalTree != null)
            {
                page.SetLinkTree(originalTree);
            }

            ClearOverlay(ctx);
            if (lens != null)
            {
                await lens.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// workspace-wylw: lens hooks for the wizard — evaluates
    /// <see cref="TunerScript"/> (dialect css, link-list sections) on the
    /// dedicated lens tab. Every failure degrades to no-highlight; the wizard
    /// cards still show identifiers and match counts as text.
    /// </summary>
    private static SetupWizard.Lens? BuildWizardLens(IBrowserSession? session, ILogger logger)
    {
        if (session == null)
        {
            return null;
        }

        return new SetupWizard.Lens(
            HighlightCssAsync: async (css, _) =>
            {
                try
                {
                    var lensPage = await session.GetLensPageAsync().ConfigureAwait(false);
                    if (lensPage == null)
                    {
                        logger.LogInformation("Wizard lens: no lens page; cannot highlight {Selector}", css);
                        return -1;
                    }

                    var count = await lensPage.EvaluateAsync<int>(
                        TunerScript.Highlight, new { selector = css, dialect = "css" }).ConfigureAwait(false);
                    logger.LogInformation("Wizard lens: {Selector} highlighted {Count} match(es)", css, count);
                    return count;
                }
                catch (Exception ex)
                {
                    logger.LogInformation(ex, "Wizard lens: highlight failed for {Selector}", css);
                    return -1;
                }
            },
            ClearAsync: async _ =>
            {
                try
                {
                    var lensPage = await session.GetLensPageAsync().ConfigureAwait(false);
                    if (lensPage != null)
                    {
                        await lensPage.EvaluateAsync<string>(TunerScript.Clear).ConfigureAwait(false);
                    }
                }
                catch (Exception)
                {
                    // Cosmetic.
                }
            });
    }

    /// <summary>
    /// workspace-5oe9.9 / workspace-6yb7.5: lets the user point at the main
    /// story EITHER by clicking it in the sidecar browser window (PickScript
    /// arms a navigation-swallowing click trap on the lens; clicks are polled
    /// on animation ticks) OR by walking the previewed tree with j/k + Enter
    /// (always available — the only path when the dock is hidden). Esc cancels.
    /// Returns the chosen <see cref="LinkInfo"/> or null.
    /// </summary>
    private static async Task<LinkInfo?> PickLeadFromTreeAsync(
        CommandContext ctx,
        IServiceScope scope,
        Domain.Entities.Browser.Page page,
        RenderOptions options,
        CancellationToken ct)
    {
        var tree = page.LinkTree;
        if (tree == null)
        {
            return null;
        }

        var links = ctx.PageCache.TryGetBuildCache(page.Url)?.Links ?? new List<LinkInfo>();
        var session = scope.ServiceProvider.GetService<IBrowserSession>();
        var lensPage = await ArmLensPickAsync(session, ctx.Logger).ConfigureAwait(false);

        // Visibly distinct footer so the pick mode is unmistakable.
        ClearOverlay(ctx);
        var hint = lensPage != null
            ? "Pick the main story — click it in the browser window, or j/k + Enter here · Esc cancel"
            : "Pick the main story — j/k move · Enter set · Esc cancel";
        ctx.NavigationService.SetStatusMessage(hint, TimeSpan.FromMinutes(2));
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        // Poll the lens for clicks between keystrokes via the animation-tick
        // race; the prior animation state is restored on every exit path.
        var animation = ctx.InputHandler.AnimationController;
        var hadAnimation = animation.AnimationState.HasActiveAnimation;
        var savedInterval = animation.AnimationIntervalMs;
        if (lensPage != null && !hadAnimation)
        {
            animation.AnimationIntervalMs = 200;
            animation.StartAnimation();
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
                switch (command.Type)
                {
                    case CommandType.AnimationTick:
                        var pick = await PollLensPickAsync(lensPage, ctx.Logger).ConfigureAwait(false);
                        if (pick != null)
                        {
                            return pick.ToLinkInfo(links);
                        }

                        break;
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
        finally
        {
            if (lensPage != null && !hadAnimation)
            {
                animation.StopAnimation();
                animation.AnimationIntervalMs = savedInterval;
            }

            await DisarmLensPickAsync(lensPage, ctx.Logger).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Arms <see cref="PickScript"/> on the lens tab; null when no sidecar /
    /// lens page exists or the evaluation fails (keyboard pick still works).
    /// </summary>
    private static async Task<Microsoft.Playwright.IPage?> ArmLensPickAsync(IBrowserSession? session, ILogger logger)
    {
        if (session == null)
        {
            return null;
        }

        try
        {
            var lensPage = await session.GetLensPageAsync().ConfigureAwait(false);
            if (lensPage == null)
            {
                return null;
            }

            await lensPage.EvaluateAsync<string>(PickScript.Arm).ConfigureAwait(false);
            return lensPage;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Lens pick: arming failed; keyboard pick only");
            return null;
        }
    }

    private static async Task<LensPick?> PollLensPickAsync(Microsoft.Playwright.IPage? lensPage, ILogger logger)
    {
        if (lensPage == null)
        {
            return null;
        }

        try
        {
            var json = await lensPage.EvaluateAsync<string>(PickScript.Poll).ConfigureAwait(false);
            return LensPick.Parse(json);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Lens pick: poll failed");
            return null;
        }
    }

    private static async Task DisarmLensPickAsync(Microsoft.Playwright.IPage? lensPage, ILogger logger)
    {
        if (lensPage == null)
        {
            return;
        }

        try
        {
            await lensPage.EvaluateAsync<string>(PickScript.Disarm).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Lens pick: disarm failed (page may have navigated)");
        }
    }

    /// <summary>
    /// workspace-5oe9.9: AI-first entry for an unconfigured page. AI is the
    /// highlighted primary action (dim + key-hint when no OpenAI key); Document
    /// Order and "Compare all strategies" are secondary. No probe runs here.
    /// </summary>
    private static async Task RunSetupEntryAsync(
        CommandContext ctx,
        IServiceScope scope,
        Domain.Entities.Browser.Page page,
        RenderOptions options,
        CancellationToken ct)
    {
        var analyzer = scope.ServiceProvider.GetService<IHierarchyAnalyzer>();
        var buildCache = ctx.PageCache.TryGetBuildCache(page.Url);
        var contentCount = (buildCache?.Links ?? new List<LinkInfo>())
            .Count(l => l.Type == Domain.Enums.Browser.LinkType.Content);
        var aiAvailable = ChooserEntry.AiSetupAvailable(analyzer?.IsConfigured ?? false, contentCount);

        var overlay = new UI.Components.SetupWizardOverlay.State();
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        ctx.SetOverlayPainter(opts =>
            UI.Components.SetupWizardOverlay.Render(overlay, palette, opts.TerminalWidth, opts.TerminalHeight));

        int choice;
        try
        {
            var card = new UI.Components.SetupWizardOverlay.WizardCard
            {
                Title = "Set up this site",
                Prompt = "How should WireCopy read this site?",
                Options =
                {
                    new UI.Components.SetupWizardOverlay.CardOption
                    {
                        Label = aiAvailable
                            ? "✨ Let AI find the stories (recommended)"
                            : "AI setup — add an OpenAI key first (press c on the launcher)",
                    },
                    new UI.Components.SetupWizardOverlay.CardOption { Label = "Document order — show every link" },
                    new UI.Components.SetupWizardOverlay.CardOption { Label = "Compare all strategies…" },
                },
                Cursor = aiAvailable ? 0 : 1,
                Hint = "↑/↓ choose · Enter select · Esc cancel",
            };
            choice = await RunEntryCardAsync(ctx, overlay, card, options, ct).ConfigureAwait(false);
        }
        finally
        {
            ClearOverlay(ctx);
        }

        switch (choice)
        {
            case 0 when aiAvailable:
                await RunAiSetupAndApplyAsync(ctx, scope, page, options, ct).ConfigureAwait(false);
                break;
            case 0:
                ctx.NavigationService.SetStatusMessage("AI setup needs an OpenAI key — press c on the launcher to add one");
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
            case 1:
                await RunStrategyAndApplyAsync(ctx, scope, page, ScrapingStrategies.DocumentOrderStrategy.StrategyId, options, ct).ConfigureAwait(false);
                break;
            case 2:
                await RunCompareModeAsync(ctx, options, ct).ConfigureAwait(false);
                break;
            default:
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// workspace-5oe9.9: compact summary for an already-configured page. Shows
    /// the current strategy (surfacing a legacy-snapshot re-run nudge) and offers
    /// reconfigure / reset / compare instead of re-running setup unprompted.
    /// </summary>
    private static async Task RunConfiguredSummaryAsync(
        CommandContext ctx,
        IServiceScope scope,
        Domain.Entities.Browser.Page page,
        SiteHierarchyConfig config,
        RenderOptions options,
        CancellationToken ct)
    {
        var overlay = new UI.Components.SetupWizardOverlay.State();
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        ctx.SetOverlayPainter(opts =>
            UI.Components.SetupWizardOverlay.Render(overlay, palette, opts.TerminalWidth, opts.TerminalHeight));

        int choice;
        try
        {
            var card = new UI.Components.SetupWizardOverlay.WizardCard
            {
                Title = "Layout",
                Prompt = ChooserEntry.DescribeConfig(config),
                Options =
                {
                    new UI.Components.SetupWizardOverlay.CardOption { Label = "Reconfigure with AI" },
                    new UI.Components.SetupWizardOverlay.CardOption { Label = "Reset to document order" },
                    new UI.Components.SetupWizardOverlay.CardOption { Label = "Compare all strategies…" },
                    new UI.Components.SetupWizardOverlay.CardOption { Label = "Close" },
                },
                Cursor = ConfigMigration.NeedsReanalysis(config) ? 0 : 3,
                Hint = "↑/↓ choose · Enter select · Esc close",
            };
            choice = await RunEntryCardAsync(ctx, overlay, card, options, ct).ConfigureAwait(false);
        }
        finally
        {
            ClearOverlay(ctx);
        }

        switch (choice)
        {
            case 0:
                await RunAiSetupAndApplyAsync(ctx, scope, page, options, ct).ConfigureAwait(false);
                break;
            case 1:
                var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
                if (configStore != null)
                {
                    await configStore.DeleteConfigAsync(page.Url).ConfigureAwait(false);
                }

                await RunStrategyAndApplyAsync(ctx, scope, page, ScrapingStrategies.DocumentOrderStrategy.StrategyId, options, ct).ConfigureAwait(false);
                break;
            case 2:
                await RunCompareModeAsync(ctx, options, ct).ConfigureAwait(false);
                break;
            default:
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Runs the AI setup wizard from the entry, then saves + applies its result.</summary>
    private static async Task RunAiSetupAndApplyAsync(
        CommandContext ctx,
        IServiceScope scope,
        Domain.Entities.Browser.Page page,
        RenderOptions options,
        CancellationToken ct)
    {
        var wizardResult = await RunSetupWizardAsync(ctx, scope, page, options, ct).ConfigureAwait(false);
        if (wizardResult.Cancelled)
        {
            ctx.NavigationService.SetStatusMessage("Cancelled — site not configured");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        if (wizardResult.Config == null)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var treeBuilder = scope.ServiceProvider.GetService<INavigationTreeBuilder>();
        var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
        var links = ctx.PageCache.TryGetBuildCache(page.Url)?.Links ?? new List<LinkInfo>();
        if (treeBuilder != null)
        {
            var tree = await treeBuilder.BuildTreeAsync(links, wizardResult.Config, ct).ConfigureAwait(false);
            page.SetLinkTree(tree);
        }

        if (configStore != null)
        {
            await configStore.SaveConfigAsync(wizardResult.Config).ConfigureAwait(false);
        }

        ctx.NavigationService.SetStatusMessage($"✔ Site set up · AI Curated · {wizardResult.Config.Sections.Count} section(s)");
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a single named strategy directly (no probe) and saves + applies it.</summary>
    private static async Task RunStrategyAndApplyAsync(
        CommandContext ctx,
        IServiceScope scope,
        Domain.Entities.Browser.Page page,
        string strategyId,
        RenderOptions options,
        CancellationToken ct)
    {
        var registry = scope.ServiceProvider.GetService<IScrapingStrategyRegistry>();
        var strategy = registry?.GetById(strategyId);
        if (strategy == null)
        {
            ctx.NavigationService.SetStatusMessage("Strategy unavailable");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
        var links = ctx.PageCache.TryGetBuildCache(page.Url)?.Links ?? new List<LinkInfo>();
        var stContext = new ScrapingStrategyContext
        {
            PageUrl = page.Url,
            Html = page.RawHtml ?? string.Empty,
            Links = links,
        };

        var result = await strategy.BuildTreeAsync(stContext, ct).ConfigureAwait(false);
        if (configStore != null)
        {
            await configStore.SaveConfigAsync(result.Config).ConfigureAwait(false);
        }

        page.SetLinkTree(result.Tree);
        ctx.NavigationService.SetStatusMessage($"✔ {result.Summary}");
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>workspace-5oe9.9: ↑/↓/Enter loop over a single entry/summary card. Returns the chosen index or -1.</summary>
    private static async Task<int> RunEntryCardAsync(
        CommandContext ctx,
        UI.Components.SetupWizardOverlay.State overlay,
        UI.Components.SetupWizardOverlay.WizardCard card,
        RenderOptions options,
        CancellationToken ct)
    {
        overlay.Mode = UI.Components.SetupWizardOverlay.Mode.Card;
        overlay.Card = card;
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        var count = Math.Max(1, card.Options.Count);
        while (!ct.IsCancellationRequested)
        {
            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            switch (command.Type)
            {
                case CommandType.MoveDown or CommandType.ExpandNode:
                    card.Cursor = (card.Cursor + 1) % count;
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
                case CommandType.MoveUp or CommandType.CollapseNode:
                    card.Cursor = (card.Cursor - 1 + count) % count;
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
                case CommandType.ActivateLink:
                    return card.Cursor;
                case CommandType.GoBack or CommandType.Quit:
                    return -1;
                case CommandType.TerminalResized:
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
            }
        }

        return -1;
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
            return $"{summary} · Shift+I:set up";
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

    private static string ExtractDomain(string url) => HierarchyDomainKey.FromUrl(url);
}
