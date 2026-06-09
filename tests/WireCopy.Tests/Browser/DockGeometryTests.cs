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
}
