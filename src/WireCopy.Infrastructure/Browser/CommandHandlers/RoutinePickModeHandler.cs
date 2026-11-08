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
/// routine colours; Space on a section header includes it, Space on a story toggles its position; Esc saves the picks into the routine.
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

        // Pick mode loads the site as its own page; on the way out the user returns to where they were
        // (the launcher, or the list they pressed g s on) - the Esc chain never strands them on the site.
        var startedInLauncher = ctx.NavigationService.InLauncherMode;
        var startPage = ctx.NavigationService.CurrentPage;

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

            // R7: positions are counted on today's page, so the visible story order must be today's too -
            // a cached copy can be stale after the site reorders ("#3" would sit on the wrong card).
            if (ctx.NavigationService.CurrentContext.IsFromCache)
            {
                await ctx.ForceRefreshAsync(request.SiteUrl, options, ct).ConfigureAwait(false);
                tree = ctx.NavigationService.CurrentPage?.LinkTree ?? tree;
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

            var domain = HierarchyDomainKey.FromUrl(request.SiteUrl);
            session.Start(recipe.Id, recipe.Name, request.SiteUrl, config, configIsNew, todays);
            session.FromSteps(recipe, domain, config.UrlPattern);

            // Legacy "top N" / "first story" steps show (and edit) as their positions #1..#N; steps the user
            // never touches are written back exactly as stored.
            var legacy = ConvertLegacyPicksToPositions(session);
            if (request.PreIncludeSection is { } pre)
            {
                session.Picks[pre] = new SectionPick
                {
                    Scope = string.Equals(pre, RecipeStep.WholePageSectionName, StringComparison.OrdinalIgnoreCase) && config.Sections.Count == 0
                        ? StepScope.WholePage
                        : StepScope.PinnedSection,
                    TakeMode = TakeMode.WholeSection,
                };

                // Start on the section g s added, so its "● all N" chip is the first thing in view.
                var preTree = ctx.NavigationService.CurrentPage?.LinkTree;
                var header = preTree?.SectionHeaders.FirstOrDefault(h =>
                    string.Equals(h.Link.DisplayText?.Trim(), pre, StringComparison.OrdinalIgnoreCase));
                if (header is not null)
                {
                    preTree!.SelectNodeById(header.Id);
                    ctx.AdjustScrollForSelection(preTree, options);
                }
            }

            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

            await LoopAsync(ctx, options, session, ct).ConfigureAwait(false);
            RestoreUntouchedLegacy(session, legacy);
            return await SaveAsync(ctx, scheduleStore, configStore, session, recipe).ConfigureAwait(false);
        }
        finally
        {
            session.Active = false;
            ctx.SetOverlayPainter(null);
            if (startPage is not null && !ReferenceEquals(ctx.NavigationService.CurrentPage, startPage) && ctx.NavigationService.CanGoBack)
            {
                ctx.NavigationService.GoBack();
            }

            if (startedInLauncher && !ctx.NavigationService.InLauncherMode)
            {
                ctx.NavigationService.EnterLauncher();

                // A launcher render is what parks the docked live page (DockSpotlight clears outside the
                // link list); without it the site's web pane stays over the routine editor drawn next.
                await ctx.RenderCurrentPageAsync(ctx.GetCurrentRenderOptions(), CancellationToken.None).ConfigureAwait(false);
            }

            if (turnedOn)
            {
                modeService.Set(UiMode.Normal);
            }
        }
    }

    private static async Task LoopAsync(
        CommandContext ctx, RenderOptions options, RoutinePickSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

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

    private static Dictionary<string, (SectionPick Original, SortedSet<int> Converted)> ConvertLegacyPicksToPositions(RoutinePickSession session)
    {
        var legacy = new Dictionary<string, (SectionPick, SortedSet<int>)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, pick) in session.Picks.ToList())
        {
            if (pick.TakeMode is not (TakeMode.TopN or TakeMode.SingleTopStory))
            {
                continue;
            }

            var count = pick.TakeMode == TakeMode.SingleTopStory ? 1 : Math.Max(1, pick.TakeCount ?? 1);
            var converted = new SectionPick { Scope = pick.Scope, TakeMode = TakeMode.Slots };
            for (var i = 1; i <= count; i++)
            {
                converted.Positions.Add(i);
            }

            legacy[name] = (pick, new SortedSet<int>(converted.Positions));
            session.Picks[name] = converted;
        }

        return legacy;
    }

    private static void RestoreUntouchedLegacy(RoutinePickSession session, Dictionary<string, (SectionPick Original, SortedSet<int> Converted)> legacy)
    {
        foreach (var (name, (original, converted)) in legacy)
        {
            if (session.Picks.TryGetValue(name, out var now) && now.TakeMode == TakeMode.Slots && now.Positions.SetEquals(converted))
            {
                session.Picks[name] = original;
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
        var sel0 = SelectedSection(ctx, session);
        if (sel0 is { OnHeader: false } story)
        {
            ToggleStory(ctx, session, story);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        if (sel0 is not { OnHeader: true } sel)
        {
            Status(ctx, RoutineText.HeaderOnlyYet);
        }
        else if (!session.Picks.Remove(sel.Name))
        {
            session.Picks[sel.Name] = new SectionPick { Scope = sel.Scope, TakeMode = TakeMode.WholeSection };
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>S2 Space on a story: toggles the story's POSITION in its section (never the article itself).</summary>
    private static void ToggleStory(CommandContext ctx, RoutinePickSession session, (string Name, StepScope Scope, bool OnHeader) sel)
    {
        var node = ctx.NavigationService.CurrentPage?.LinkTree?.CurrentSelection;
        var config = session.Config;
        if (node is null || config is null)
        {
            return;
        }

        var owner = NavigationTree.GetOwningSectionHeader(node);
        if (owner is null && config.Sections.Count > 0)
        {
            Status(ctx, RoutineText.NoSection);
            return;
        }

        if (session.Picks.TryGetValue(sel.Name, out var existing)
            && existing.TakeMode is TakeMode.WholeSection)
        {
            Status(ctx, RoutineText.WholeSectionAlready);
            return;
        }

        var wholePage = sel.Scope == StepScope.WholePage;
        var sortOrder = config.Sections
            .FirstOrDefault(s => string.Equals(s.Name, sel.Name, StringComparison.OrdinalIgnoreCase))?.SortOrder ?? 0;
        var step = RecipeStep.Create(
            session.SiteUrl,
            new Uri(session.SiteUrl).Host,
            config.UrlPattern,
            wholePage ? RecipeStep.WholePageSectionName : sel.Name,
            TakeMode.WholeSection,
            null,
            required: false,
            sortOrderFallback: sortOrder,
            scope: sel.Scope);
        using var scope = ctx.ScopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ISectionResolver>();
        var pos = resolver.PositionOf(config, session.TodaysLinks, step, node.Link.Url);
        if (pos is null)
        {
            Status(ctx, RoutineText.CannotPlace);
            return;
        }

        if (existing is null)
        {
            existing = new SectionPick { Scope = sel.Scope, TakeMode = TakeMode.Slots };
            session.Picks[sel.Name] = existing;
        }

        if (!existing.Positions.Remove(pos.Value))
        {
            existing.Positions.Add(pos.Value);
        }

        existing.TakeMode = TakeMode.Slots;
        if (existing.Positions.Count == 0)
        {
            session.Picks.Remove(sel.Name);
        }
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

    /// <summary>
    /// Waits for the site's real (non-skeleton) page. The main loop is not running while this handler owns the
    /// input loop, so the finished background load is applied here via ctx.PumpBackgroundLoadAsync. Esc cancels.
    /// </summary>
    private static async Task<NavigationTree?> WaitForLoadedAsync(CommandContext ctx, string siteUrl, RenderOptions options, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var lastRender = DateTime.MinValue;
        Task<NavigationCommand>? pending = null;
        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < LoadTimeout)
        {
            var stillLoading = await ctx.PumpBackgroundLoadAsync(options, ct).ConfigureAwait(false);
            var page = ctx.NavigationService.CurrentPage;
            if (page is { IsSkeleton: false } && page.LinkTree is { } tree && !stillLoading
                && Uri.TryCreate(page.Url, UriKind.Absolute, out var u)
                && Uri.TryCreate(siteUrl, UriKind.Absolute, out var want)
                && string.Equals(u.Host, want.Host, StringComparison.OrdinalIgnoreCase))
            {
                return tree;
            }

            if (DateTime.UtcNow - lastRender > TimeSpan.FromSeconds(1))
            {
                lastRender = DateTime.UtcNow;
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            }

            pending ??= ctx.InputHandler.WaitForInputAsync(ct);
            var done = await Task.WhenAny(pending, Task.Delay(150, ct)).ConfigureAwait(false);
            if (done == pending)
            {
                var command = await pending.ConfigureAwait(false);
                pending = null;
                if (command.Type is CommandType.GoBack or CommandType.Quit)
                {
                    return null;
                }
            }
        }

        return null;
    }
}
