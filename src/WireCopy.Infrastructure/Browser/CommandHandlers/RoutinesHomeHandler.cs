// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

internal static class RoutinesHomeHandler
{
    /// <summary>workspace-wyvga.9 stub (real screen: slice R4).</summary>
    public static Task OpenAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        ctx.NavigationService.SetStatusMessage(RoutineText.HomeStub);
        return Task.CompletedTask;
    }
}
