// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Animations;
using Xunit;

namespace WireCopy.Tests.Browser.UI.Animations;

/// <summary>
/// Tests for <see cref="BreathingBarAnimation"/>. Console output is always
/// redirected in xUnit, so Start returns a no-op handle — these tests verify
/// the handle's behavioral contract (idempotent Dispose, no exceptions).
/// </summary>
public class BreathingBarAnimationTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    [Fact]
    public void Start_WithRedirectedOutput_ReturnsDisposableHandle()
    {
        var handle = BreathingBarAnimation.Start(row: 10, col: 10, width: 5, Palette);

        Assert.NotNull(handle);
        handle.Dispose();
    }

    [Fact]
    public void Start_WithZeroWidth_ReturnsNoOpHandleWithoutException()
    {
        var handle = BreathingBarAnimation.Start(row: 0, col: 0, width: 0, Palette);

        handle.Dispose();
    }

    [Fact]
    public void Start_WithNegativeWidth_ReturnsNoOpHandleWithoutException()
    {
        var handle = BreathingBarAnimation.Start(row: 0, col: 0, width: -1, Palette);

        handle.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var handle = BreathingBarAnimation.Start(row: 5, col: 5, width: 3, Palette);

        handle.Dispose();
        handle.Dispose(); // must not throw
    }

    [Fact]
    public void StartForBotChallenge_DoesNotThrow()
    {
        using var handle = BreathingBarAnimation.StartForBotChallenge(Palette);

        Assert.NotNull(handle);
    }
}
