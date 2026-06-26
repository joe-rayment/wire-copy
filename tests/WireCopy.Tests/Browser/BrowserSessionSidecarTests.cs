// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Unit tests for the sidecar intent's configured default (workspace-exbz):
/// the sticky dock intent starts in the configured state, BEFORE any browser
/// is launched, so the first headed launch docks (sidecar on) or minimizes
/// (sidecar off). The live launch behavior itself is covered by the Xvfb test
/// in <see cref="BrowserWindowDockIntegrationTests"/>.
/// </summary>
public class BrowserSessionSidecarTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WantsSidecar_ReflectsConfiguredDefault(bool sidecar)
    {
        var config = Options.Create(new BrowserConfiguration { Sidecar = sidecar });
        using var session = new BrowserSession(
            config,
            NullLogger<BrowserSession>.Instance,
            Substitute.For<ICookieManager>());

        session.WantsSidecar.Should().Be(sidecar);
    }

    [Fact]
    public void Sidecar_DefaultsOff_ParkedImmersive()
    {
        // workspace-75ng: the headed browser launches PARKED off-screen by default so it never
        // pops up or steals keyboard focus at startup (the macOS/Ghostty focus-steal). 'O'
        // brings it on-screen to dock; opting into Sidecar=true restores auto-docking.
        new BrowserConfiguration().Sidecar.Should().BeFalse(
            "the default reading mode is immersive/parked; O brings the sidecar on-screen");
    }
}
