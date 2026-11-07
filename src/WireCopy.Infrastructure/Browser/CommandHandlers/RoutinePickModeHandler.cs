// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Scheduling;
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
/// workspace-wyvga.6 (S1.4, S2): pick mode for SECTIONS. The normal link tree for one site in
/// routine colours; Space/T on a section header include it; Esc saves the picks into the routine.
/// LinkTreeRenderer draws the chips while <see cref="RoutinePickSession.Active"/>.
/// </summary>
internal static class RoutinePickModeHandler
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(45);

    /// <summary>True = the routine's steps changed.</summary>
    public static async Task<bool> OpenAsync(CommandContext ctx, RenderOptions options, RoutinePickRequest request, CancellationToken ct)
    {
        using var scope = ctx.ScopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var scheduleStore = sp.GetRequiredService<IScheduleStore>();
        var configStore = sp.GetRequiredService<IHierarchyConfigStore>();
        var modeService = sp.GetRequiredService<IUiModeService>();
        var session = sp.GetRequiredService<RoutinePickSession>();

        var recipe = await scheduleStore.GetAsync(request.RoutineId).ConfigureAwait(false);
        if (recipe is null)
        {
            return false;
        }

        // S1: whoever turns routine colours on turns them off.
        var turnedOn = modeService.Current == UiMode.Normal;
        if (turnedOn)
        {
            modeService.Set(UiMode.Routine);
        }

        try
        {
            ctx.NavigationService.SetStatusMessage("Loading " + request.SiteUrl + " …", TimeSpan.FromSeconds(30));
            await ctx.NavigateToAsync(request.SiteUrl, options, ct).ConfigureAwait(false);
            var tree = await WaitForLoadedAsync(ctx, request.SiteUrl, options, ct).ConfigureAwait(false);
            if (tree is null)
            {
                ctx.NavigationService.SetStatusMessage("Couldn't load " + request.SiteUrl, TimeSpan.FromSeconds(6), StatusSeverity.Error);
                return false;
            }

            var page = ctx.NavigationService.CurrentPage!;
            var lookup = await ScheduleConfigResolution.ForUrlAsync(configStore, page.Url).ConfigureAwait(false);
            var config = lookup.Config;
            var configIsNew = config is null;
            if (config is null)
            {
                var derived = AutoGroupedSectionDerivation.FromTree(tree);
                config = new SiteHierarchyConfig
                {
                    Domain = HierarchyDomainKey.FromUrl(page.Url),
                    UrlPattern = ScrapingStrategies.DocumentOrderStrategy.BuildUrlPattern(page.Url),
                    Sections = derived,
                    CreatedAt = DateTime.UtcNow,
                    ModelVersion = derived.Count > 0 ? "auto-grouped" : "document-order",
                    Kind = LayoutKind.DocumentOrder,
                    Version = 2,
                    Strategy = ScrapingStrategies.DocumentOrderStrategy.StrategyId,
                    StructuralSignature = $"doc-order:{tree.TotalLinks}",
                };
            }

            if (config.Sections.Count == 0 && tree.SectionHeaders.Count == 0)
            {
                // A flat page has no header to Space on: show one in-memory "All stories" group
                // (view only - never persisted; the pick saves as the WholePage step).
                page.SetLinkTree(WithAllStoriesHeader(tree));
            }

            IReadOnlyList<LinkInfo> todays = Array.Empty<LinkInfo>();
            var loader = sp.GetService<IUnattendedSectionLoader>();
            if (loader is not null)
            {
                try
                {
                    todays = (await loader.LoadLinksAndConfigAsync(request.SiteUrl, ct).ConfigureAwait(false)).Links;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Positions (R7) degrade gracefully; section picking does not need them.
                }
            }

            var domain = new Uri(request.SiteUrl).Host;
            session.Start(recipe.Id, recipe.Name, request.SiteUrl, config, configIsNew, todays);
            session.FromSteps(recipe, domain, config.UrlPattern);
            if (request.PreIncludeSection is { } pre)
            {
                session.Picks[pre] = new SectionPick
                {
                    Scope = string.Equals(pre, RecipeStep.WholePageSectionName, StringComparison.OrdinalIgnoreCase) && config.Sections.Count == 0
                        ? StepScope.WholePage
                        : StepScope.PinnedSection,
                    TakeMode = TakeMode.WholeSection,
                };
            }

            var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
            var overlay = new SetupWizardOverlay.State();
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            await LoopAsync(ctx, options, session, overlay, palette, ct).ConfigureAwait(false);
            return await SaveAsync(ctx, scheduleStore, configStore, session, recipe).ConfigureAwait(false);
        }
        finally
        {
            session.Active = false;
            ctx.SetOverlayPainter(null);
            if (turnedOn)
            {
                modeService.Set(UiMode.Normal);
            }
        }
    }

    private static async Task LoopAsync(
        CommandContext ctx, RenderOptions options, RoutinePickSession session, SetupWizardOverlay.State overlay, ThemePalette palette, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

            // S2 key reading rule: Shift+T arrives as SwitchToHierarchical carrying RawKeyChar 'T'.
            if (command.RawKeyChar == 'T')
            {
                await TakePickerAsync(ctx, options, session, overlay, palette, ct).ConfigureAwait(false);
                continue;
            }

            switch (command.Type)
            {
                case CommandType.GoBack or CommandType.Quit:
                    return;
                case CommandType.TerminalResized:
                    options = ctx.GetCurrentRenderOptions();
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    break;
                case CommandType.ToggleSelection:
                    await ToggleAsync(ctx, options, session, ct).ConfigureAwait(false);
                    break;
                case CommandType.ActivateLink:
                    if (SelectedSection(ctx, session) is { OnHeader: true })
                    {
                        await NavigationCommandHandler.HandleActivateLink(ctx, options, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        Status(ctx, RoutineText.StoryHint);
                        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    }

                    break;
                case CommandType.MoveDown:
                    await NavigationCommandHandler.HandleMoveDown(ctx, command, options, ct).ConfigureAwait(false);
                    break;
                case CommandType.MoveUp:
                    await NavigationCommandHandler.HandleMoveUp(ctx, command, options, ct).ConfigureAwait(false);
                    break;
                case CommandType.ExpandNode:
                    await NavigationCommandHandler.HandleExpandNode(ctx, options, ct).ConfigureAwait(false);
                    break;
                case CommandType.CollapseNode:
                    await NavigationCommandHandler.HandleCollapseNode(ctx, options, ct).ConfigureAwait(false);
                    break;
                case CommandType.ToggleNode:
                    await NavigationCommandHandler.HandleToggleNode(ctx, options, ct).ConfigureAwait(false);
                    break;
                case CommandType.PageDown:
                    await NavigationCommandHandler.HandlePageDown(ctx, options, ct).ConfigureAwait(false);
                    break;
                case CommandType.PageUp:
                    await NavigationCommandHandler.HandlePageUp(ctx, options, ct).ConfigureAwait(false);
                    break;
                case CommandType.ParagraphDown:
                    await NavigationCommandHandler.HandleParagraphDown(ctx, options, ct).ConfigureAwait(false);
                    break;
                case CommandType.ParagraphUp:
                    await NavigationCommandHandler.HandleParagraphUp(ctx, options, ct).ConfigureAwait(false);
                    break;
                case CommandType.GoToTop:
                    await NavigationCommandHandler.HandleGoToTop(ctx, command, options, ct).ConfigureAwait(false);
                    break;
                case CommandType.GoToBottom:
                    await NavigationCommandHandler.HandleGoToBottom(ctx, command, options, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>The section the cursor is in (name + scope) and whether the cursor is on its header.</summary>
    private static (string Name, StepScope Scope, bool OnHeader)? SelectedSection(CommandContext ctx, RoutinePickSession session)
    {
        var node = ctx.NavigationService.CurrentPage?.LinkTree?.CurrentSelection;
        if (node is null)
        {
            return null;
        }

        var header = NavigationTree.GetOwningSectionHeader(node);
        if (header is null)
        {
            return (RecipeStep.WholePageSectionName, StepScope.WholePage, false);
        }

        // On a flat (zero-section) config the only header is the synthetic "All stories" one,
        // which stands for the whole page (S4: SectionName "All stories", Scope WholePage).
        var flat = session.Config is null || session.Config.Sections.Count == 0;
        return (header.Link.DisplayText.Trim(), flat ? StepScope.WholePage : StepScope.PinnedSection, ReferenceEquals(header, node));
    }

    private static async Task ToggleAsync(CommandContext ctx, RenderOptions options, RoutinePickSession session, CancellationToken ct)
    {
        if (SelectedSection(ctx, session) is not { OnHeader: true } sel)
        {
            Status(ctx, RoutineText.HeaderOnlyYet);
        }
        else if (!session.Picks.Remove(sel.Name))
        {
            session.Picks[sel.Name] = new SectionPick { Scope = sel.Scope, TakeMode = TakeMode.WholeSection };
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static async Task TakePickerAsync(
        CommandContext ctx, RenderOptions options, RoutinePickSession session, SetupWizardOverlay.State overlay, ThemePalette palette, CancellationToken ct)
    {
        if (SelectedSection(ctx, session) is not { OnHeader: true } sel)
        {
            Status(ctx, RoutineText.HeaderOnlyYet);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var labels = new[] { "All", "Top 3", "Top 5", "Top N…", "First story" };
        var index = await CardAsync(ctx, options, overlay, palette, "Take from " + sel.Name, "How much of this section? Enter select · Esc back", labels, ct).ConfigureAwait(false);
        if (index >= 0)
        {
            var pick = new SectionPick { Scope = sel.Scope };
            switch (index)
            {
                case 0:
                    pick.TakeMode = TakeMode.WholeSection;
                    break;
                case 1 or 2:
                    pick.TakeMode = TakeMode.TopN;
                    pick.TakeCount = index == 1 ? 3 : 5;
                    break;
                case 3:
                    var field = new FormFieldConfig
                    {
                        Label = "How many stories (N)?",
                        InitialValue = "3",
                        Validate = v => int.TryParse(v, out var n) && n >= 1 ? null : "Enter a whole number ≥ 1",
                    };
                    var startRow = Math.Max(1, (options.TerminalHeight / 2) - 3);
                    var fieldWidth = Math.Min(Math.Max(20, UI.OverlayViewport.Width - 6), 60);
                    ctx.SetOverlayPainter(null);
                    var text = await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false);
                    if (text is null || !int.TryParse(text, out var count) || count < 1)
                    {
                        index = -1;
                        break;
                    }

                    pick.TakeMode = TakeMode.TopN;
                    pick.TakeCount = count;
                    break;
                default:
                    pick.TakeMode = TakeMode.SingleTopStory;
                    pick.TakeCount = 1;
                    break;
            }

            if (index >= 0)
            {
                session.Picks[sel.Name] = pick;
            }
        }

        ctx.SetOverlayPainter(null);
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    private static async Task<int> CardAsync(
        CommandContext ctx, RenderOptions options, SetupWizardOverlay.State overlay, ThemePalette palette, string title, string prompt, string[] labels, CancellationToken ct)
    {
        var card = new SetupWizardOverlay.WizardCard { Title = title, Prompt = prompt, Hint = "↑/↓ move · Enter select · Esc back", Cursor = 0 };
        foreach (var l in labels)
        {
            card.Options.Add(new SetupWizardOverlay.CardOption { Label = l });
        }

        ctx.SetOverlayPainter(opts => SetupWizardOverlay.Render(overlay, palette, opts.TerminalWidth, opts.TerminalHeight));
        while (!ct.IsCancellationRequested)
        {
            overlay.Mode = SetupWizardOverlay.Mode.Card;
            overlay.Card = card;
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            switch (command.Type)
            {
                case CommandType.GoBack or CommandType.Quit:
                    return -1;
                case CommandType.MoveDown or CommandType.ExpandNode:
                    card.Cursor = (card.Cursor + 1) % labels.Length;
                    break;
                case CommandType.MoveUp or CommandType.CollapseNode:
                    card.Cursor = (card.Cursor - 1 + labels.Length) % labels.Length;
                    break;
                case CommandType.ActivateLink:
                    return card.Cursor;
            }
        }

        return -1;
    }

    private static async Task<bool> SaveAsync(
        CommandContext ctx, IScheduleStore store, IHierarchyConfigStore configStore, RoutinePickSession session, ScheduleRecipe recipe)
    {
        var steps = session.MergeInto(recipe);
        var changed = !steps.SequenceEqual(recipe.Steps);
        if (session.ConfigIsNew && session.Picks.Count > 0 && session.Config is not null
            && !await configStore.SaveConfigAsync(session.Config).ConfigureAwait(false))
        {
            Status(ctx, "Could not save this page's layout (disk?) — picks were NOT saved", StatusSeverity.Error);
            return false;
        }

        if (changed)
        {
            var updated = ScheduleRecipe.Rehydrate(
                recipe.Id, recipe.Name, recipe.Enabled, recipe.Cadence, steps, recipe.OutputCollectionName, recipe.RunState, recipe.Version);
            await store.SaveAsync(updated).ConfigureAwait(false);
        }

        return changed;
    }

    /// <summary>Copy of a flat tree with every story under one "All stories" sub-section header; chrome groups (More, External) stay at the root.</summary>
    private static NavigationTree WithAllStoriesHeader(NavigationTree flat)
    {
        var root = LinkNode.CreateRoot();
        var all = root.AddChild(LinkInfo.CreateSubSectionHeader(RecipeStep.WholePageSectionName, LinkType.Content));
        var groups = new List<LinkNode>();
        foreach (var child in flat.Root.Children)
        {
            if (child.IsGroupHeader)
            {
                groups.Add(child);
            }
            else
            {
                all.AddChild(child.Link);
            }
        }

        foreach (var g in groups)
        {
            var copy = root.AddChild(g.Link);
            foreach (var c in g.Children)
            {
                copy.AddChild(c.Link);
            }
        }

        return NavigationTree.BuildFromRoot(root);
    }

    private static void Status(CommandContext ctx, string text, StatusSeverity severity = StatusSeverity.Info) =>
        ctx.NavigationService.SetStatusMessage(text, TimeSpan.FromSeconds(4), severity);

    /// <summary>Polls until the current page is a loaded (non-skeleton) page of the site's host; renders meanwhile.</summary>
    private static async Task<NavigationTree?> WaitForLoadedAsync(CommandContext ctx, string siteUrl, RenderOptions options, CancellationToken ct)
    {
        var host = new Uri(siteUrl).Host;
        var started = DateTime.UtcNow;
        var lastRender = DateTime.MinValue;
        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < LoadTimeout)
        {
            var page = ctx.NavigationService.CurrentPage;
            if (page is { IsSkeleton: false } && page.LinkTree is { } tree
                && Uri.TryCreate(page.Url, UriKind.Absolute, out var u)
                && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return tree;
            }

            if (DateTime.UtcNow - lastRender > TimeSpan.FromSeconds(1))
            {
                lastRender = DateTime.UtcNow;
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        return null;
    }
}
