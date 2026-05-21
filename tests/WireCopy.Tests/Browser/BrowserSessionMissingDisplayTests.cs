// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Unit tests for the workspace-j0b8 "missing display" heuristic. When
/// <c>BrowserSession.LaunchBrowserAsync</c> is called with <c>headless=false</c>
/// in an environment that has no X server, Playwright surfaces a
/// <c>TargetClosedException</c> whose message embeds the Chromium
/// "Looks like you launched a headed browser without having a XServer running"
/// banner. The <see cref="BrowserSession.LooksLikeMissingDisplay"/> heuristic
/// picks this case out so <c>GetOrCreatePageAsync</c> can auto-fall-back to
/// headless instead of surfacing "Page navigated mid-load" to the user (the
/// macleans.ca symptom from workspace-hrrf).
/// </summary>
[Trait("Category", "Unit")]
public class BrowserSessionMissingDisplayTests
{
    [Theory]
    // Real shapes captured from logs/wirecopy-20260520.log (workspace-hrrf macleans repro).
    [InlineData("Target page, context or browser has been closed\nBrowser logs:\n\nLooks like you launched a headed browser without having a XServer running.\nSet either 'headless: true' or use 'xvfb-run", true)]
    [InlineData("[err] Missing X server or $DISPLAY", true)]
    [InlineData("Missing X server", true)]
    [InlineData("DISPLAY environment variable is not set; $DISPLAY missing", true)]
    [InlineData("launched a headed browser without having a XServer", true)]
    // Negative cases: real stale-target and DNS failures must NOT be misclassified
    // as a missing-display fallback opportunity (would waste a retry pretending
    // headless mode will help).
    [InlineData("Target page, context or browser has been closed", false)]
    [InlineData("net::ERR_NAME_NOT_RESOLVED", false)]
    [InlineData("Timeout 30000ms exceeded", false)]
    [InlineData("", false)]
    public void LooksLikeMissingDisplay_PicksOutPlaywrightNoDisplayBanner(
        string errorMessage,
        bool expected)
    {
        BrowserSession.LooksLikeMissingDisplay(errorMessage)
            .Should().Be(expected);
    }
}
