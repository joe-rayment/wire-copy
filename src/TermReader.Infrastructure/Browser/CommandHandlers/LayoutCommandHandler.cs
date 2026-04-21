// Educational and personal use only.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles the layout chooser flow: generates layout candidates,
/// enters preview mode, and manages the cycling/save/cancel lifecycle.
/// </summary>
internal static class LayoutCommandHandler
{
    // Braille spinner + stage messages for the generation animation
    private static readonly string[] Spinner = ["\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2826", "\u2827", "\u2807", "\u280f"];

    private static readonly string[] WarmUpPhrases =
    [
        "Analyzing page structure",
        "Studying the layout",
        "Reading the room",
        "Mapping the terrain",
        "Surveying the landscape",
    ];

    /// <summary>
    /// Opens the layout chooser. Shows an animated progress sequence while
    /// generating candidates (document-order instantly, AI and RSS in parallel),
    /// then enters preview mode.
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

        // Pick a random warm-up phrase for variety
        var phrase = WarmUpPhrases[Environment.TickCount % WarmUpPhrases.Length];
        ctx.NavigationService.SetStatusMessage($"{phrase}...", TimeSpan.FromMinutes(1));
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
            var links = buildCache?.Links ?? new List<LinkInfo>();

            // Capture screenshot for AI analysis (may be null)
            byte[]? screenshot = null;
            if (scope.ServiceProvider.GetService<IBrowserSessionControl>() is IBrowserSession session)
            {
                screenshot = await session.CaptureScreenshotAsync();
            }

            // Generate candidates with animated progress updates
            var generateTask = generator.GenerateCandidatesAsync(
                links, html, page.Url, screenshot, ct);

            var candidates = await AnimateWhileWaiting(
                generateTask, ctx, options, phrase, ct);

            if (candidates.Count == 0)
            {
                ctx.NavigationService.SetStatusMessage("No layout candidates found");
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            if (candidates.Count == 1)
            {
                // Only one layout — skip preview mode, just apply it directly
                ctx.NavigationService.SetStatusMessage(
                    $"\u2714 {candidates[0].Summary} (only layout available)");
                await ctx.RenderCurrentPageAsync(options, ct);
                return;
            }

            // Multiple layouts — celebrate and enter preview mode
            ctx.NavigationService.SetStatusMessage(
                $"\u2728 {candidates.Count} layouts ready! Use \u25c0/\u25b6 to browse");
            await ctx.RenderCurrentPageAsync(options, ct);
            await Task.Delay(600, ct);

            ctx.NavigationService.EnterPreviewMode(candidates);
            await ctx.RenderCurrentPageAsync(options, ct);
        }
        catch (OperationCanceledException)
        {
            // User cancelled — clean exit
        }
        catch (Exception ex)
        {
            ctx.Logger.LogError(ex, "Layout chooser failed");
            ctx.NavigationService.SetStatusMessage("Layout generation failed");
            await ctx.RenderCurrentPageAsync(options, ct);
        }
    }

    /// <summary>
    /// Animates the status bar with a spinner while a task is running.
    /// </summary>
    private static async Task<List<LayoutCandidate>> AnimateWhileWaiting(
        Task<List<LayoutCandidate>> task,
        CommandContext ctx,
        RenderOptions options,
        string basePhrase,
        CancellationToken ct)
    {
        var frame = 0;
        var dots = 0;

        while (!task.IsCompleted)
        {
            var spinChar = Spinner[frame % Spinner.Length];
            var dotStr = new string('.', (dots % 3) + 1).PadRight(3);
            ctx.NavigationService.SetStatusMessage(
                $"{spinChar} {basePhrase}{dotStr}",
                TimeSpan.FromMinutes(1));
            await ctx.RenderCurrentPageAsync(options, ct);

            frame++;
            dots++;

            // Wait 250ms or until the task completes, whichever is first
            var delayTask = Task.Delay(250, ct);
            await Task.WhenAny(task, delayTask);
        }

        return await task;
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
            ctx.NavigationService.SetStatusMessage($"Applied · {selected.Summary} (save failed)");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
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
                var deleted = await configStore.DeleteConfigAsync(page.Url);
                ctx.NavigationService.SetStatusMessage(
                    deleted ? "Layout config cleared" : "No saved layout for this page");
            }
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Failed to clear layout config");
            ctx.NavigationService.SetStatusMessage("Failed to clear layout config");
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
