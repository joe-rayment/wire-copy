// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles view switching and width adjustment commands.
/// </summary>
internal static class ViewCommandHandler
{
    private const int DefaultContentWidth = 66;
    private const int WidthStep = 10;
    private const int MinContentWidth = 40;
    private const int MaxContentWidth = 120;

    public static async Task HandleSwitchView(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        if (viewMode is ViewMode.CollectionList or ViewMode.CollectionItems or ViewMode.Launcher)
        {
            return;
        }

        ctx.NavigationService.ToggleViewMode();
        ctx.LineCacheManager.InvalidateLineCache();
        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleSwitchToHierarchical(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        if (viewMode is ViewMode.CollectionList or ViewMode.CollectionItems or ViewMode.Launcher)
        {
            return;
        }

        ctx.NavigationService.SetViewMode(ViewMode.Hierarchical);
        ctx.LineCacheManager.InvalidateLineCache();
        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleSwitchToReadable(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        if (viewMode is ViewMode.CollectionList or ViewMode.CollectionItems or ViewMode.Launcher)
        {
            return;
        }

        ctx.NavigationService.SetViewMode(ViewMode.Readable);
        ctx.LineCacheManager.InvalidateLineCache();
        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleIncreaseWidth(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var current = ctx.ContentWidthOverride ?? DefaultContentWidth;
        ctx.ContentWidthOverride = Math.Clamp(current + WidthStep, MinContentWidth, MaxContentWidth);
        ctx.NavigationService.SetStatusMessage($"Width: {ctx.ContentWidthOverride}");
        var newOptions = ctx.GetCurrentRenderOptions();
        ctx.LineCacheManager.PreserveScrollPositionAfterRewrap(newOptions);
        await ctx.RenderCurrentPageAsync(newOptions, ct);
    }

    public static async Task HandleDecreaseWidth(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var current = ctx.ContentWidthOverride ?? DefaultContentWidth;
        ctx.ContentWidthOverride = Math.Clamp(current - WidthStep, MinContentWidth, MaxContentWidth);
        ctx.NavigationService.SetStatusMessage($"Width: {ctx.ContentWidthOverride}");
        var newOptions = ctx.GetCurrentRenderOptions();
        ctx.LineCacheManager.PreserveScrollPositionAfterRewrap(newOptions);
        await ctx.RenderCurrentPageAsync(newOptions, ct);
    }

    public static async Task HandleResetWidth(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        ctx.ContentWidthOverride = null;
        ctx.NavigationService.SetStatusMessage($"Width: {DefaultContentWidth} (default)");
        var newOptions = ctx.GetCurrentRenderOptions();
        ctx.LineCacheManager.PreserveScrollPositionAfterRewrap(newOptions);
        await ctx.RenderCurrentPageAsync(newOptions, ct);
    }

    public static async Task HandleOpenLauncher(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        ctx.NavigationService.EnterLauncher();
        await ctx.RefreshBookmarksAsync(ct);
        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleCycleTheme(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        ctx.ThemeProvider.CycleTheme();
        ctx.NavigationService.SetStatusMessage(ctx.ThemeProvider.CurrentTheme.ToString());
        ctx.LineCacheManager.InvalidateLineCache();
        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleTerminalResized(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var newOptions = ctx.GetCurrentRenderOptions();
        if (ctx.LineCacheManager.CachedWidth > 0 && newOptions.MaxContentWidth != ctx.LineCacheManager.CachedWidth)
        {
            ctx.LineCacheManager.PreserveScrollPositionAfterRewrap(newOptions);
        }

        ctx.LineCacheManager.ClampScrollOffset();
        await ctx.RenderCurrentPageAsync(newOptions, ct);
    }

    public static async Task HandleShowHelp(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Console.Write("\x1b[H\x1b[2J");
            Console.WriteLine(ctx.InputHandler.GetHelpText());

            var command = await ctx.InputHandler.WaitForInputAsync(ct);
            if (command.Type == CommandType.TerminalResized)
            {
                continue;
            }

            break;
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleDumpHtml(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var page = ctx.NavigationService.CurrentPage;
        if (page == null || string.IsNullOrEmpty(page.RawHtml))
        {
            ctx.NavigationService.SetStatusMessage("No page loaded to dump");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        try
        {
            var fixturesDir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
            Directory.CreateDirectory(fixturesDir);

            var uri = new Uri(page.Url);
            var domain = uri.Host.Replace(".", "_");
            var pathSegment = uri.AbsolutePath.Trim('/').Replace("/", "_");
            if (string.IsNullOrEmpty(pathSegment))
            {
                pathSegment = "index";
            }

            // Sanitize: keep only alphanumeric, underscore, hyphen
            var safePath = new string(pathSegment.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
            if (safePath.Length > 80)
            {
                safePath = safePath[..80];
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = $"{domain}_{safePath}_{timestamp}.html";
            var filePath = Path.Combine(fixturesDir, fileName);

            await File.WriteAllTextAsync(filePath, page.RawHtml, ct);

            ctx.NavigationService.SetStatusMessage($"HTML dumped to fixtures/{fileName}");
            ctx.Logger.LogInformation("Dumped page HTML to {FilePath} ({Bytes} bytes)", filePath, page.RawHtml.Length);
        }
        catch (Exception ex)
        {
            ctx.NavigationService.SetStatusMessage($"Dump failed: {ex.Message}");
            ctx.Logger.LogWarning(ex, "Failed to dump page HTML");
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }

    public static async Task HandleOpenInBrowser(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var url = ctx.NavigationService.CurrentPage?.Url;
        if (string.IsNullOrEmpty(url))
        {
            ctx.NavigationService.SetStatusMessage("No page URL to open");
            await ctx.RenderCurrentPageAsync(options, ct);
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
            ctx.NavigationService.SetStatusMessage("Opened in browser");
        }
        catch (Exception ex)
        {
            ctx.NavigationService.SetStatusMessage($"Failed to open browser: {ex.Message}");
            ctx.Logger.LogWarning(ex, "Failed to open URL in browser: {Url}", url);
        }

        await ctx.RenderCurrentPageAsync(options, ct);
    }
}
