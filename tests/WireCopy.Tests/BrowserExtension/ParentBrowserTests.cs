// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.BrowserExtension;
using Xunit;

namespace WireCopy.Tests.BrowserExtension;

/// <summary>
/// The pure path-to-browser mapping behind "which browser launched the host" (workspace-m7aml).
/// </summary>
[Trait("Category", "Unit")]
public sealed class ParentBrowserTests
{
    // Guards:   Each supported browser's real executable path (Linux and macOS) maps to the right browser id, and anything unrecognised or empty falls back to "browser".
    // Fails if: The match order changes (e.g. "chrome" tested before "vivaldi"/"brave"/"msedge"/"chromium"), so a Chromium-based browser is shown as plain Chrome in Settings.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter ParentBrowser_MapsExecutablePathToBrowserId
    [Theory]
    [InlineData("/opt/vivaldi/vivaldi-bin", "vivaldi")]
    [InlineData("/opt/google/chrome/chrome", "chrome")]
    [InlineData("/Applications/Chromium.app/Contents/MacOS/Chromium", "chromium")]
    [InlineData("Google Chrome", "chrome")]
    [InlineData("/opt/microsoft/msedge/msedge", "edge")]
    [InlineData("/opt/brave.com/brave/brave", "brave")]
    [InlineData("/usr/bin/python3", "browser")]
    [InlineData("", "browser")]
    public void ParentBrowser_MapsExecutablePathToBrowserId(string exePath, string expected)
    {
        ParentBrowser.FromExecutablePath(exePath).Should().Be(expected);
    }

    // Guards:   A parent process that could not be identified (null path) is reported as the generic "browser", not an error.
    // Fails if: FromExecutablePath throws or returns null for a missing path.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter ParentBrowser_TreatsAMissingPathAsGenericBrowser
    [Fact]
    public void ParentBrowser_TreatsAMissingPathAsGenericBrowser()
    {
        ParentBrowser.FromExecutablePath(null).Should().Be("browser");
    }
}
