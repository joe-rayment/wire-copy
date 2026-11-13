// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>A yes/no question in the footer row of a tile screen; the page stays visible.</summary>
internal static class TileConfirm
{
    public static async Task<bool> AskAsync(CommandContext ctx, TileScreen screen, string question, CancellationToken ct)
    {
        var trimmed = question.TrimEnd();
        if (trimmed.EndsWith("y / n", StringComparison.Ordinal))
        {
            question = trimmed[..^"y / n".Length].TrimEnd();
        }

        var asked = screen with
        {
            Confirm = new PromptLineState { Prefix = question + "  [y] yes · [n] no ", Buffer = string.Empty, Cursor = 0 },
        };
        ctx.Renderer.RenderTileScreen(asked, ctx.GetCurrentRenderOptions());
        var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
        return command.RawKeyChar is 'y' or 'Y';
    }
}
