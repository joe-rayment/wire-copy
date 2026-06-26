// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Drives the REAL headed browser to verify the 'w' switcher actually positions
/// the Chromium window on the right half of the screen via CDP. Requires an X
/// display — run under xvfb-run (the test self-skips when $DISPLAY is unset or a
/// headed browser cannot launch in the environment).
/// </summary>
[Trait("Category", "Integration")]
[Collection(WireCopy.Tests.HeadedBrowserSerialCollection.Name)]
public class BrowserWindowDockIntegrationTests
{
    private readonly ITestOutputHelper _out;

    public BrowserWindowDockIntegrationTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ToggleWindowDock_DocksHeadedWindowToRightHalf_ThenMinimizes()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        // Sidecar=false: this test exercises the explicit toggle from a minimized start.
        var config = Options.Create(new BrowserConfiguration { Headless = false, Sidecar = false });
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);

        IPage page;
        try
        {
            page = await session.GetOrCreatePageAsync(headless: false);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        await page.GotoAsync("data:text/html,<title>dock</title><body>dock test</body>");

        var screen = await page.EvaluateAsync<int[]>(
            "() => [window.screen.availWidth, window.screen.availHeight]");
        var screenW = screen[0];
        var screenH = screen[1];
        _out.WriteLine($"screen.avail = {screenW}x{screenH}");

        // --- Dock to the right half ---
        var docked = await session.ToggleWindowDockAsync();
        docked.Should().Be(BrowserWindowState.Docked);

        var afterDock = await ReadBoundsAsync(page);
        _out.WriteLine($"after dock : {afterDock}");

        // workspace-o5yf: the sidecar is phone-shaped now (DockWidthPx default 430).
        var expectedWidth = 430;
        var expectedLeft = screenW - expectedWidth;
        afterDock.State.Should().Be("normal");
        afterDock.Left.Should().BeCloseTo(expectedLeft, 60,
            "the window should be pinned to the right edge");
        afterDock.Width.Should().BeCloseTo(expectedWidth, 80,
            "the window should be phone-shaped (DockWidthPx default)");

        // --- Toggle back: minimize ---
        var minimized = await session.ToggleWindowDockAsync();
        minimized.Should().Be(BrowserWindowState.Minimized);

        // A bare Xvfb has no window manager, so the read-back "minimized"
        // windowState may not be honoured (Chromium keeps reporting "normal").
        // The toggle RETURN value above is the authoritative state-machine check;
        // the read-back is logged for visibility without asserting a WM-dependent value.
        var afterMin = await ReadBoundsAsync(page);
        _out.WriteLine($"after min  : {afterMin}");
        afterMin.State.Should().BeOneOf("minimized", "normal");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task SummonAndDock_FromNoWindow_OpensNavigatesAndDocksRight()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        // Sidecar=false: the summon must dock on its own merits, not via launch auto-dock.
        var config = Options.Create(new BrowserConfiguration { Headless = false, Sidecar = false });
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);

        // Lens-on-demand (workspace-ziky): no headed window exists yet — a plain toggle
        // must no-op…
        BrowserWindowState? toggled;
        try
        {
            toggled = await session.ToggleWindowDockAsync();
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        toggled.Should().BeNull("there is no headed window to toggle before the first summon");

        // …but summoning must open a headed window WITH a lens tab and dock right.
        // workspace-u4o9: the summon no longer navigates — follow-navigation is the
        // dock spotlight's job — so the assertion is lens existence + dock geometry.
        var state = await session.SummonAndDockAsync("https://example.com/");
        state.Should().Be(BrowserWindowState.Docked);

        var lens = await session.GetLensPageAsync();
        lens.Should().NotBeNull("the summon must create the lens tab");

        // workspace-o5yf: the lens renders phone-shaped so responsive sites collapse
        // to a single column and spotlight targets are always on-screen.
        lens!.ViewportSize.Should().NotBeNull();
        lens.ViewportSize!.Width.Should().Be(414, "the lens viewport is pinned to mobile width");

        var page = await session.GetOrCreatePageAsync(headless: false);
        var screen = await page.EvaluateAsync<int[]>(
            "() => [window.screen.availWidth, window.screen.availHeight]");
        var screenW = screen[0];
        // workspace-o5yf: the sidecar is phone-shaped now (DockWidthPx default 430).
        var expectedWidth = 430;
        var expectedLeft = screenW - expectedWidth;

        var bounds = await ReadBoundsAsync(page);
        _out.WriteLine($"after summon+dock : {bounds}");
        bounds.State.Should().Be("normal");
        bounds.Left.Should().BeCloseTo(expectedLeft, 60,
            "the summoned window should be pinned to the right edge");
        bounds.Width.Should().BeCloseTo(expectedWidth, 80,
            "the summoned window should be phone-shaped (DockWidthPx default)");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task IsWindowDocked_FlipsFalse_WhenHeadedWindowCloses()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        var config = Options.Create(new BrowserConfiguration { Headless = false, Sidecar = false });
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);

        IPage page;
        try
        {
            page = await session.GetOrCreatePageAsync(headless: false);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        await page.GotoAsync("data:text/html,<title>dock</title><body>dock test</body>");

        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Docked);
        session.IsWindowDocked.Should().BeTrue("the window was just docked");

        // Simulate the user closing/crashing the headed window: the persistent affordance
        // must not keep claiming "docked" (workspace-v7mb crash-reset wiring).
        await page.CloseAsync();
        // The Close event handler runs on Playwright's dispatch loop; give it a beat.
        for (var i = 0; i < 50 && session.IsWindowDocked; i++)
        {
            await Task.Delay(20);
        }

        session.IsWindowDocked.Should().BeFalse("closing the headed window must clear the docked state");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ToggleWindowDock_LeftDock_PinsHeadedWindowToLeftHalf()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        // workspace-8fkv / workspace-nqqs: DockSide.Left must pin the live window to the
        // display's LEFT edge (the terminal then keeps the right columns). Same CDP path as
        // right-dock, only the computed bounds differ — verified here against a real window.
        var config = Options.Create(new BrowserConfiguration { Headless = false, DockSide = DockSide.Left, Sidecar = false });
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);

        IPage page;
        try
        {
            page = await session.GetOrCreatePageAsync(headless: false);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        await page.GotoAsync("data:text/html,<title>dock</title><body>left dock test</body>");

        var screen = await page.EvaluateAsync<int[]>(
            "() => [window.screen.availWidth, window.screen.availHeight, Math.round(window.screen.availLeft || 0)]");
        var screenW = screen[0];
        var availLeft = screen[2];
        _out.WriteLine($"screen.availWidth = {screenW}, availLeft = {availLeft}");

        var docked = await session.ToggleWindowDockAsync();
        docked.Should().Be(BrowserWindowState.Docked);

        var afterDock = await ReadBoundsAsync(page);
        _out.WriteLine($"after left dock : {afterDock}");

        afterDock.State.Should().Be("normal");
        afterDock.Left.Should().BeCloseTo(availLeft, 60,
            "left-dock pins the window to the display's left work-area edge");
        afterDock.Width.Should().BeCloseTo(430, 80,
            "the window should be phone-shaped (DockWidthPx default)");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task HeadedLaunch_SidecarOn_AutoDocksInsteadOfMinimizing()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        // workspace-exbz: with sidecar mode ON (opt-in since workspace-75ng), a headed launch
        // must dock beside the terminal instead of minimizing into the void with a blank page.
        var config = Options.Create(new BrowserConfiguration { Headless = false, Sidecar = true });
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);
        session.WantsSidecar.Should().BeTrue("Sidecar=true was requested");

        IPage page;
        try
        {
            page = await session.GetOrCreatePageAsync(headless: false);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        session.IsWindowDocked.Should().BeTrue(
            "sidecar mode docks the headed window at launch instead of minimizing it");

        var screen = await page.EvaluateAsync<int[]>(
            "() => [window.screen.availWidth, window.screen.availHeight]");
        var screenW = screen[0];
        var expectedLeft = screenW - 430;

        var bounds = await ReadBoundsAsync(page);
        _out.WriteLine($"after sidecar launch : {bounds}");
        bounds.State.Should().Be("normal", "the window must NOT launch minimized in sidecar mode");
        bounds.Left.Should().BeCloseTo(expectedLeft, 60,
            "the auto-docked window should be pinned phone-width to the right edge");

        // Background quieting (the preload service re-minimizes around every prefetch)
        // must NOT strip a dock the user wants — workspace-exbz regression: the dock
        // engaged and was minimized away 3 seconds later by the first preload tick.
        await session.MinimizeWindowAsync();
        session.IsWindowDocked.Should().BeTrue(
            "a background minimize must keep the sidecar docked while the user wants it");
        (await ReadBoundsAsync(page)).State.Should().Be("normal");

        // The explicit toggle is the real un-dock: it clears the intent first.
        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Minimized);
        session.IsWindowDocked.Should().BeFalse();

        // And with the intent cleared, a background minimize stays minimized.
        await session.MinimizeWindowAsync();
        session.IsWindowDocked.Should().BeFalse();
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task HeadedLaunch_Default_ParksWindowOffScreen_RendersButNotVisible()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        // workspace-75ng.1: the DEFAULT launch (Sidecar off) PARKS the headed window
        // off-screen — it never pops up on-screen or steals focus — yet keeps rendering so
        // CDP/DOM extraction still works. Proven by the window's real bounds + a live DOM
        // read, not logs.
        var config = Options.Create(new BrowserConfiguration { Headless = false }); // Sidecar defaults OFF (parked)
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);
        session.WantsSidecar.Should().BeFalse("the default is parked/immersive, not auto-dock");

        IPage page;
        try
        {
            page = await session.GetOrCreatePageAsync(headless: false);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        session.IsWindowDocked.Should().BeFalse("the default launch parks off-screen, it does not dock");

        var bounds = await ReadBoundsAsync(page);
        _out.WriteLine($"after default (parked) launch : {bounds}");

        // OUTCOME: the window sits entirely OFF the visible region (right edge left of x=0),
        // so a screenshot of the screen shows no browser — and it is NOT minimized.
        bounds.State.Should().Be("normal",
            "a parked window stays normal so it keeps rendering — minimizing would occlusion-throttle it");
        (bounds.Left + bounds.Width).Should().BeLessThanOrEqualTo(0,
            "the parked window must sit entirely off the left of the visible region");

        // OUTCOME: the off-screen page still renders and is fully extractable.
        await page.GotoAsync("data:text/html,<title>parked</title><body><h1 id='h'>parked and rendering</h1></body>");
        var heading = await page.EvaluateAsync<string>("() => document.getElementById('h')?.textContent ?? ''");
        heading.Should().Be("parked and rendering",
            "the parked window keeps rendering off-screen — extraction is visibility-independent");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ToggleWindowDock_FromParked_DocksOnScreen_ThenReParksOffScreen()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        // workspace-75ng.2: 'O' brings the parked window ON-SCREEN docked; pressing 'O' again
        // RE-PARKS it off-screen (not minimized) so it keeps rendering for an instant re-dock.
        var config = Options.Create(new BrowserConfiguration { Headless = false }); // parked default
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);

        IPage page;
        try
        {
            page = await session.GetOrCreatePageAsync(headless: false);
        }
        catch (Exception ex)
        {
            Skip.If(true, $"Headed Chromium could not launch here: {ex.Message}");
            return;
        }

        await page.GotoAsync(
            "data:text/html,<title>park</title>"
            + "<body style='background:%23202060'><h1 id='h' style='color:white'>live page</h1></body>");

        var screen = await page.EvaluateAsync<int[]>(
            "() => [window.screen.availWidth, window.screen.availHeight]");
        var screenW = screen[0];

        var parkedStart = await ReadBoundsAsync(page);
        _out.WriteLine($"parked at launch : {parkedStart}");
        (parkedStart.Left + parkedStart.Width).Should().BeLessThanOrEqualTo(0,
            "the default launch parks the window off-screen");

        // --- 'O' : reveal on-screen, docked ---
        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Docked);
        var afterDock = await ReadBoundsAsync(page);
        _out.WriteLine($"after dock (O) : {afterDock}");
        afterDock.State.Should().Be("normal");
        (afterDock.Left + afterDock.Width).Should().BeGreaterThan(0,
            "docking brings the window into the visible region");
        afterDock.Left.Should().BeLessThan(screenW,
            "the docked window's left edge is on-screen, not parked off the right either");

        // --- 'O' again : re-park off-screen (NOT minimized) ---
        (await session.ToggleWindowDockAsync()).Should().Be(BrowserWindowState.Minimized);
        var afterPark = await ReadBoundsAsync(page);
        _out.WriteLine($"after re-park (O) : {afterPark}");
        afterPark.State.Should().Be("normal",
            "re-park keeps windowState=normal (NOT minimized) so the live page keeps painting");
        (afterPark.Left + afterPark.Width).Should().BeLessThanOrEqualTo(0,
            "re-park moves the window entirely off the visible region");
        session.IsWindowDocked.Should().BeFalse("re-park clears the docked state");

        // OUTCOME: the re-parked page still RENDERS. The dock spotlight drives the live page
        // via CDP screenshots, which are window-visibility-independent — they force a frame
        // even off-screen, so the re-dock shows current content instantly. (A minimized window
        // can stall this; an off-screen normal window does not.) This is the real "keeps
        // rendering" guarantee that matters here. NOTE: requestAnimationFrame is paused by
        // Chromium for any non-visible surface (off-screen included), so wall-clock animation
        // does not advance while parked — only the on-demand CDP render path does, which is
        // exactly what the spotlight uses.
        var parkedShot = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png });
        _out.WriteLine($"parked CDP screenshot bytes: {parkedShot.Length}");
        parkedShot.Should().NotBeNull();
        parkedShot.Length.Should().BeGreaterThan(1000,
            "the parked off-screen page must still render a real frame via CDP (the spotlight's mechanism)");
        IsPng(parkedShot).Should().BeTrue("the parked window produced a valid rendered PNG frame");

        // And the page stays fully reachable/evaluable while parked.
        (await page.EvaluateAsync<string>("() => document.getElementById('h')?.textContent ?? ''"))
            .Should().Be("live page", "the parked page's DOM is intact and extractable");
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    private static async Task<WindowBounds> ReadBoundsAsync(IPage page)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var info = await cdp.SendAsync("Browser.getWindowForTarget")
            ?? throw new InvalidOperationException("getWindowForTarget returned no payload");
        var b = info.GetProperty("bounds");
        return new WindowBounds(
            b.GetProperty("left").GetInt32(),
            b.GetProperty("top").GetInt32(),
            b.GetProperty("width").GetInt32(),
            b.GetProperty("height").GetInt32(),
            b.GetProperty("windowState").GetString() ?? "unknown");
    }

    private readonly record struct WindowBounds(int Left, int Top, int Width, int Height, string State)
    {
        public override string ToString() => $"left={Left} top={Top} width={Width} height={Height} state={State}";
    }
}
