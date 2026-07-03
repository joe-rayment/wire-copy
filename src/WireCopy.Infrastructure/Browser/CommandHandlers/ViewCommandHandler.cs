// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Handles view switching and width adjustment commands.
/// </summary>
internal static class ViewCommandHandler
{
    private const int DefaultContentWidth = 60;
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
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleIncreaseWidth(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var current = ctx.ContentWidthOverride ?? DefaultContentWidth;
        var newWidth = Math.Clamp(current + WidthStep, MinContentWidth, MaxContentWidth);
        ctx.ContentWidthOverride = newWidth;
        AnnounceWidthChange(ctx, current, newWidth, boundLabel: "maximum");
        var newOptions = ctx.GetCurrentRenderOptions();
        ctx.LineCacheManager.PreserveScrollPositionAfterRewrap(newOptions);
        await ctx.RenderCurrentPageAsync(newOptions, ct).ConfigureAwait(false);
    }

    public static async Task HandleDecreaseWidth(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var current = ctx.ContentWidthOverride ?? DefaultContentWidth;
        var newWidth = Math.Clamp(current - WidthStep, MinContentWidth, MaxContentWidth);
        ctx.ContentWidthOverride = newWidth;
        AnnounceWidthChange(ctx, current, newWidth, boundLabel: "minimum");
        var newOptions = ctx.GetCurrentRenderOptions();
        ctx.LineCacheManager.PreserveScrollPositionAfterRewrap(newOptions);
        await ctx.RenderCurrentPageAsync(newOptions, ct).ConfigureAwait(false);
    }

    public static async Task HandleResetWidth(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        ctx.ContentWidthOverride = null;
        ctx.NavigationService.Announce(
            glyph: null,
            $"Width {DefaultContentWidth} (default)",
            new[]
            {
                new StatusKeyHint("[", "narrow"),
                new StatusKeyHint("]", "widen"),
                new StatusKeyHint("0", "reset"),
            });
        var newOptions = ctx.GetCurrentRenderOptions();
        ctx.LineCacheManager.PreserveScrollPositionAfterRewrap(newOptions);
        await ctx.RenderCurrentPageAsync(newOptions, ct).ConfigureAwait(false);
    }

    public static async Task HandleOpenLauncher(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        ctx.NavigationService.EnterLauncher();
        await ctx.RefreshBookmarksAsync(ct).ConfigureAwait(false);
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleCycleTheme(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        ctx.ThemeProvider.CycleTheme();
        ctx.NavigationService.Announce(
            glyph: null,
            $"Theme: {ctx.ThemeProvider.CurrentTheme}",
            new[] { new StatusKeyHint("Ctrl+p", "next") });
        ctx.LineCacheManager.InvalidateLineCache();
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleTerminalResized(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var newOptions = ctx.GetCurrentRenderOptions();
        if (ctx.LineCacheManager.CachedWidth > 0 && newOptions.MaxContentWidth != ctx.LineCacheManager.CachedWidth)
        {
            ctx.LineCacheManager.PreserveScrollPositionAfterRewrap(newOptions);
        }

        ctx.LineCacheManager.ClampScrollOffset();
        await ctx.RenderCurrentPageAsync(newOptions, ct).ConfigureAwait(false);
    }

    public static Task HandleShowHelp(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var mode = ctx.NavigationService.CurrentContext.ViewMode;
        return ShowHelpPopupAsync(
            ctx,
            options,
            (palette, opts) => UI.Components.KeybindingPopup.Render(mode, palette, opts.TerminalWidth, opts.TerminalHeight),
            ct);
    }

    /// <summary>
    /// workspace-syj1.4 — ':help' documents the ':' commands themselves (verb, args,
    /// one-liner) rather than the '?' keybinding popup, which describes keys.
    /// </summary>
    public static Task HandleShowCommandLineHelp(CommandContext ctx, RenderOptions options, CancellationToken ct)
        => ShowHelpPopupAsync(
            ctx,
            options,
            (palette, opts) => UI.Components.KeybindingPopup.RenderCommandReference(palette, opts.TerminalWidth, opts.TerminalHeight),
            ct);

    public static async Task HandleDumpHtml(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var page = ctx.NavigationService.CurrentPage;
        if (page == null || string.IsNullOrEmpty(page.RawHtml))
        {
            ctx.NavigationService.SetStatusMessage("No page loaded to dump");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
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

            await File.WriteAllTextAsync(filePath, page.RawHtml, ct).ConfigureAwait(false);

            ctx.NavigationService.SetStatusMessage($"HTML dumped to fixtures/{fileName}");
            ctx.Logger.LogInformation("Dumped page HTML to {FilePath} ({Bytes} bytes)", filePath, page.RawHtml.Length);
        }
        catch (Exception ex)
        {
            ctx.NavigationService.SetStatusMessage($"Dump failed: {ex.Message}", StatusSeverity.Error);
            ctx.Logger.LogWarning(ex, "Failed to dump page HTML");
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    public static async Task HandleOpenInBrowser(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var viewMode = ctx.NavigationService.CurrentContext.ViewMode;
        string? url;

        if (viewMode == ViewMode.Hierarchical)
        {
            // In link tree view, open the selected link's URL (what the user is looking at)
            var selectedNode = ctx.NavigationService.CurrentPage?.LinkTree?.GetSelectedNode();
            url = selectedNode?.Link.Url ?? ctx.NavigationService.CurrentPage?.Url;
        }
        else
        {
            // In reader view, collection items, etc. — open the current page URL
            url = ctx.NavigationService.CurrentPage?.Url;
        }

        if (string.IsNullOrEmpty(url))
        {
            ctx.NavigationService.SetStatusMessage("No URL to open");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        // workspace-kdda: when the preloader is sitting on a CAPTCHA / login
        // gate, the system browser (Process.Start) lands in a separate
        // context with no shared session — solving the gate there has no
        // effect on the in-app preloader (the user previously saw a blank
        // page on switching to Chrome). Route through the orchestrator's
        // headed-Chrome flow so the user is solving in the SAME browser
        // session the preloader uses.
        var blocked = ctx.PreloadService.GetProgress().BlockedAction;
        if (blocked != null && UrlMatchesDomain(url, blocked.Domain))
        {
            await ctx.OpenInteractiveBrowserAsync(url, options, ct).ConfigureAwait(false);
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
            ctx.NavigationService.SetStatusMessage($"Failed to open browser: {ex.Message}", StatusSeverity.Error);
            ctx.Logger.LogWarning(ex, "Failed to open URL in browser: {Url}", url);
        }

        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// workspace-1m3h.2/.5: announces a width adjustment as a transition
    /// ("Width 60 → 70") so the old value is visible; when the clamp left the
    /// width unchanged we're at a bound and say that instead
    /// ("Width 120 (maximum)" / "Width 40 (minimum)").
    /// </summary>
    private static void AnnounceWidthChange(CommandContext ctx, int oldWidth, int newWidth, string boundLabel)
    {
        var text = newWidth == oldWidth
            ? $"Width {newWidth} ({boundLabel})"
            : $"Width {oldWidth} → {newWidth}";
        ctx.NavigationService.Announce(
            glyph: null,
            text,
            new[]
            {
                new StatusKeyHint("[", "narrow"),
                new StatusKeyHint("]", "widen"),
                new StatusKeyHint("0", "reset"),
            });
    }

    private static bool UrlMatchesDomain(string url, string domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, domain, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ShowHelpPopupAsync(
        CommandContext ctx,
        RenderOptions options,
        Action<Themes.ThemePalette, RenderOptions> renderPopup,
        CancellationToken ct)
    {
        var palette = Themes.BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);

        // Render the popup overlay (paints over current content)
        renderPopup(palette, options);

        // Wait for any keypress to dismiss (re-render on resize)
        while (!ct.IsCancellationRequested)
        {
            var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);
            if (command.Type == CommandType.TerminalResized)
            {
                // Re-render page + popup on resize
                await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                options = ctx.GetCurrentRenderOptions();
                renderPopup(palette, options);
                continue;
            }

            break;
        }

        // Restore the original page render
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }
}
