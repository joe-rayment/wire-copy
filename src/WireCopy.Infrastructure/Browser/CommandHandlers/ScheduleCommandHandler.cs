// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-wyvga.8 — 'g s' / 's on a section header': "Add to routine". A card lists the
/// routines by name plus "+ New routine"; choosing one hands the current page to routine pick
/// mode (<see cref="RoutinePickModeHandler"/>), then returns to the same list position in normal colours.
/// </summary>
internal static class ScheduleCommandHandler
{
    private const string CardTitle = "Add to routine";

    public static async Task HandleAddToScheduleAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var page = ctx.NavigationService.CurrentPage;
        var tree = page?.LinkTree;
        if (ctx.NavigationService.CurrentContext.ViewMode != ViewMode.Hierarchical || page == null || tree == null || tree.TotalLinks == 0)
        {
            ctx.NavigationService.SetStatusMessage(
                "Open a site's link list first — g s adds a section of the page you're on to a routine.",
                TimeSpan.FromSeconds(6));
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var pageUrl = page.Url;
        var selected = tree.CurrentSelection;
        var savedId = selected?.Id;
        var savedText = selected?.Link.DisplayText;
        var savedUrl = selected?.Link.Url;
        var savedIndex = selected == null ? 0 : Math.Max(0, tree.GetVisibleNodes().ToList().IndexOf(selected));
        var sectionName = NavigationTree.GetOwningSectionHeader(selected)?.Link.DisplayText?.Trim();
        if (string.IsNullOrEmpty(sectionName))
        {
            sectionName = null;
        }

        using var scope = ctx.ScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IScheduleStore>();
        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

        var overlay = new SetupWizardOverlay.State();
        ctx.SetOverlayPainter(opts => SetupWizardOverlay.Render(overlay, palette, opts.TerminalWidth, opts.TerminalHeight));
        Guid? routineId = null;
        var routineName = string.Empty;
        try
        {
            var recipes = (await store.GetAllAsync().ConfigureAwait(false))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var labels = recipes.Select(r => r.Name).ToList();
            labels.Add("+ New routine");
            var pick = await PickAsync(ctx, overlay, options, labels, ct).ConfigureAwait(false);
            if (pick >= 0 && pick < recipes.Count)
            {
                routineId = recipes[pick].Id;
                routineName = recipes[pick].Name;
            }
            else if (pick == recipes.Count)
            {
                ctx.SetOverlayPainter(null);
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                var created = await CreateAsync(ctx, options, store, recipes, palette, ct).ConfigureAwait(false);
                if (created != null)
                {
                    routineId = created.Id;
                    routineName = created.Name;
                }
            }
        }
        finally
        {
            ctx.SetOverlayPainter(null);
        }

        if (routineId == null)
        {
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var session = scope.ServiceProvider.GetRequiredService<RoutinePickSession>();
        var changed = await RoutinePickModeHandler.OpenAsync(
            ctx, options, new RoutinePickRequest(routineId.Value, pageUrl, sectionName), ct).ConfigureAwait(false);

        RestoreSelection(ctx, savedId, savedText, savedUrl, savedIndex);
        if (changed)
        {
            ctx.NavigationService.SetStatusMessage(
                RoutineText.AddedToRoutine(routineName, session.Picks.Count), TimeSpan.FromSeconds(6));
        }

        await ctx.RenderCurrentPageAsync(ctx.GetCurrentRenderOptions(), ct).ConfigureAwait(false);
    }

    private static void RestoreSelection(CommandContext ctx, Guid? id, string? text, string? url, int index)
    {
        var tree = ctx.NavigationService.CurrentPage?.LinkTree;
        if (tree == null || id == null || tree.CurrentSelection?.Id == id || tree.SelectNodeById(id.Value))
        {
            return;
        }

        var nodes = tree.GetVisibleNodes().ToList();
        var match = nodes.FirstOrDefault(n => n.Link.DisplayText == text && n.Link.Url == url);
        if (match != null)
        {
            tree.SelectNodeById(match.Id);
        }
        else if (nodes.Count > 0)
        {
            tree.SelectNodeById(nodes[Math.Min(index, nodes.Count - 1)].Id);
        }
    }

    private static async Task<ScheduleRecipe?> CreateAsync(
        CommandContext ctx, RenderOptions options, IScheduleStore store, List<ScheduleRecipe> recipes, ThemePalette palette, CancellationToken ct)
    {
        var field = new FormFieldConfig
        {
            Label = "New routine",
            Subtitle = "Name your routine · Esc cancels",
            Placeholder = "e.g. Morning briefing",
        };
        var fieldWidth = Math.Min(80, Math.Max(30, options.TerminalWidth - 6));
        var startRow = Math.Max(0, options.TerminalHeight - FormField.HeightFor(field) - 1);
        var name = (await FormField.PromptAsync(ctx.InputHandler, field, palette, startRow, fieldWidth, ct).ConfigureAwait(false))?.Trim();
        if (name == null)
        {
            return null;
        }

        if (name.Length == 0)
        {
            ctx.NavigationService.SetStatusMessage(RoutineText.NameEmpty, TimeSpan.FromSeconds(6));
            return null;
        }

        if (recipes.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ctx.NavigationService.SetStatusMessage(RoutineText.NameTaken, TimeSpan.FromSeconds(6));
            return null;
        }

        var recipe = ScheduleRecipe.Create(name, Cadence.Daily(new TimeOnly(5, 0)), Array.Empty<RecipeStep>(), name, true);
        await store.SaveAsync(recipe).ConfigureAwait(false);
        return recipe;
    }

    /// <summary>Index of the chosen row, or -1 when cancelled.</summary>
    private static async Task<int> PickAsync(
        CommandContext ctx, SetupWizardOverlay.State overlay, RenderOptions options, List<string> labels, CancellationToken ct)
    {
        var card = new SetupWizardOverlay.WizardCard
        {
            Title = CardTitle,
            Prompt = "Which routine?",
            Hint = "↑/↓ move · Enter select · Esc cancel",
            Cursor = 0,
        };
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
            switch (command.Type)
            {
                case CommandType.GoBack or CommandType.Quit:
                    return -1;
                case CommandType.MoveDown or CommandType.ExpandNode:
                    card.Cursor = (card.Cursor + 1) % count;
                    break;
                case CommandType.MoveUp or CommandType.CollapseNode:
                    card.Cursor = (card.Cursor - 1 + count) % count;
                    break;
                case CommandType.TerminalResized:
                    options = ctx.GetCurrentRenderOptions();
                    break;
                case CommandType.ActivateLink:
                    return card.Cursor;
            }
        }

        return -1;
    }
}
