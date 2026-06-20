// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Playwright;
using WireCopy.Application.Interfaces;

namespace WireCopy.Infrastructure.Browser.Extension;

/// <summary>
/// The browser session used in extension mode (<c>WIRECOPY_BROWSER=extension</c>, epic workspace-blg5).
/// In extension mode the user's OWN browser is the renderer — the server-side Playwright browser is
/// RETIRED. This implementation therefore reports the browser as <b>unavailable</b> and no-ops every
/// operation, so NOTHING can launch a server-side Chromium: not the content-quality retry in
/// <see cref="PageLoadPipeline"/>, not the bot-challenge / login polls, not refresh or the human-action
/// watcher (all of which gate on <see cref="IsBrowserAvailable"/>), and not a stray UI dock/summon
/// command (which call the control methods directly — here they are no-ops too).
///
/// That stray server-side window is the cardinal single-window violation the extension epic exists to
/// eliminate (workspace-blg5.2): the real <see cref="BrowserSession"/> hardcoded
/// <c>IsBrowserAvailable =&gt; true</c>, so in extension mode every fallback path happily launched a
/// second visible browser window.
/// </summary>
public sealed class ExtensionBrowserSession : IBrowserSession, IAsyncDisposable
{
    /// <inheritdoc />
    public bool HasActiveBrowser => false;

    /// <inheritdoc />
    public bool IsWindowDocked => false;

    /// <inheritdoc />
    public bool HasBrowserContext => false;

    /// <inheritdoc />
    /// <remarks>
    /// The whole point: the server-side browser is retired in extension mode. Every auto-launch path
    /// guards on this, so returning false short-circuits all of them.
    /// </remarks>
    public bool IsBrowserAvailable => false;

    /// <inheritdoc />
    public bool IsDocked => false;

    /// <inheritdoc />
    public bool WantsSidecar => false;

    /// <inheritdoc />
    public bool HasPreloadContext => false;

    /// <inheritdoc />
    /// <remarks>
    /// Never reached: all callers gate on <see cref="IsBrowserAvailable"/> first. If something ever does
    /// call this in extension mode it is a wiring bug — surface it loudly rather than silently launching.
    /// </remarks>
    public Task<IPage> GetOrCreatePageAsync(bool headless)
    {
        throw new InvalidOperationException(
            "The server-side browser is retired in extension mode (WIRECOPY_BROWSER=extension); " +
            "the user's own browser is the renderer. This call should be guarded by IsBrowserAvailable.");
    }

    /// <inheritdoc />
    public Task<IPage?> GetLensPageAsync()
    {
        return Task.FromResult<IPage?>(null);
    }

    /// <inheritdoc />
    public Task<IPage?> GetDisplayPageAsync()
    {
        return Task.FromResult<IPage?>(null);
    }

    /// <inheritdoc />
    public Task<DateTimeOffset?> ReadLastUserInputAsync()
    {
        return Task.FromResult<DateTimeOffset?>(null);
    }

    /// <inheritdoc />
    public void ReleasePage()
    {
    }

    /// <inheritdoc />
    public Task InvalidatePageAsync()
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RestoreWindowAsync()
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MinimizeWindowAsync()
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<BrowserWindowState?> ToggleWindowDockAsync()
    {
        return Task.FromResult<BrowserWindowState?>(null);
    }

    /// <inheritdoc />
    public Task<BrowserWindowState?> SummonAndDockAsync(string url)
    {
        return Task.FromResult<BrowserWindowState?>(null);
    }

    /// <inheritdoc />
    public Task<byte[]?> CaptureScreenshotAsync()
    {
        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public Task<byte[]?> CaptureScreenshotAsync(IReadOnlyList<ScreenshotMark>? marks)
    {
        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public Task<IPage?> CreateBackgroundPageAsync()
    {
        return Task.FromResult<IPage?>(null);
    }

    /// <inheritdoc />
    public Task CloseBackgroundPageAsync(IPage page)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredCookie>> GetCookiesForUrlAsync(string url)
    {
        return Task.FromResult<IReadOnlyList<StoredCookie>>(Array.Empty<StoredCookie>());
    }

    /// <inheritdoc />
    public Task<int> SyncCookiesToPreloadContextAsync(IReadOnlyList<StoredCookie> cookies)
    {
        return Task.FromResult(0);
    }

    /// <inheritdoc />
    /// <remarks>Warmup is a no-op — there is no server-side browser to warm.</remarks>
    public Task WarmUpAsync()
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
