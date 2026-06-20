// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.Extension;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-blg5.2: in extension mode the server-side browser is retired, so the session must report
/// the browser UNAVAILABLE and no-op everything — that is what prevents any fallback path (or stray UI
/// command) from launching the cardinal-rule-violating second browser window.
/// </summary>
public sealed class ExtensionBrowserSessionTests
{
    [Fact]
    public void ReportsBrowserUnavailable_SoNoFallbackPathLaunchesAStrayWindow()
    {
        var session = new ExtensionBrowserSession();

        // Every auto-launch path (PageLoadPipeline content-quality retry, bot/login polls, refresh,
        // human-action watcher) gates on these — all must be false in extension mode.
        session.IsBrowserAvailable.Should().BeFalse();
        session.HasActiveBrowser.Should().BeFalse();
        session.HasBrowserContext.Should().BeFalse();
        session.HasPreloadContext.Should().BeFalse();
        session.IsDocked.Should().BeFalse();
        session.IsWindowDocked.Should().BeFalse();
        session.WantsSidecar.Should().BeFalse();
    }

    [Fact]
    public void GetOrCreatePageAsync_ThrowsRatherThanLaunching()
    {
        var session = new ExtensionBrowserSession();

        // Reaching this in extension mode is a wiring bug — fail loudly, never silently launch a browser.
        var act = () => session.GetOrCreatePageAsync(headless: false);
        act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ControlAndQueryMethods_AreSafeNoOps()
    {
        var session = new ExtensionBrowserSession();

        // A stray UI dock/summon/warmup command must not launch anything — these return cleanly.
        await session.WarmUpAsync();
        (await session.ToggleWindowDockAsync()).Should().BeNull();
        (await session.SummonAndDockAsync("https://example.com/")).Should().BeNull();
        (await session.GetLensPageAsync()).Should().BeNull();
        (await session.GetDisplayPageAsync()).Should().BeNull();
        (await session.CaptureScreenshotAsync()).Should().BeNull();
        (await session.GetCookiesForUrlAsync("https://example.com/")).Should().BeEmpty();
        (await session.SyncCookiesToPreloadContextAsync(Array.Empty<WireCopy.Application.Interfaces.StoredCookie>()))
            .Should().Be(0);
        session.ReleasePage(); // does not throw
    }
}
