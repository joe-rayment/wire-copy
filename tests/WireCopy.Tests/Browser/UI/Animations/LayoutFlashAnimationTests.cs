// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Animations;
using Xunit;

namespace WireCopy.Tests.Browser.UI.Animations;

/// <summary>
/// Tests for <see cref="LayoutFlashAnimation"/>. Console output is redirected
/// under xUnit so Play is a no-op — we verify it accepts the documented inputs
/// without throwing.
/// </summary>
public class LayoutFlashAnimationTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    [Fact]
    public void Play_WithNullLabel_IsNoOp()
    {
        LayoutFlashAnimation.Play(layoutName: null!, Palette, terminalWidth: 80, terminalHeight: 24);
    }

    [Fact]
    public void Play_WithEmptyLabel_IsNoOp()
    {
        LayoutFlashAnimation.Play(string.Empty, Palette, terminalWidth: 80, terminalHeight: 24);
    }

    [Fact]
    public void Play_WithLabelWiderThanTerminal_IsNoOp()
    {
        // Long label, narrow terminal — bails before any Console call.
        LayoutFlashAnimation.Play("A very long layout name here", Palette, terminalWidth: 10, terminalHeight: 24);
    }

    [Fact]
    public void Play_WithReasonableLabel_DoesNotThrow()
    {
        LayoutFlashAnimation.Play("Document Order", Palette, terminalWidth: 80, terminalHeight: 24);
    }

    [Fact]
    public void Play_TruncatesOversizedLabel_DoesNotThrow()
    {
        var label = new string('a', 200);
        LayoutFlashAnimation.Play(label, Palette, terminalWidth: 200, terminalHeight: 24);
    }
}
