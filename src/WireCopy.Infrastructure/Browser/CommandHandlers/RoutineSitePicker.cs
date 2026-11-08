// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// "+ Add a site" in the routine editor: the app's own launcher (same tile grid, movement keys,
/// digits and Go to URL) in routine colours. It keeps its own selection so the real launcher's
/// is untouched; Enter on a site tile (or a submitted URL) returns that URL, Esc returns null.
/// </summary>
internal static class RoutineSitePicker
{
    public static async Task<string?> PickAsync(CommandContext ctx, string routineName, CancellationToken ct)
    {
        var nav = ctx.NavigationService;
        var savedSel = nav.LauncherSelectedIndex;
        var savedScroll = nav.LauncherScrollOffset;
        var sel = 0;
        var scroll = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var opts = Options(ctx, routineName);
                var bc = ctx.Bookmarks?.Count ?? 0;
                Paint(ctx, opts, sel, scroll);

                var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
                if (command.Type == CommandType.TerminalResized)
                {
                    continue;
                }

                if (sel == -1 && command.RawKeyChar is { } typed && typed >= 32
                    && command.Type is not (CommandType.MoveDown or CommandType.MoveUp or CommandType.ActivateLink))
                {
                    var typedUrl = await TypeUrlAsync(ctx, routineName, typed, ct).ConfigureAwait(false);
                    if (typedUrl is not null)
                    {
                        return typedUrl;
                    }

                    continue;
                }

                switch (command.Type)
                {
                    case CommandType.GoBack:
                    case CommandType.Quit:
                        return null;
                    case CommandType.GoToUrl:
                    case CommandType.OpenInBrowser:
                        sel = -1;
                        var url = await TypeUrlAsync(ctx, routineName, null, ct).ConfigureAwait(false);
                        if (url is not null)
                        {
                            return url;
                        }

                        break;
                    case CommandType.ActivateLink:
                        if (sel == -1)
                        {
                            var entered = await TypeUrlAsync(ctx, routineName, null, ct).ConfigureAwait(false);
                            if (entered is not null)
                            {
                                return entered;
                            }
                        }
                        else
                        {
                            var idx = LauncherCommandHandler.BookmarkIndexFromVirtual(sel, bc);
                            if (idx >= 0 && ctx.Bookmarks is { } marks && idx < marks.Count)
                            {
                                return marks[idx].Url;
                            }

                            nav.SetStatusMessage(RoutineText.AddSiteNotASite, TimeSpan.FromSeconds(5));
                        }

                        break;
                    default:
                        sel = LauncherCommandHandler.MoveSelection(command.Type, command.Count, bc, sel, LauncherCommandHandler.LayoutColumns(opts));
                        scroll = LauncherCommandHandler.NextScrollOffset(bc, sel, scroll, opts);
                        break;
                }
            }

            return null;
        }
        finally
        {
            nav.LauncherSelectedIndex = savedSel;
            nav.LauncherScrollOffset = savedScroll;
        }
    }

    private static async Task<string?> TypeUrlAsync(CommandContext ctx, string routineName, char? seed, CancellationToken ct)
    {
        var opts = Options(ctx, routineName);
        return await LauncherCommandHandler.PromptUrlAsync(
            ctx,
            opts,
            ct,
            () =>
            {
                Paint(ctx, Options(ctx, routineName), -1, 0);
                return Task.CompletedTask;
            },
            seed).ConfigureAwait(false);
    }

    private static RenderOptions Options(CommandContext ctx, string routineName) =>
        ctx.GetCurrentRenderOptions() with
        {
            LauncherEyebrow = RoutineText.Eyebrow(routineName),
            LauncherFooterHint = RoutineText.AddSiteFooter,
        };

    private static void Paint(CommandContext ctx, RenderOptions opts, int sel, int scroll) =>
        ctx.Renderer.RenderLauncher(
            ctx.Bookmarks?.ToList() ?? new List<Domain.Entities.Bookmarks.Bookmark>(), sel, scroll, opts);
}
