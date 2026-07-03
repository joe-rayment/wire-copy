// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles the layout chooser flow: generates layout candidates,
/// enters preview mode, and manages the cycling/save/cancel lifecycle.
/// </summary>
internal static class LayoutCommandHandler
{
    /// <summary>
    /// Cycles to the next layout preview (Right arrow in preview mode).
    /// </summary>
    public static async Task HandleCycleRight(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        ctx.NavigationService.CyclePreview(1);
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cycles to the previous layout preview (Left arrow in preview mode).
    /// </summary>
    public static async Task HandleCycleLeft(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        ctx.NavigationService.CyclePreview(-1);
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the current preview layout and saves it (Enter in preview mode).
    /// </summary>
    public static async Task HandleApplyAndSave(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var selected = ctx.NavigationService.ApplyPreview();
        if (selected == null)
        {
            return;
        }

        // Save the selected layout config
        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
            if (configStore != null)
            {
                await configStore.SaveConfigAsync(selected.Config).ConfigureAwait(false);
                ctx.NavigationService.SetStatusMessage($"\u2714 Layout saved \u00b7 {selected.Summary}");
            }
            else
            {
                ctx.NavigationService.SetStatusMessage($"\u2714 Applied \u00b7 {selected.Summary}");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to save layout config");
            ctx.NavigationService.SetStatusMessage($"Applied · {selected.Summary} (save failed)", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the saved layout config for the current URL.
    /// </summary>
    public static async Task HandleClearLayout(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var page = ctx.NavigationService.CurrentContext.CurrentPage;
        if (page == null)
        {
            return;
        }

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var configStore = scope.ServiceProvider.GetService<IHierarchyConfigStore>();
            if (configStore != null)
            {
                var deleted = await configStore.DeleteConfigAsync(page.Url).ConfigureAwait(false);
                ctx.NavigationService.SetStatusMessage(
                    deleted ? "Layout config cleared" : "No saved layout for this page");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to clear layout config");
            ctx.NavigationService.SetStatusMessage("Failed to clear layout config", StatusSeverity.Error);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels preview mode and restores the original layout (Esc in preview mode).
    /// </summary>
    public static async Task HandleCancel(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        // workspace-ujxu: clear the anchored chooser overlay on cancel —
        // it stays installed across the entire preview phase and must be
        // dropped before the link list re-renders without the modal.
        StrategyChooserHandler.ClearOverlay(ctx);

        ctx.NavigationService.CancelPreview();
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }
}
