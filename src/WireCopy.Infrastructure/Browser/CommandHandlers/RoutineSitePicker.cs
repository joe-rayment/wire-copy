// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// "+ Add a site" in the routine editor: its own tile screen with one tile per saved site and the
/// bar for typing an address. Enter on a site tile (or a submitted address) returns that URL, Esc returns null.
/// </summary>
internal static class RoutineSitePicker
{
    public static async Task<string?> PickAsync(
        CommandContext ctx, string routineName, CancellationToken ct, ScheduleRecipe? recipe = null)
    {
        var marks = ctx.Bookmarks ?? Array.Empty<Domain.Entities.Bookmarks.Bookmark>();
        var sel = marks.Count == 0 ? TileGrid.Bar : 0;
        string? error = null;
        while (!ct.IsCancellationRequested)
        {
            var screen = Build(routineName, marks, recipe, error, null) with { Selected = sel };
            error = null;
            ctx.Renderer.RenderTileScreen(screen, ctx.GetCurrentRenderOptions());

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            if (command.Type == CommandType.TerminalResized)
            {
                continue;
            }

            if (command.Type == CommandType.ShowHelp)
            {
                await TileHelp.ShowAsync(
                    ctx,
                    $"Routines › {routineName} › Add a site",
                    [
                        ("ENTER", "Pick the lit site"),
                        ("1-9", "Jump to a site"),
                        ("O", "Type an address"),
                        ("ESC", "Back"),
                    ],
                    ct).ConfigureAwait(false);
                continue;
            }

            var onTile = sel != TileGrid.Bar;
            var seed = string.Empty;
            var typing = command.Type is CommandType.GoToUrl or CommandType.OpenInBrowser
                || (!onTile && RoutineEditorHandler.IsBarTypingKey(command, out seed));
            if (typing)
            {
                if (command.Type is CommandType.GoToUrl or CommandType.OpenInBrowser)
                {
                    seed = string.Empty;
                }

                var input = await RoutineEditorHandler.ReadInBarAsync(
                    ctx,
                    ctx.GetCurrentRenderOptions(),
                    string.Empty,
                    seed,
                    null,
                    (t, hint) => Build(routineName, marks, recipe, hint, t) with { Selected = TileGrid.Bar },
                    ct).ConfigureAwait(false);
                sel = TileGrid.Bar;
                if (string.IsNullOrWhiteSpace(input))
                {
                    continue;
                }

                if (NavigationUrl.TryNormalize(input, out var url, out var urlError))
                {
                    return url;
                }

                error = urlError;
                continue;
            }

            switch (command.Type)
            {
                case CommandType.GoBack:
                case CommandType.Quit:
                    return null;
                case CommandType.JumpToIndex:
                    sel = TileGrid.Jump(screen, command.Count);
                    break;
                case CommandType.ActivateLink:
                    if (TileGrid.ItemAt(screen, sel)?.Id is { } id
                        && id.StartsWith("bm:", StringComparison.Ordinal)
                        && int.TryParse(id["bm:".Length..], out var idx) && idx >= 0 && idx < marks.Count)
                    {
                        return marks[idx].Url;
                    }

                    break;
                default:
                    sel = TileGrid.Move(screen, command.Type);
                    break;
            }
        }

        return null;
    }

    private static TileScreen Build(
        string routineName,
        IReadOnlyList<Domain.Entities.Bookmarks.Bookmark> marks,
        ScheduleRecipe? recipe,
        string? hintRow,
        LauncherUrlTyping? typing)
    {
        var count = marks.Count switch
        {
            0 => "no saved sites",
            1 => "1 site",
            _ => $"{marks.Count} sites",
        };
        var cells = marks.Select((m, i) =>
        {
            var host = HierarchyDomainKey.FromUrl(m.Url);
            var sections = recipe?.Steps.Count(s => string.Equals(s.Domain, host, StringComparison.OrdinalIgnoreCase)) ?? 0;
            string? detail = null;
            if (sections > 0)
            {
                detail = $"already in this routine · {sections} section" + (sections == 1 ? string.Empty : "s");
            }

            return new TileCell
            {
                Tile = new Tile
                {
                    Id = $"bm:{i}",
                    Title = m.Name.ToUpperInvariant(),
                    Hint = host,
                    Detail = detail,
                },
            };
        }).ToList();
        return new TileScreen
        {
            Screen = "addSite",
            Eyebrow = $"Routines › {routineName} › Add a site",
            Title = "Add a site",
            Tagline = "Pick one of your sites, or type an address",
            CountLabel = count,
            FooterCount = count,
            BarPlaceholder = "Go to URL…",
            BarTyping = typing,
            HintRow = marks.Count == 0 ? hintRow ?? "No saved sites yet — type a site's address above" : hintRow,
            AppLabel = "Add a site",
            Columns = 2,
            Cells = cells,
            Selected = marks.Count == 0 ? TileGrid.Bar : 0,
            Hints =
            [
                new LauncherHint { Key = "Enter", Label = "pick this site" },
                new LauncherHint { Key = "1-9", Label = "jump" },
                new LauncherHint { Key = "o", Label = "type an address" },
                new LauncherHint { Key = "Esc", Label = "back" },
            ],
        };
    }
}
