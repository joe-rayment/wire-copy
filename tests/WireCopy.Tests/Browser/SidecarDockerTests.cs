// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Drives the full dock-placement FLOW (workspace-75ng) against a fake window controller that
/// simulates the macOS conditions which broke in the field and which Xvfb can't reproduce: a
/// phantom work-area read from a just-un-parked window, and Chromium clamping the requested
/// phone width UP to a platform minimum. Proves the placement is always on-screen, flush, and
/// never full-width — in this Linux environment, no Mac required.
/// </summary>
public class SidecarDockerTests
{
    private static SidecarGeometry.DisplayInfo D(int l, int t, int w, int h) => new(l, t, w, h);

    [Fact]
    public async Task Place_AnchorsAtTerminalPosition_WhenKnown()
    {
        // workspace-9k27.8: anchoring at (0,0) always resolves the PRIMARY display's
        // work area — a terminal on a secondary monitor got its dock (and tile) yanked
        // to the primary. With the terminal's position passed as the anchor, the
        // pre-read move lands on the terminal's display instead.
        var moves = new List<(int X, int Y)>();
        var geo = new FakeGeo([D(2560, 0, 1920, 1080)], minWidthClamp: 0, onMove: (x, y) => moves.Add((x, y)));

        var result = await SidecarDocker.PlaceAsync(
            geo, DockSide.Right, requestedWidthPx: 390, dockFraction: 0.5, anchor: (2600, 40));

        moves[0].Should().Be((2600, 40), "the anchor move targets the terminal's display, not the global origin");
        result.Should().NotBeNull();
        var b = result!.Value.Browser;
        (b.X + b.Width).Should().Be(2560 + 1920, "the dock is flush inside the SECONDARY display's work area");
    }

    [Fact]
    public async Task Place_AnchorsAtGlobalOrigin_WhenTerminalUnknown()
    {
        var moves = new List<(int X, int Y)>();
        var geo = new FakeGeo([D(0, 0, 1280, 720)], minWidthClamp: 0, onMove: (x, y) => moves.Add((x, y)));

        await SidecarDocker.PlaceAsync(geo, DockSide.Right, requestedWidthPx: 390, dockFraction: 0.5);

        moves[0].Should().Be((0, 0), "no anchor → the pre-9k27.8 primary-origin behavior is preserved");
    }

    [Fact]
    public async Task Place_WithWidthClamp_EndsFlushRight_NotPastEdge()
    {
        // Work area 1280x720, dock right, request 390 — but Chromium clamps the window to 600.
        var geo = new FakeGeo([D(0, 0, 1280, 720)], minWidthClamp: 600);

        var result = await SidecarDocker.PlaceAsync(geo, DockSide.Right, requestedWidthPx: 390, dockFraction: 0.5);

        result.Should().NotBeNull();
        var b = result!.Value.Browser;
        b.Width.Should().Be(600, "the window obeys Chromium's clamp");
        b.X.Should().Be(1280 - 600, "the CLAMPED width is re-placed flush — not left positioned for 390");
        (b.X + b.Width).Should().Be(1280, "right edge flush with the work area, never past the Dock");
        geo.Current.Should().Be(b, "the final applied bounds match the returned rect");
    }

    [Fact]
    public async Task Place_RecoversFromPhantomFirstRead()
    {
        // First read is a phantom from the still-off-screen window (implausible), then the real
        // display appears. The docker must place using the REAL work area, not the phantom.
        var geo = new FakeGeo(
            [D(0, 0, 0, 0), D(0, 0, 1280, 720), D(0, 0, 1280, 720)],
            minWidthClamp: 500);

        var result = await SidecarDocker.PlaceAsync(geo, DockSide.Right, 390, 0.5);

        result.Should().NotBeNull();
        result!.Value.Display.Should().Be(D(0, 0, 1280, 720), "the phantom read was rejected; the real display drove placement");
        var b = result.Value.Browser;
        (b.X + b.Width).Should().Be(1280, "placed flush on the real display");
    }

    [Fact]
    public async Task Place_HugeClamp_NeverFullWidth_StaysOnScreen()
    {
        // Pathological: Chromium reports a near-full-screen width. The result must be capped and
        // fully on-screen — the "opens full-width, pushed off screen" bug must be impossible.
        var geo = new FakeGeo([D(0, 0, 1280, 720)], minWidthClamp: 2000);

        var result = await SidecarDocker.PlaceAsync(geo, DockSide.Right, 390, 0.5);

        var b = result!.Value.Browser;
        var cap = (int)System.Math.Round(1280 * DockGeometry.MaxFraction);
        b.Width.Should().BeLessThanOrEqualTo(cap, "never full-width");
        b.X.Should().BeGreaterThanOrEqualTo(0, "never off the left");
        (b.X + b.Width).Should().BeLessThanOrEqualTo(1280, "never off the right");
        b.X.Should().BeGreaterThan(0, "leaves room for the terminal");
    }

    [Fact]
    public async Task Place_RightDock_NeverOverlapsDock_AcrossWorkAreas()
    {
        // Sweep several work areas (incl. a Dock-on-right-reducing-availWidth and a menu-bar
        // offset) and clamps; the right edge must always land exactly on the work-area edge.
        foreach (var (display, clamp) in new[]
        {
            (D(0, 0, 1280, 720), 500),
            (D(0, 38, 1440, 860), 700),   // menu bar
            (D(0, 0, 1080, 720), 900),    // narrow work area (Dock on right ate width)
            (D(1512, 0, 1512, 982), 600), // secondary display
        })
        {
            var geo = new FakeGeo([display], clamp);
            var result = await SidecarDocker.PlaceAsync(geo, DockSide.Right, 390, 0.5);
            var b = result!.Value.Browser;

            (b.X + b.Width).Should().Be(display.AvailLeft + display.AvailWidth,
                $"flush on {display.AvailWidth}x{display.AvailHeight}@{display.AvailLeft},{display.AvailTop} clamp={clamp}");
            b.X.Should().BeGreaterThanOrEqualTo(display.AvailLeft);
            b.Y.Should().Be(display.AvailTop);
        }
    }

    [Fact]
    public async Task Place_FromHiddenState_UnHidesAndLandsOnScreen()
    {
        // workspace-ynn9: the window starts truly hidden (HideAsync — off-screen + iconified).
        // Docking must UN-hide it AND land it fully inside the work area. Asserts the STATE
        // TRANSITION (hidden → visible-on-screen), not just an initial condition.
        var geo = new FakeGeo([D(0, 0, 1280, 720)], minWidthClamp: 500);
        await geo.HideAsync();
        geo.Hidden.Should().BeTrue("precondition: the parked window is hidden");
        geo.Current.X.Should().BeLessThan(0, "precondition: hidden window sits off-screen");

        var result = await SidecarDocker.PlaceAsync(geo, DockSide.Right, requestedWidthPx: 390, dockFraction: 0.5);

        result.Should().NotBeNull();
        geo.Hidden.Should().BeFalse("docking un-hides the window (NormalizeAsync)");
        var b = result!.Value.Browser;
        b.X.Should().BeGreaterThanOrEqualTo(0, "un-hidden window is on-screen (not off the left)");
        (b.X + b.Width).Should().BeLessThanOrEqualTo(1280, "and fully inside the work area (not off the right)");
        geo.Current.Should().Be(b, "the final applied bounds match the returned rect");
    }

    [Fact]
    public async Task Place_FromHiddenState_NormalizesBeforeMovingOrSizing()
    {
        // The un-hide (Normalize) must come FIRST — CDP forbids combining windowState with bounds,
        // and a still-iconified window can't be positioned. Assert the ORDERING of the seam calls.
        var geo = new FakeGeo([D(0, 0, 1280, 720)], minWidthClamp: 500);
        await geo.HideAsync();
        geo.Calls.Clear(); // ignore the setup Hide; observe only the placement flow

        await SidecarDocker.PlaceAsync(geo, DockSide.Right, 390, 0.5);

        geo.Calls.Should().NotBeEmpty();
        geo.Calls[0].Should().Be("Normalize", "placement un-hides before touching geometry");
        var firstNormalize = geo.Calls.IndexOf("Normalize");
        var firstMove = geo.Calls.IndexOf("Move");
        var firstSet = geo.Calls.IndexOf("Set");
        firstMove.Should().BeGreaterThan(firstNormalize, "the anchor Move happens after the un-hide");
        firstSet.Should().BeGreaterThan(firstNormalize, "the window is sized/placed after the un-hide");
    }

    [Fact]
    public async Task Place_NoPlausibleDisplay_ReturnsNull_PlacementSkipped()
    {
        // Every read is a phantom → the docker refuses to guess and skips placement (leaving the
        // window where it is) rather than docking it off-screen.
        var geo = new FakeGeo([D(0, 0, 0, 0)], minWidthClamp: 500);

        var result = await SidecarDocker.PlaceAsync(geo, DockSide.Right, 390, 0.5);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Place_AnchorsWindowOnScreenBeforeMeasuring()
    {
        // The window starts parked off-screen at -32000; the docker must move it on-screen
        // (anchor) before it ends up placed — so the final rect is never at the parked origin.
        var geo = new FakeGeo([D(0, 0, 1280, 720)], minWidthClamp: 500);

        var result = await SidecarDocker.PlaceAsync(geo, DockSide.Right, 390, 0.5);

        result!.Value.Browser.X.Should().BeGreaterThanOrEqualTo(0, "never left parked off-screen");
        geo.SetCount.Should().BeGreaterThanOrEqualTo(2, "places once, then re-places at the clamped width");
    }
}
