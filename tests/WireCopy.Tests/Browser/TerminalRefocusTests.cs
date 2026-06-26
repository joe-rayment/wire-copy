// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Cross-platform unit tests for the macOS terminal-refocus command construction
/// (workspace-75ng). The osascript itself only runs on a Mac (and is confirmed there by
/// the user), but the DECISION — capture the frontmost bundle id once, then activate THAT
/// app, falling back to a TERM_PROGRAM name map that no longer guesses Apple Terminal — is
/// pure and fully testable here, the same way <see cref="DockGeometry"/> is.
/// </summary>
public class TerminalRefocusTests
{
    [Fact]
    public void MapTermProgramToAppName_Ghostty_MapsToGhostty_NotTerminal()
    {
        // The bug: ghostty fell through to "Terminal" and focus returned to the wrong app.
        TerminalRefocus.MapTermProgramToAppName("ghostty").Should().Be("Ghostty");
        TerminalRefocus.MapTermProgramToAppName("Ghostty").Should().Be("Ghostty");
    }

    [Theory]
    [InlineData("iTerm.app", "iTerm2")]
    [InlineData("Apple_Terminal", "Terminal")]
    [InlineData("WezTerm", "WezTerm")]
    [InlineData("Alacritty", "Alacritty")]
    [InlineData("kitty", "kitty")]
    [InlineData("WarpTerminal", "Warp")]
    [InlineData("Warp", "Warp")]
    public void MapTermProgramToAppName_KnownTerminals_MapToAppNames(string termProgram, string expected)
    {
        TerminalRefocus.MapTermProgramToAppName(termProgram).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some-unknown-terminal")]
    public void MapTermProgramToAppName_UnknownOrEmpty_ReturnsNull_NeverGuessesTerminal(string? termProgram)
    {
        // Critical regression guard: an unknown terminal must NOT resolve to "Terminal",
        // because activating the wrong app is exactly the focus-steal bug (workspace-75ng).
        TerminalRefocus.MapTermProgramToAppName(termProgram).Should().BeNull();
    }

    [Fact]
    public void BuildActivateScript_PrefersBundleId_OverAppName()
    {
        var script = TerminalRefocus.BuildActivateScript("com.mitchellh.ghostty", "Terminal");

        script.Should().Be("tell application id \"com.mitchellh.ghostty\" to activate");
        script.Should().NotContain("Terminal", "the captured bundle id wins over any name guess");
    }

    [Fact]
    public void BuildActivateScript_FallsBackToAppName_WhenNoBundleId()
    {
        TerminalRefocus.BuildActivateScript(null, "iTerm2")
            .Should().Be("tell application \"iTerm2\" to activate");
        TerminalRefocus.BuildActivateScript("   ", "iTerm2")
            .Should().Be("tell application \"iTerm2\" to activate");
    }

    [Fact]
    public void BuildActivateScript_ReturnsNull_WhenNeitherKnown()
    {
        // No captured id AND an unrecognised terminal → no command, so we never refocus the
        // wrong app.
        TerminalRefocus.BuildActivateScript(null, null).Should().BeNull();
    }

    [Fact]
    public void BuildActivateScript_EscapesQuotesAndBackslashes()
    {
        // Defensive: a value containing a quote must not break out of the AppleScript string.
        var script = TerminalRefocus.BuildActivateScript(null, "weird\"name\\x");

        script.Should().Be("tell application \"weird\\\"name\\\\x\" to activate");
    }

    [Fact]
    public void ResolveActivateScript_UsesCapturedBundleId_WhenPresent()
    {
        TerminalRefocus.ResolveActivateScript("com.apple.Terminal", "ghostty")
            .Should().Be("tell application id \"com.apple.Terminal\" to activate");
    }

    [Fact]
    public void ResolveActivateScript_FallsBackToTermProgramMap_WhenNoBundleId()
    {
        // Ghostty with no captured id still resolves to Ghostty, never Apple Terminal.
        TerminalRefocus.ResolveActivateScript(null, "ghostty")
            .Should().Be("tell application \"Ghostty\" to activate");
    }

    [Fact]
    public void ResolveActivateScript_Null_WhenNoBundleIdAndUnknownTerminal()
    {
        TerminalRefocus.ResolveActivateScript(null, "mystery-term").Should().BeNull();
    }

    [Theory]
    [InlineData("com.mitchellh.ghostty", true)]
    [InlineData("com.apple.Terminal", true)]
    [InlineData("dev.warp.Warp-Stable", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("missing value", false)]
    [InlineData("execution error: not allowed", false)]
    [InlineData("noseparator", false)]
    public void IsUsableBundleId_AcceptsRealIds_RejectsErrorsAndBlanks(string? value, bool expected)
    {
        TerminalRefocus.IsUsableBundleId(value).Should().Be(expected);
    }

    [Fact]
    public void CaptureFrontmostBundleIdScript_QueriesFrontmostProcess()
    {
        // Sanity-check the capture expression targets the frontmost app's bundle id — this is
        // what makes the capture terminal-agnostic.
        TerminalRefocus.CaptureFrontmostBundleIdScript
            .Should().Contain("frontmost is true")
            .And.Contain("bundle identifier");
    }
}
