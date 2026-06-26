// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Cross-platform unit tests for the sidecar placement math (workspace-75ng). These reproduce
/// the macOS conditions an Xvfb integration test can't — Retina-scaled work areas, a menu-bar
/// offset, a Dock on the right, and Chromium clamping the requested phone width UP to a large
/// value — and assert the two guarantees that were violated in the field: the docked window is
/// NEVER full-width, and NEVER off-screen / past the Dock.
/// </summary>
public class SidecarGeometryTests
{
    private static SidecarGeometry.DisplayInfo Display(int l, int t, int w, int h) => new(l, t, w, h);

    [Fact]
    public void PlanDockedWindow_RightDock_PinsFlushToWorkAreaRightEdge()
    {
        var rect = SidecarGeometry.PlanDockedWindow(Display(0, 0, 1280, 720), DockSide.Right, 390, 0.5, actualWidth: null);

        rect.Width.Should().Be(390, "the requested phone width fits within the cap");
        rect.X.Should().Be(1280 - 390, "right dock pins the right edge to the work-area edge");
        rect.Y.Should().Be(0);
        rect.Height.Should().Be(720, "the sidecar is full work-area height");
        (rect.X + rect.Width).Should().Be(1280, "right edge is flush, never past the Dock");
    }

    [Fact]
    public void PlanDockedWindow_LeftDock_PinsToLeftEdge()
    {
        var rect = SidecarGeometry.PlanDockedWindow(Display(0, 0, 1280, 720), DockSide.Left, 390, 0.5, actualWidth: null);

        rect.X.Should().Be(0);
        rect.Width.Should().Be(390);
    }

    [Fact]
    public void PlanDockedWindow_UsesActualClampedWidth_StillFlushRight()
    {
        // The user's scenario: requested 390, Chromium clamps the real window UP to 600. The
        // re-plan must place the ACTUAL 600-wide window flush — not leave it positioned for 390
        // (which pushed the right edge ~210px past the Dock).
        var rect = SidecarGeometry.PlanDockedWindow(Display(0, 0, 1280, 720), DockSide.Right, 390, 0.5, actualWidth: 600);

        rect.Width.Should().Be(600);
        rect.X.Should().Be(1280 - 600);
        (rect.X + rect.Width).Should().Be(1280, "the real (clamped) width is pinned flush, not past the edge");
    }

    [Fact]
    public void PlanDockedWindow_HugeReadbackWidth_IsCapped_NeverFullWidth()
    {
        // THE regression guard: a bogus/huge read-back width (e.g. the window briefly reporting
        // near-screen-width) must NOT produce a full-width window — it is capped at MaxFraction
        // of the work area. This is exactly the "opens full-width" bug.
        var rect = SidecarGeometry.PlanDockedWindow(Display(0, 0, 1280, 720), DockSide.Right, 390, 0.5, actualWidth: 1280);

        var cap = (int)System.Math.Round(1280 * DockGeometry.MaxFraction);
        rect.Width.Should().BeLessThanOrEqualTo(cap, "the sidecar is capped and can never fill the screen");
        rect.Width.Should().Be(cap);
        rect.X.Should().Be(1280 - cap);
        rect.X.Should().BeGreaterThan(0, "a capped window still leaves room for the terminal — not full-width");
    }

    [Fact]
    public void PlanDockedWindow_AlwaysFullyOnScreen_RightDock()
    {
        // Across a spread of (possibly bad) actual widths, the window is ALWAYS inside the work
        // area — never off-screen, never spilling past the right edge.
        foreach (var actual in new int?[] { null, 100, 390, 600, 1000, 1280, 5000 })
        {
            var d = Display(0, 0, 1280, 720);
            var rect = SidecarGeometry.PlanDockedWindow(d, DockSide.Right, 390, 0.5, actual);

            rect.X.Should().BeGreaterThanOrEqualTo(d.AvailLeft, $"actual={actual}: left within work area");
            (rect.X + rect.Width).Should().BeLessThanOrEqualTo(d.AvailLeft + d.AvailWidth, $"actual={actual}: right edge within work area");
            rect.Y.Should().Be(d.AvailTop);
            rect.Height.Should().BeLessThanOrEqualTo(d.AvailHeight);
        }
    }

    [Fact]
    public void PlanDockedWindow_RespectsMenuBarAndDockOffsets()
    {
        // macOS menu bar (availTop=38) + Dock on the right reducing availWidth, with the work
        // area not starting at the origin (secondary display at availLeft=1512).
        var d = Display(1512, 38, 1000, 944);
        var rect = SidecarGeometry.PlanDockedWindow(d, DockSide.Right, 390, 0.5, actualWidth: 600);

        rect.X.Should().Be(1512 + 1000 - 600, "flush to the work-area right edge on the secondary display");
        rect.Y.Should().Be(38, "top respects the menu bar");
        rect.Height.Should().Be(944);
        (rect.X + rect.Width).Should().Be(1512 + 1000);
    }

    [Fact]
    public void PlanDockedWindow_FractionFallback_WhenNoRequestedWidth()
    {
        // DockWidthPx <= 0 → fall back to the fraction of the work area (still capped).
        var rect = SidecarGeometry.PlanDockedWindow(Display(0, 0, 1000, 800), DockSide.Right, 0, 0.5, actualWidth: null);

        rect.Width.Should().Be(500, "0.5 of a 1000px work area");
        rect.X.Should().Be(500);
    }

    [Fact]
    public void DisplayInfo_Plausibility()
    {
        Display(0, 0, 1280, 720).IsPlausible.Should().BeTrue();
        Display(0, 0, 0, 0).IsPlausible.Should().BeFalse("a zero-size read is a parked/off-screen phantom");
        Display(0, 0, 50, 50).IsPlausible.Should().BeFalse("below the minimum plausible dimension");
    }

    [Fact]
    public void PlanTerminalTile_RightDock_ComplementsTheBrowser()
    {
        var tile = SidecarGeometry.PlanTerminalTile(Display(0, 0, 1280, 720), DockSide.Right, browserWidth: 600);

        tile.Should().NotBeNull();
        tile!.Value.X.Should().Be(0, "terminal takes the left slice when the browser is on the right");
        tile.Value.Width.Should().Be(1280 - 600);
        tile.Value.Height.Should().Be(720);
    }
}
