// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

internal static class RoutinePickModeHandler
{
    /// <summary>workspace-wyvga.9 stub (real mode: slice R6). True = routine changed.</summary>
    public static Task<bool> OpenAsync(CommandContext ctx, RenderOptions options, RoutinePickRequest request, CancellationToken ct)
    {
        ctx.NavigationService.SetStatusMessage(RoutineText.PickStub);
        return Task.FromResult(false);
    }
}
