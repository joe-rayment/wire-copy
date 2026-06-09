// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the persistent "⇉ docked" affordance rendered by
/// <see cref="StatusBarRenderer"/> (workspace-v7mb), so the side-by-side "concert"
/// state stays visible after the transient status message fades. Uses the real
/// serial console collection so it doesn't race other Console.Out-mutating tests.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class StatusBarRendererDockBadgeTests
{
    private readonly StatusBarRenderer _statusBar;

    public StatusBarRendererDockBadgeTests()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        _statusBar = new StatusBarRenderer(new RenderHelpers(), themeProvider);
    }

    private static string CaptureConsoleOutput(Action action)
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);
            action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Theory]
    [InlineData(ViewMode.Hierarchical)]
    [InlineData(ViewMode.Readable)]
    public void StatusBar_RendersDockedAffordance_WhenBrowserDocked(ViewMode mode)
    {
        var context = new NavigationContext { ViewMode = mode };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, mode, terminalWidth: 200, browserDocked: true));

        output.Should().Contain("docked", "the persistent affordance labels the concert state");
        output.Should().Contain("⇉", "the ⇉ glyph signals the docked side-by-side layout at a glance");
    }

    [Fact]
    public void StatusBar_OmitsDockedAffordance_WhenNotDocked()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };

        var output = CaptureConsoleOutput(() =>
            _statusBar.RenderStatusBar(context, ViewMode.Hierarchical, terminalWidth: 200, browserDocked: false));

        output.Should().NotContain("docked", "the affordance must not appear when the browser is not docked");
        output.Should().NotContain("⇉");
    }
}
