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

        var expectedLeft = screenW / 2;
        var expectedWidth = screenW - expectedLeft;
        afterDock.State.Should().Be("normal");
        afterDock.Left.Should().BeCloseTo(expectedLeft, 60,
            "the window should be pinned to the right half horizontally");
        afterDock.Width.Should().BeCloseTo(expectedWidth, 80,
            "the window should span ~half the screen width");

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

        // …but summoning a live URL must open a headed window, navigate it, and dock right.
        // A data: URL keeps the test network-free while still exercising real navigation.
        var state = await session.SummonAndDockAsync("data:text/html,<title>lens</title><body>summon test</body>");
        state.Should().Be(BrowserWindowState.Docked);

        var page = await session.GetOrCreatePageAsync(headless: false);
        var screen = await page.EvaluateAsync<int[]>(
            "() => [window.screen.availWidth, window.screen.availHeight]");
        var screenW = screen[0];
        var expectedLeft = screenW / 2;
        var expectedWidth = screenW - expectedLeft;

        var bounds = await ReadBoundsAsync(page);
        _out.WriteLine($"after summon+dock : {bounds}");
        bounds.State.Should().Be("normal");
        bounds.Left.Should().BeCloseTo(expectedLeft, 60,
            "the summoned window should be pinned to the right half horizontally");
        bounds.Width.Should().BeCloseTo(expectedWidth, 80,
            "the summoned window should span ~half the screen width");
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
        afterDock.Width.Should().BeCloseTo(screenW / 2, 80,
            "the window should span ~half the screen width");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task HeadedLaunch_SidecarOn_AutoDocksInsteadOfMinimizing()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")),
            "Headed browser requires an X display — run under xvfb-run.");

        // workspace-exbz: with sidecar mode on (the default), a headed launch must dock
        // beside the terminal instead of minimizing into the void with a blank page.
        var config = Options.Create(new BrowserConfiguration { Headless = false });
        var cookieManager = Substitute.For<ICookieManager>();
        cookieManager.LoadCookiesAsync().Returns(Array.Empty<StoredCookie>());

        using var session = new BrowserSession(config, NullLogger<BrowserSession>.Instance, cookieManager);
        session.WantsSidecar.Should().BeTrue("sidecar mode defaults on");

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
        var expectedLeft = screenW / 2;

        var bounds = await ReadBoundsAsync(page);
        _out.WriteLine($"after sidecar launch : {bounds}");
        bounds.State.Should().Be("normal", "the window must NOT launch minimized in sidecar mode");
        bounds.Left.Should().BeCloseTo(expectedLeft, 60,
            "the auto-docked window should be pinned to the right half");

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
