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

        var config = Options.Create(new BrowserConfiguration { Headless = false });
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

        var config = Options.Create(new BrowserConfiguration { Headless = false });
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
