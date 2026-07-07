// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Drives the corner-park placement (workspace-v7g7) against the fake window controller,
/// simulating the macOS conditions Xvfb can't reproduce: an OS that clamps/offsets applied
/// positions, Chromium's platform-minimum window size, the workspace-r2we JS/CDP scale skew,
/// and a user who drags or resizes the parked tile. Proves the tile always lands FLUSH in the
/// bottom-right of the work area and that background parks never fight the user — in this Linux
/// environment, no Mac required.
/// </summary>
public class CornerParkerTests
{
    private static SidecarGeometry.DisplayInfo D(int l, int t, int w, int h) => new(l, t, w, h);

    // ---- Pure planner math -------------------------------------------------------------

    [Fact]
    public void PlanCornerWindow_PlacesFlushBottomRight()
    {
        var rect = SidecarGeometry.PlanCornerWindow(D(0, 0, 1600, 900), 800, 600, margin: 8);

        rect.Should().Be(new TerminalTiling.WindowRect(1600 - 800 - 8, 900 - 600 - 8, 800, 600));
    }

    [Fact]
    public void PlanCornerWindow_HonorsWorkAreaOrigin()
    {
        // Secondary display / menu-bar offset: the corner is the WORK AREA's corner.
        var rect = SidecarGeometry.PlanCornerWindow(D(2560, 25, 1920, 1055), 800, 600, margin: 8);

        rect.Should().Be(new TerminalTiling.WindowRect(2560 + 1920 - 800 - 8, 25 + 1055 - 600 - 8, 800, 600));
    }

    [Fact]
    public void PlanCornerWindow_ShrinksToASmallWorkArea()
    {
        var rect = SidecarGeometry.PlanCornerWindow(D(0, 0, 400, 300), 800, 600, margin: 8);

        rect.Width.Should().Be(400 - 16, "the tile must fit inside the work area minus margins");
        rect.Height.Should().Be(300 - 16);
        rect.X.Should().Be(8);
        rect.Y.Should().Be(8);
    }

    [Fact]
    public void PlanCornerWindow_ReplansFlushWithTheActualClampedSize()
    {
        // Chromium clamped our 800x600 request up to 900x650: the corner must stay flush.
        var rect = SidecarGeometry.PlanCornerWindow(D(0, 0, 1600, 900), 800, 600, margin: 8, actualSize: (900, 650));

        rect.Should().Be(new TerminalTiling.WindowRect(1600 - 900 - 8, 900 - 650 - 8, 900, 650));
    }

    // ---- Placement flow ----------------------------------------------------------------

    [Fact]
    public async Task Place_PutsTheTileFlushInTheBottomRightCorner()
    {
        var geo = new FakeGeo([D(0, 0, 1600, 900)], minWidthClamp: 0);

        var result = await CornerParker.PlaceAsync(geo, 800, 600, margin: 8, lastApplied: null, respectDrift: false);

        result!.Value.Rect.Should().Be(new TerminalTiling.WindowRect(792, 292, 800, 600));
        geo.Current.Should().Be(result!.Value.Rect, "the window really is where the caller will remember it");
    }

    [Fact]
    public async Task Place_ReplansFlushWhenChromiumClampsTheSize()
    {
        // Chromium refuses widths below 900 (fake platform minimum): the first placement comes
        // back wider than asked, and the re-plan must land the RIGHT edge flush again.
        var geo = new FakeGeo([D(0, 0, 1600, 900)], minWidthClamp: 900);

        var result = await CornerParker.PlaceAsync(geo, 800, 600, margin: 8, lastApplied: null, respectDrift: false);

        result!.Value.Rect.Width.Should().Be(900);
        (result.Value.Rect.X + result.Value.Rect.Width).Should().Be(1600 - 8, "flush right despite the clamp");
    }

    [Fact]
    public async Task Place_NudgesAwayAnOsPositionOffset()
    {
        // The OS applies every position 30px higher than asked (frame-inset style offset).
        // The one corrective nudge must cancel it so the tile still lands on the plan.
        var geo = new FakeGeo(
            [D(0, 0, 1600, 900)],
            minWidthClamp: 0,
            setTransform: r => r with { Y = r.Y - 30 });

        var result = await CornerParker.PlaceAsync(geo, 800, 600, margin: 8, lastApplied: null, respectDrift: false);

        result!.Value.Rect.Should().Be(new TerminalTiling.WindowRect(792, 292, 800, 600));
    }

    [Fact]
    public async Task Place_AcceptsTheOsAnswerWhenItHardClamps()
    {
        // The OS hard-clamps any Y below 280 (a Dock/taskbar we did not know about): the nudge
        // cannot win, and the final rect is the OS's flush answer — never an infinite loop.
        var geo = new FakeGeo(
            [D(0, 0, 1600, 900)],
            minWidthClamp: 0,
            setTransform: r => r with { Y = System.Math.Min(r.Y, 280) });

        var result = await CornerParker.PlaceAsync(geo, 800, 600, margin: 8, lastApplied: null, respectDrift: false);

        result!.Value.Rect.Y.Should().Be(280);
        result.Value.Rect.X.Should().Be(792);
    }

    [Fact]
    public async Task Place_PrefersTheLaunchCapturedRealDisplay()
    {
        // The emulated fetch page's JS screen metrics LIE (they report the 1280x720 viewport,
        // the workspace-r2we skew). The session captures the real display pre-emulation and
        // passes it as an override — the tile must land in the REAL corner, ignoring JS reads.
        var geo = new FakeGeo([D(0, 0, 1280, 720)], minWidthClamp: 0);

        var result = await CornerParker.PlaceAsync(
            geo, 800, 600, margin: 8, lastApplied: null, respectDrift: false,
            displayOverride: D(0, 0, 1600, 900));

        result!.Value.Rect.Should().Be(new TerminalTiling.WindowRect(792, 292, 800, 600));
    }

    [Fact]
    public async Task Place_AnchorsOnScreenWhenTheDisplayReadIsPhantom()
    {
        // A window parked far off-screen reports no plausible work area; the parker must anchor
        // it on-screen once and re-read (the dock's known trap).
        var geo = new FakeGeo(
            [null, null, null, null, null, null, D(0, 0, 1600, 900), D(0, 0, 1600, 900)],
            minWidthClamp: 0);

        var result = await CornerParker.PlaceAsync(geo, 800, 600, margin: 8, lastApplied: null, respectDrift: false);

        geo.Calls.Should().Contain("Move", "the phantom read forces a one-time on-screen anchor");
        result!.Value.Rect.Should().Be(new TerminalTiling.WindowRect(792, 292, 800, 600));
    }

    [Fact]
    public async Task Place_SkipsWhenNoPlausibleDisplayEverAppears()
    {
        var geo = new FakeGeo([D(0, 0, 0, 0)], minWidthClamp: 0);

        var result = await CornerParker.PlaceAsync(geo, 800, 600, margin: 8, lastApplied: null, respectDrift: false);

        result.Should().BeNull("placement is skipped rather than guessed");
        geo.SetCount.Should().Be(0);
    }

    // ---- Never fight the user ----------------------------------------------------------

    [Fact]
    public async Task Place_IsAReadOnlyNoOpWhenTheTileIsWhereWePutIt()
    {
        // Background prefetch parks run twice per article: once placed, they must be cheap
        // reads that never re-place (or visibly touch) the window.
        var placed = new TerminalTiling.WindowRect(792, 292, 800, 600);
        var geo = new FakeGeo([D(0, 0, 1600, 900)], minWidthClamp: 0) { Current = placed };

        var result = await CornerParker.PlaceAsync(geo, 800, 600, margin: 8, lastApplied: placed, respectDrift: false);

        result!.Value.Rect.Should().Be(placed);
        result.Value.Settled.Should().BeTrue("observing the tile where we left it arms the respect-drift flag");
        geo.SetCount.Should().Be(0);
        geo.Calls.Should().NotContain("Move");
    }

    [Fact]
    public async Task Place_RespectsATileTheUserDraggedOrResized()
    {
        // The tile was observed settled once (respectDrift armed); now the user dragged it to
        // the top-left and made it smaller. The next background park must adopt THEIR placement,
        // not snap it back to the corner.
        var ours = new TerminalTiling.WindowRect(792, 292, 800, 600);
        var theirs = new TerminalTiling.WindowRect(40, 60, 500, 400);
        var geo = new FakeGeo([D(0, 0, 1600, 900)], minWidthClamp: 0) { Current = theirs };

        var result = await CornerParker.PlaceAsync(geo, 800, 600, margin: 8, lastApplied: ours, respectDrift: true);

        result!.Value.Rect.Should().Be(theirs, "the user's placement wins until an on-screen flow resets the memory");
        result.Value.Settled.Should().BeTrue();
        geo.SetCount.Should().Be(0);
    }

    [Fact]
    public async Task Place_CorrectsLaunchWmDriftBeforeTheFirstSettledObservation()
    {
        // The gate's field finding: openbox re-placed the freshly mapped window OVER the early
        // corner park (312,49 at 1288x851 — nearly full-screen). Until a park has OBSERVED the
        // tile settled where we left it, such a drift is WM interference and must be corrected —
        // NOT adopted as the user's placement.
        var early = new TerminalTiling.WindowRect(792, 292, 800, 600);
        var wmDrift = new TerminalTiling.WindowRect(312, 49, 1288, 851);
        var geo = new FakeGeo([D(0, 0, 1600, 900)], minWidthClamp: 0) { Current = wmDrift };

        var result = await CornerParker.PlaceAsync(geo, 800, 600, margin: 8, lastApplied: early, respectDrift: false);

        result!.Value.Rect.Should().Be(new TerminalTiling.WindowRect(792, 292, 800, 600), "the WM's map-time placement is corrected");
        result.Value.Settled.Should().BeFalse("a full placement must be confirmed by the next park before drifts count as the user's");
    }
}
