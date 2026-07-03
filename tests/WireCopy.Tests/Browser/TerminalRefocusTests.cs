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

    // ---- workspace-9k27.7: refocus debounce — skipped calls must never extend the ----
    // ---- window (the old Exchange-before-check starved non-forced refocus).       ----

    private const long WindowTicks = 500 * TimeSpan.TicksPerMillisecond;

    [Fact]
    public void TryClaimRefocusSlot_FirstClaim_Proceeds_AndStampsTheSlot()
    {
        long slot = 0;
        var t0 = TimeSpan.TicksPerDay; // any non-zero "now"

        TerminalRefocus.TryClaimRefocusSlot(ref slot, t0, force: false, WindowTicks).Should().BeTrue();
        slot.Should().Be(t0, "a successful claim stamps the timestamp");
    }

    [Fact]
    public void TryClaimRefocusSlot_WithinWindow_Skips_WithoutTouchingTheSlot()
    {
        long slot = 0;
        var t0 = TimeSpan.TicksPerDay;
        TerminalRefocus.TryClaimRefocusSlot(ref slot, t0, force: false, WindowTicks);

        var t1 = t0 + (400 * TimeSpan.TicksPerMillisecond);
        TerminalRefocus.TryClaimRefocusSlot(ref slot, t1, force: false, WindowTicks)
            .Should().BeFalse("400ms < the 500ms window");
        slot.Should().Be(t0, "a SKIPPED call must not extend the window — the starvation bug");
    }

    [Fact]
    public void TryClaimRefocusSlot_BurstOfSkippedCalls_CannotStarveALaterClaim()
    {
        long slot = 0;
        var t0 = TimeSpan.TicksPerDay;
        TerminalRefocus.TryClaimRefocusSlot(ref slot, t0, force: false, WindowTicks);

        // A burst of calls inside the window, each of which the OLD code would have
        // stamped — pushing the window endlessly forward.
        for (var ms = 100; ms <= 400; ms += 100)
        {
            TerminalRefocus.TryClaimRefocusSlot(
                    ref slot, t0 + (ms * TimeSpan.TicksPerMillisecond), force: false, WindowTicks)
                .Should().BeFalse();
        }

        // 600ms after the LAST SUCCESSFUL claim the slot must open again.
        TerminalRefocus.TryClaimRefocusSlot(
                ref slot, t0 + (600 * TimeSpan.TicksPerMillisecond), force: false, WindowTicks)
            .Should().BeTrue("the burst of skipped calls never restarted the window");
    }

    [Fact]
    public void TryClaimRefocusSlot_Forced_AlwaysProceeds_AndStamps()
    {
        long slot = 0;
        var t0 = TimeSpan.TicksPerDay;
        TerminalRefocus.TryClaimRefocusSlot(ref slot, t0, force: false, WindowTicks);

        var t1 = t0 + (50 * TimeSpan.TicksPerMillisecond);
        TerminalRefocus.TryClaimRefocusSlot(ref slot, t1, force: true, WindowTicks)
            .Should().BeTrue("the dock path's final refocus must win even inside the window");
        slot.Should().Be(t1, "a forced claim re-stamps so followers debounce against IT");
    }

    [Fact]
    public void TryClaimRefocusSlot_TwoClaimsAtTheSameInstant_OnlyOneWins()
    {
        long slot = 0;
        var t0 = TimeSpan.TicksPerDay;

        TerminalRefocus.TryClaimRefocusSlot(ref slot, t0, force: false, WindowTicks).Should().BeTrue();
        TerminalRefocus.TryClaimRefocusSlot(ref slot, t0, force: false, WindowTicks)
            .Should().BeFalse("the second caller sees the first one's stamp");
    }

    // ---- workspace-fihe: refocus must NOT fire on background parks and must NEVER yank ----
    // ---- focus away from a user-chosen foreign app (the reported focus-war).           ----

    private const string TerminalId = "com.mitchellh.ghostty";
    private const string BrowserId = "org.chromium.Chromium";
    private const string ForeignAppId = "com.anthropic.claude"; // the app the user tried to switch to

    [Fact]
    public void DecideRefocus_BackgroundPark_NeverRefocuses_EvenWhenTerminalIsFrontmost()
    {
        // The reported trigger: a background prefetch completes and parks the (never-shown)
        // off-screen window. weJustActivatedBrowser=false => there is nothing to hand back, so
        // the terminal must NOT be re-activated on a cadence.
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: false, TerminalId, TerminalId)
            .Should().BeFalse();
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: false, BrowserId, TerminalId)
            .Should().BeFalse();
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: false, ForeignAppId, TerminalId)
            .Should().BeFalse("a background park must never touch focus");
    }

    [Fact]
    public void DecideRefocus_UserSwitchedToForeignApp_NeverStealsFocusBack()
    {
        // The exact focus-war: WireCopy raised the browser (weJustActivatedBrowser=true) but by
        // the time the refocus runs the user has moved the Claude app to the front. We must leave
        // them there — never re-activate the terminal over the foreign app.
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: true, ForeignAppId, TerminalId)
            .Should().BeFalse("a user-chosen foreign app must win — this is the workspace-fihe fix");
    }

    [Fact]
    public void DecideRefocus_OurBrowserHoldsFocus_HandsBackToTerminal()
    {
        // The legitimate hand-back: we docked/launched and our own headed Chromium is frontmost.
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: true, BrowserId, TerminalId)
            .Should().BeTrue();
    }

    [Fact]
    public void DecideRefocus_AlreadyOnTerminal_IsANoOp()
    {
        // Focus already sits on the terminal — no need to re-activate it.
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: true, TerminalId, TerminalId)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("missing value")]
    public void DecideRefocus_FrontmostUnreadable_TrustsIntent_AndHandsBack(string? frontmost)
    {
        // osascript denied/failed => we cannot read the frontmost app. Since we know WE just
        // raised the browser, hand focus back best-effort rather than stranding it.
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: true, frontmost, TerminalId)
            .Should().BeTrue();
    }

    [Fact]
    public void DecideRefocus_FocusWarSequence_TerminalNotReRaisedAfterForeignFocus()
    {
        // End-to-end reproduction of the reported sequence, asserted deterministically:
        // 1) dock: browser frontmost, we hand back to the terminal.
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: true, BrowserId, TerminalId)
            .Should().BeTrue("step 1: docking hands focus back to the terminal");

        // 2) the user switches to the Claude app (foreign becomes frontmost).
        // 3) a background prefetch completes and parks the window.
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: false, ForeignAppId, TerminalId)
            .Should().BeFalse("step 3: the background park must NOT re-raise the terminal");

        // 4) even a stray hand-back path firing while Claude is frontmost must not steal focus.
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: true, ForeignAppId, TerminalId)
            .Should().BeFalse("step 4: the user stays in Claude — no focus-war");
    }

    [Theory]
    [InlineData("org.chromium.Chromium", true)]           // Playwright's bundled Chromium — WireCopy's own
    [InlineData("com.google.chrome.for.testing", true)]   // Chrome for Testing (if a channel is set)
    [InlineData("com.google.Chrome", false)]              // the USER's real Chrome — NOT ours, must not steal
    [InlineData("com.mitchellh.ghostty", false)]
    [InlineData("com.anthropic.claude", false)]
    [InlineData("com.apple.Safari", false)]
    [InlineData("com.microsoft.edgemac", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void LooksLikeBrowserBundleId_MatchesOnlyWireCopysOwnChromium_NotTheUsersChrome(string? bundleId, bool expected)
    {
        // Regression guard for the workspace-fihe review: a loose "contains chrome" test matched
        // the user's own com.google.Chrome, so a hand-back stole focus from it. Only WireCopy's
        // bundled Chromium (org.chromium.*) and Chrome for Testing count as "ours".
        TerminalRefocus.LooksLikeBrowserBundleId(bundleId).Should().Be(expected);
    }

    [Fact]
    public void DecideRefocus_UsersOwnChrome_IsTreatedAsForeign_NeverStealsFocus()
    {
        // The exact review finding: user docks WireCopy, then switches to their OWN Google Chrome.
        // A hand-back path fires — it must NOT re-activate the terminal over the user's Chrome.
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: true, "com.google.Chrome", TerminalId)
            .Should().BeFalse("the user's real Chrome is a foreign app, not WireCopy's browser");

        // ...but WireCopy's OWN Chromium holding focus still hands back.
        TerminalRefocus.DecideRefocus(weJustActivatedBrowser: true, "org.chromium.Chromium", TerminalId)
            .Should().BeTrue();
    }
}
