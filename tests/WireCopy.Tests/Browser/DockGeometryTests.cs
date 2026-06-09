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
}
