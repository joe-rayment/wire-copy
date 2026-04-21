// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles the layout chooser flow: generates layout candidates,
/// enters preview mode, and manages the cycling/save/cancel lifecycle.
/// </summary>
internal static class LayoutCommandHandler
{
    /// <summary>
    /// Opens the layout chooser. Generates candidates (document-order instantly,
    /// AI and RSS in parallel), enters preview mode, and renders.
    /// </summary>
    public static async Task HandleChooseLayout(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var navContext = ctx.NavigationService.CurrentContext;

        // Only available in Hierarchical view with a loaded page
        if (navContext.ViewMode != ViewMode.Hierarchical || navContext.CurrentPage == null)
        {
            return;
        }

        // If already in preview mode, treat as cancel
        if (ctx.NavigationService.IsInPreviewMode)
        {
            ctx.NavigationService.CancelPreview();
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        ctx.NavigationService.SetStatusMessage("Generating layouts...");
        await ctx.RenderCurrentPageAsync(options, ct);

        try
        {
            using var scope = ctx.ScopeFactory.CreateScope();
            var generator = scope.ServiceProvider.GetService<ILayoutCandidateGenerator>();
            if (generator == null)
            {
                ctx.NavigationService.SetStatusMessage("Layout chooser unavailable");
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            var page = navContext.CurrentPage;
            var html = page.RawHtml;

            // Get the build cache for the current page to access the extracted links
            var buildCache = ctx.PageCache.TryGetBuildCache(page.Url);
            var links = buildCache?.Links ?? new List<Domain.ValueObjects.Browser.LinkInfo>();

            // Capture screenshot for AI analysis (may be null)
            byte[]? screenshot = null;
            if (scope.ServiceProvider.GetService<IBrowserSessionControl>() is IBrowserSession session)
            {
                screenshot = await session.CaptureScreenshotAsync();
            }

            var candidates = await generator.GenerateCandidatesAsync(
                links, html, page.Url, screenshot, ct);

            if (candidates.Count == 0)
            {
                ctx.NavigationService.SetStatusMessage("No layout candidates available");
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            // Enter preview mode
            ctx.NavigationService.EnterPreviewMode(candidates);
            await ctx.RenderCurrentPageAsync(options, ct);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Layout chooser failed");
            ctx.NavigationService.SetStatusMessage("Layout generation failed");
            await ctx.RenderCurrentPageAsync(options, ct);
        }
    }

    /// <summary>
    /// Cycles to the next layout preview (Right arrow in preview mode).
    /// </summary>
    public static async Task HandleCycleRight(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        ctx.NavigationService.CyclePreview(1);
        await ctx.RenderCurrentPageAsync(options, ct);
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
        await ctx.RenderCurrentPageAsync(options, ct);
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
                await configStore.SaveConfigAsync(selected.Config);
                ctx.NavigationService.SetStatusMessage($"Layout saved · {selected.Summary}");
            }
            else
            {
                ctx.NavigationService.SetStatusMessage($"Applied · {selected.Summary}");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to save layout config");
            ctx.NavigationService.SetStatusMessage($"Applied · {selected.Summary} (save failed)");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    /// <summary>
    /// Cancels preview mode and restores the original layout (Esc in preview mode).
    /// </summary>
    public static async Task HandleCancel(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        ctx.NavigationService.CancelPreview();
        await ctx.RenderCurrentPageAsync(options, ct);
    }
}
