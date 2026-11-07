// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

internal static class RoutineEditorHandler
{
    /// <summary>workspace-wyvga.9 stub (real screen: slice R5).</summary>
    public static Task OpenAsync(CommandContext ctx, RenderOptions options, Guid routineId, CancellationToken ct)
    {
        ctx.NavigationService.SetStatusMessage(RoutineText.EditorStub);
        return Task.CompletedTask;
    }
}
