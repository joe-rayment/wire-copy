// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Resolves the browser-dock keystroke (workspace-ziky). Toggles an existing headed
/// window between docked/minimized, or — the lens-on-demand path — summons the page the
/// terminal is currently reading into a fresh headed window docked to the right when no
/// headed window exists yet. Kept separate from <see cref="BrowserOrchestrator"/> so the
/// toggle-vs-summon decision is unit-testable against a fake <see cref="IBrowserSession"/>.
/// </summary>
internal static class BrowserDockCommandHandler
{
    /// <summary>
    /// Decides what the dock key does this press and returns the resulting window state.
    /// </summary>
    /// <param name="session">The active headed-capable browser session, or null.</param>
    /// <param name="currentUrl">URL the terminal is currently reading (may be null).</param>
    /// <param name="onSummoning">
    /// Invoked once, just before a (potentially slow) summon, so the caller can paint an
    /// "opening…" status. NOT invoked on the plain toggle path, which is instant.
    /// </param>
    /// <returns>
    /// <see cref="BrowserWindowState.Docked"/> / <see cref="BrowserWindowState.Minimized"/>,
    /// or null when there is no headed window AND no summonable live page.
    /// </returns>
    public static async Task<BrowserWindowState?> ResolveAsync(
        IBrowserSession? session,
        string? currentUrl,
        Func<Task>? onSummoning = null)
    {
        if (session is null)
        {
            return null;
        }

        // Browser-hosted web pane: there is no OS dock window to toggle — the page is streamed into
        // the user's tab (hidden/revealed there with F9). Never summon a headed window in this mode.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WIRECOPY_WEBPANE_SOCKET")))
        {
            return null;
        }

        // First, try to toggle an already-open headed window (the post-captcha/login case).
        var state = await session.ToggleWindowDockAsync().ConfigureAwait(false);
        if (state is not null)
        {
            return state;
        }

        // No headed window. If the terminal is reading a real web page, summon it beside us.
        if (!IsSummonableUrl(currentUrl))
        {
            return null;
        }

        if (onSummoning is not null)
        {
            await onSummoning().ConfigureAwait(false);
        }

        return await session.SummonAndDockAsync(currentUrl!).ConfigureAwait(false);
    }

    /// <summary>
    /// True when <paramref name="url"/> is an absolute http(s) URL worth opening in a
    /// live window. Filters out launcher/skeleton/data: states where summoning is
    /// meaningless.
    /// </summary>
    internal static bool IsSummonableUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Returns the page the lens/tuner/pick scripts should inject into: the streamed display page in
    /// the browser-hosted web pane (where there is no headed OS lens window, so
    /// <see cref="IBrowserSession.GetLensPageAsync"/> returns null), or the headed lens tab in a
    /// direct-terminal run. Both resolve to the same selection-follow page the user is looking at, so
    /// the 'L' tuner and the layout strategy chooser work in either mode.
    /// </summary>
    internal static Task<Microsoft.Playwright.IPage?> GetLensOrDisplayPageAsync(IBrowserSession session)
        => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WIRECOPY_WEBPANE_SOCKET"))
            ? session.GetLensPageAsync()
            : session.GetDisplayPageAsync();
}
