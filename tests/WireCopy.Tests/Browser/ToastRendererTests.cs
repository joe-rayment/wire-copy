// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for ToastRenderer.
/// ToastRenderer writes directly to Console using cursor positioning,
/// so these tests verify it handles various inputs without throwing.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class ToastRendererTests
{
    private readonly ThemePalette _palette = BuiltInThemes.Get(ThemeName.Phosphor);

    [Theory]
    [InlineData(ToastType.Info)]
    [InlineData(ToastType.Success)]
    [InlineData(ToastType.Error)]
    [InlineData(ToastType.Celebration)]
    public void RenderToast_AllTypes_DoesNotThrow(ToastType type)
    {
        var toast = new ToastNotification { Type = type, Message = "Test message" };

        var act = () => ToastRenderer.RenderToast(toast, _palette, 80);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderToast_WithDetail_DoesNotThrow()
    {
        var toast = new ToastNotification
        {
            Type = ToastType.Info,
            Message = "Cache warmed",
            Detail = "24/24 articles"
        };

        var act = () => ToastRenderer.RenderToast(toast, _palette, 120);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderToast_LongMessage_DoesNotThrow()
    {
        var toast = new ToastNotification
        {
            Type = ToastType.Success,
            Message = "This is a very long message that should be truncated to fit"
        };

        var act = () => ToastRenderer.RenderToast(toast, _palette, 80);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderToast_NarrowTerminal_DoesNotThrow()
    {
        var toast = new ToastNotification
        {
            Type = ToastType.Error,
            Message = "Error",
            Detail = "timeout"
        };

        var act = () => ToastRenderer.RenderToast(toast, _palette, 20);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderToast_EmptyDetail_DoesNotThrow()
    {
        var toast = new ToastNotification
        {
            Type = ToastType.Celebration,
            Message = "Podcast ready!",
            Detail = string.Empty
        };

        var act = () => ToastRenderer.RenderToast(toast, _palette, 80);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void RenderToast_AllThemes_DoesNotThrow(ThemeName theme)
    {
        var palette = BuiltInThemes.Get(theme);
        var toast = new ToastNotification
        {
            Type = ToastType.Info,
            Message = "Cache warmed",
            Detail = "12 pages"
        };

        var act = () => ToastRenderer.RenderToast(toast, palette, 100);

        act.Should().NotThrow();
    }
}
