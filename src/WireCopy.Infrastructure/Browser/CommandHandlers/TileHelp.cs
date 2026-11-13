// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// "?" on a routines screen: that screen's keys as tiles, drawn in the pane (workspace-6ifpp.7).
/// Movement keys page through them; any other key returns and the caller repaints its own screen.
/// </summary>
internal static class TileHelp
{
    internal static async Task ShowAsync(
        CommandContext ctx, string eyebrow, IReadOnlyList<(string Key, string What)> keys, CancellationToken ct)
    {
        var cells = keys
            .Select((k, i) => new TileCell { Tile = new Tile { Id = $"key:{i}", Title = k.Key, Hint = k.What } })
            .ToList();
        var renderOptions = ctx.GetCurrentRenderOptions();
        var wide = renderOptions.ShellPane || renderOptions.TerminalWidth >= 100;
        var selected = 0;
        while (!ct.IsCancellationRequested)
        {
            var screen = new TileScreen
            {
                Screen = "help",
                Eyebrow = $"{eyebrow} › Keys",
                Title = "Keys",
                Tagline = "What works on this screen",
                BarPlaceholder = null,
                Cells = cells,
                Columns = wide ? 3 : 2,
                RowsPerPage = 3,
                Selected = selected,
                AppLabel = "Keys",
                Hints = new List<LauncherHint> { new() { Key = "Esc", Label = "back" } },
            };
            ctx.Renderer.RenderTileScreen(screen, ctx.GetCurrentRenderOptions());

            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            if (command.Type is CommandType.MoveUp or CommandType.MoveDown or CommandType.MoveLeft
                or CommandType.MoveRight or CommandType.ExpandNode or CommandType.CollapseNode)
            {
                selected = TileGrid.Move(screen, command.Type);
            }
            else if (command.Type != CommandType.TerminalResized)
            {
                return;
            }
        }
    }
}
