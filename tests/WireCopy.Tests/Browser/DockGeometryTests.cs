// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Unit tests for the docked-window geometry (workspace-v7mb): configurable side and
/// split fraction. The live CDP positioning path is covered separately by the Xvfb
/// integration test; this nails the math the integration test can't vary.
/// </summary>
public class DockGeometryTests
{
    [Fact]
    public void Compute_DefaultRightHalf_MatchesLegacyBehaviour()
    {
        // The pre-v7mb code pinned the window to screenWidth/2; defaults must preserve that.
        var (left, top, width, height) = DockGeometry.Compute(1280, 800, DockSide.Right, 0.5);
        left.Should().Be(640);
        top.Should().Be(0);
        width.Should().Be(640);
        height.Should().Be(800);
    }

    [Fact]
    public void Compute_LeftHalf_PinsToLeftEdge()
    {
        var (left, top, width, height) = DockGeometry.Compute(1280, 800, DockSide.Left, 0.5);
        left.Should().Be(0);
        top.Should().Be(0);
        width.Should().Be(640);
        height.Should().Be(800);
    }

    [Theory]
    [InlineData(0.6, DockSide.Right, 512, 768)]   // 1280*0.6 = 768 wide, left = 1280-768
    [InlineData(0.6, DockSide.Left, 0, 768)]
    [InlineData(0.3, DockSide.Right, 896, 384)]   // 1280*0.3 = 384 wide, left = 1280-384
    public void Compute_RespectsFraction(double fraction, DockSide side, int expectedLeft, int expectedWidth)
    {
        var (left, _, width, _) = DockGeometry.Compute(1280, 800, side, fraction);
        left.Should().Be(expectedLeft);
        width.Should().Be(expectedWidth);
    }

    [Theory]
    [InlineData(0.05)]  // below MinFraction
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Compute_ClampsTinyFractionToMinimum(double fraction)
    {
        var (_, _, width, _) = DockGeometry.Compute(1280, 800, DockSide.Right, fraction);
        width.Should().Be((int)(1280 * DockGeometry.MinFraction)); // 256
    }

    [Theory]
    [InlineData(0.95)]  // above MaxFraction
    [InlineData(1.0)]
    [InlineData(5.0)]
    public void Compute_ClampsHugeFractionToMaximum(double fraction)
    {
        var (_, _, width, _) = DockGeometry.Compute(1280, 800, DockSide.Right, fraction);
        width.Should().Be((int)(1280 * DockGeometry.MaxFraction)); // 1024
    }

    [Fact]
    public void Compute_NonPositiveScreen_FallsBackToDefaultResolution()
    {
        var (left, top, width, height) = DockGeometry.Compute(0, 0, DockSide.Right, 0.5);
        left.Should().Be(640);
        top.Should().Be(0);
        width.Should().Be(640);
        height.Should().Be(800);
    }

    [Fact]
    public void Compute_TallScreen_SpansFullHeight()
    {
        var (_, top, _, height) = DockGeometry.Compute(2560, 1440, DockSide.Right, 0.5);
        top.Should().Be(0);
        height.Should().Be(1440);
    }

    // workspace-nqqs: multi-monitor work-area origin. availLeft/availTop offset the
    // window into the display it actually lives on (virtual-screen coordinates).

    [Fact]
    public void Compute_SecondaryDisplayToTheRight_OffsetsByAvailLeft()
    {
        // Secondary 1920-wide display whose work area starts at x=2560 (right of a
        // 2560-wide primary). Docking right should land near the secondary's right edge.
        var (left, top, width, height) = DockGeometry.Compute(1920, 1080, 2560, 0, DockSide.Right, 0.5);
        width.Should().Be(960);
        left.Should().Be(2560 + 960, "right-dock = display origin + (width - halfWidth)");
        top.Should().Be(0);
        height.Should().Be(1080);
    }

    [Fact]
    public void Compute_SecondaryDisplayToTheLeft_PinsToDisplayOrigin()
    {
        // Secondary display whose work area starts at x=-1920 (left of primary).
        var (left, _, width, _) = DockGeometry.Compute(1920, 1080, -1920, 0, DockSide.Left, 0.5);
        left.Should().Be(-1920, "left-dock pins to the display's own work-area origin");
        width.Should().Be(960);
    }

    [Fact]
    public void Compute_AvailTopOffset_AvoidsTopBarOverlap()
    {
        // A top menu/taskbar pushes the work area down (availTop = 25, e.g. macOS menu bar).
        var (_, top, _, height) = DockGeometry.Compute(1440, 875, 0, 25, DockSide.Right, 0.5);
        top.Should().Be(25, "the window starts below the reserved top bar");
        height.Should().Be(875);
    }

    [Fact]
    public void Compute_ZeroOrigin_MatchesLegacyOverload()
    {
        // The 4-arg overload must equal the 6-arg form with a zero origin.
        var legacy = DockGeometry.Compute(1280, 800, DockSide.Right, 0.5);
        var explicitOrigin = DockGeometry.Compute(1280, 800, 0, 0, DockSide.Right, 0.5);
        explicitOrigin.Should().Be(legacy);
    }

    // workspace-8fkv: shrink the app's render width to the uncovered columns when docked
    // so the browser sits beside content instead of covering it.

    [Theory]
    [InlineData(200, 0.5, 99)]   // left half minus a 1-col seam gutter
    [InlineData(200, 0.6, 79)]   // browser takes 60% → app gets 40% (80) - 1
    [InlineData(200, 0.3, 139)]  // browser takes 30% → app gets 70% (140) - 1
    public void UncoveredWidth_RightDock_ShrinksToComplementOfFraction(int fullWidth, double fraction, int expected)
    {
        DockGeometry.UncoveredWidth(fullWidth, DockSide.Right, fraction).Should().Be(expected);
    }

    [Fact]
    public void UncoveredWidth_ClampsFractionBeforeComplementing()
    {
        // Out-of-range fractions clamp to [MinFraction, MaxFraction], so they yield the
        // same width as the boundary fraction (asserted by equality rather than a
        // hardcoded float-derived magic number, which is off-by-one due to 1.0-0.8 etc.).
        DockGeometry.UncoveredWidth(200, DockSide.Right, 0.95)
            .Should().Be(DockGeometry.UncoveredWidth(200, DockSide.Right, DockGeometry.MaxFraction));
        DockGeometry.UncoveredWidth(200, DockSide.Right, 0.05)
            .Should().Be(DockGeometry.UncoveredWidth(200, DockSide.Right, DockGeometry.MinFraction));

        // ...and the magnitudes are sane: a large browser fraction leaves the app a small
        // slice (~0.2*200), a small one leaves it most of the screen (~0.8*200).
        DockGeometry.UncoveredWidth(200, DockSide.Right, 0.95).Should().BeInRange(35, 41);
        DockGeometry.UncoveredWidth(200, DockSide.Right, 0.05).Should().BeInRange(155, 161);
    }

    [Fact]
    public void UncoveredWidth_LeftDock_ReturnsFullWidth_UntilOffsetSupported()
    {
        // Left-dock needs a content offset (not just a narrower width); until that exists
        // the helper leaves the width untouched rather than pushing content UNDER the browser.
        DockGeometry.UncoveredWidth(200, DockSide.Left, 0.5).Should().Be(200);
    }

    [Fact]
    public void UncoveredWidth_NeverShrinksBelowFloor_OnTinyTerminals()
    {
        // A very narrow terminal must not collapse to an unusable sliver.
        var result = DockGeometry.UncoveredWidth(30, DockSide.Right, 0.5);
        result.Should().BeLessThanOrEqualTo(30);
        result.Should().BeGreaterThanOrEqualTo(Math.Min(DockGeometry.MinDockedRenderWidth, 30));
    }
}
