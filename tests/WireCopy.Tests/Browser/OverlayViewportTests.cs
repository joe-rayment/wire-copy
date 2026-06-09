// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.UI;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Unit tests for the dock-aware overlay viewport (workspace-s621). Joins the
/// console serial collection because the provider is process-global state.
/// </summary>
[Collection("ConsoleSerial")]
public class OverlayViewportTests
{
    [Fact]
    public void WithoutProvider_FallsBackToFullWindow()
    {
        OverlayViewport.SetProvider(null);
        OverlayViewport.Left.Should().Be(0);
        OverlayViewport.Width.Should().BeGreaterThan(0);
    }

    [Fact]
    public void WithProvider_ReturnsDockedRegion()
    {
        try
        {
            OverlayViewport.SetProvider(() => (76, 74));
            OverlayViewport.Left.Should().Be(76);
            OverlayViewport.Width.Should().Be(74);
        }
        finally
        {
            OverlayViewport.SetProvider(null);
        }
    }

    [Fact]
    public void ThrowingProvider_FallsBackToFullWindow()
    {
        try
        {
            OverlayViewport.SetProvider(() => throw new InvalidOperationException("boom"));
            OverlayViewport.Left.Should().Be(0);
            OverlayViewport.Width.Should().BeGreaterThan(0);
        }
        finally
        {
            OverlayViewport.SetProvider(null);
        }
    }

    [Fact]
    public void LeftDockGeometry_PutsOverlaysInTheRightColumns()
    {
        // The provider the orchestrator wires uses DockedContentLayout — prove the
        // composed shape: left dock pushes the overlay origin past the seam so
        // modals land beside the browser, never under it (workspace-s621).
        var (offset, width) = DockGeometry.DockedContentLayout(150, DockSide.Left, 0.5);
        try
        {
            OverlayViewport.SetProvider(() => (offset, width));
            OverlayViewport.Left.Should().BeGreaterThan(70, "the browser covers the left half");
            (OverlayViewport.Left + OverlayViewport.Width).Should().BeLessThanOrEqualTo(150);
        }
        finally
        {
            OverlayViewport.SetProvider(null);
        }
    }
}
