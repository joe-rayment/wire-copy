// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// The park state machine (workspace-v7g7), including every macOS transition — testable here
/// because the decision is pure. The field bug these lock down: the old park unconditionally
/// normalized, and prefetch parks twice per article, so the user's own Cmd+M was deminiaturized
/// (popping Chromium to the foreground) moments after every new site open.
/// </summary>
public class ParkPlannerTests
{
    [Theory]
    [InlineData(ParkMode.Corner, true)]
    [InlineData(ParkMode.Corner, false)]
    [InlineData(ParkMode.Offscreen, true)]
    [InlineData(ParkMode.Offscreen, false)]
    public void UserMinimizedWindow_IsNeverTouched(ParkMode mode, bool isMacOS)
    {
        var d = ParkPlanner.Decide("minimized", iconifiedByUs: false, reMinimizeLatch: false, mode, isMacOS);

        d.Action.Should().Be(ParkPlanner.ParkAction.LeaveMinimized);
        d.NormalizeFirst.Should().BeFalse("normalizing is exactly the deminiaturize that popped the window back up");
        d.IconifiedByUsAfter.Should().BeFalse("the minimize stays user-owned");
    }

    [Fact]
    public void UserMinimizedWindow_SwallowsAPendingReMinimizeLatch()
    {
        // The user re-minimized it themselves after the interaction — nothing to do.
        var d = ParkPlanner.Decide("minimized", iconifiedByUs: false, reMinimizeLatch: true, ParkMode.Corner, isMacOS: true);

        d.Action.Should().Be(ParkPlanner.ParkAction.LeaveMinimized);
    }

    [Fact]
    public void OurIconifiedPark_StaysPut_InOffscreenMode()
    {
        var d = ParkPlanner.Decide("minimized", iconifiedByUs: true, reMinimizeLatch: false, ParkMode.Offscreen, isMacOS: false);

        d.Action.Should().Be(ParkPlanner.ParkAction.LeaveMinimized);
        d.IconifiedByUsAfter.Should().BeTrue("still our iconify — a wanted dock may re-summon it (wo4q)");
    }

    [Fact]
    public void OurIconifiedPark_RecoversIntoTheCorner_WhenModeIsCorner()
    {
        // Mode changed mid-profile (or a stale flag): our own iconify may be normalized into
        // the corner tile — this is the ONLY minimized state a park is allowed to leave.
        var d = ParkPlanner.Decide("minimized", iconifiedByUs: true, reMinimizeLatch: false, ParkMode.Corner, isMacOS: false);

        d.Action.Should().Be(ParkPlanner.ParkAction.PlaceCorner);
        d.NormalizeFirst.Should().BeTrue();
        d.IconifiedByUsAfter.Should().BeFalse();
    }

    [Theory]
    [InlineData(ParkMode.Corner)]
    [InlineData(ParkMode.Offscreen)]
    public void CleanupParkAfterInteraction_ReturnsTheWindowToTheUsersMinimize(ParkMode mode)
    {
        var d = ParkPlanner.Decide("normal", iconifiedByUs: false, reMinimizeLatch: true, mode, isMacOS: true);

        d.Action.Should().Be(ParkPlanner.ParkAction.ReMinimize);
        d.IconifiedByUsAfter.Should().BeFalse("we are restoring THEIR state, not creating ours");
    }

    [Theory]
    [InlineData("normal", false)]
    [InlineData("maximized", true)]
    [InlineData("fullscreen", true)]
    [InlineData(null, false)]
    public void CornerMode_PlacesTheTile(string? state, bool expectNormalize)
    {
        var d = ParkPlanner.Decide(state, iconifiedByUs: false, reMinimizeLatch: false, ParkMode.Corner, isMacOS: true);

        d.Action.Should().Be(ParkPlanner.ParkAction.PlaceCorner);
        d.NormalizeFirst.Should().Be(expectNormalize);
        d.IconifiedByUsAfter.Should().BeFalse("the corner tile is a normal visible window");
    }

    [Theory]
    [InlineData(true, false)] // macOS never iconifies in the off-screen hide
    [InlineData(false, true)] // everywhere else the hide iconifies (the ynn9 clamp fix)
    public void OffscreenMode_HidesAndTracksTheIconify(bool isMacOS, bool expectIconifiedByUs)
    {
        var d = ParkPlanner.Decide("normal", iconifiedByUs: false, reMinimizeLatch: false, ParkMode.Offscreen, isMacOS);

        d.Action.Should().Be(ParkPlanner.ParkAction.HideOffscreen);
        d.NormalizeFirst.Should().BeFalse("a normal window needs no normalize — that call was the field bug's engine");
        d.IconifiedByUsAfter.Should().Be(expectIconifiedByUs);
    }

    [Fact]
    public void EffectiveParkMode_ResolvesAutoPerPlatform_AndHonorsExplicitModes()
    {
        new BrowserConfiguration { ParkMode = ParkMode.Corner }.EffectiveParkMode.Should().Be(ParkMode.Corner);
        new BrowserConfiguration { ParkMode = ParkMode.Offscreen }.EffectiveParkMode.Should().Be(ParkMode.Offscreen);

        var expectedAuto = OperatingSystem.IsMacOS() ? ParkMode.Corner : ParkMode.Offscreen;
        new BrowserConfiguration().EffectiveParkMode.Should().Be(expectedAuto, "Auto is the default and resolves per platform");
    }
}
