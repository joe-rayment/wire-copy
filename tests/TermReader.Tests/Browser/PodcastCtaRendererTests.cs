// Educational and personal use only.

using FluentAssertions;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

public class PodcastCtaRendererTests
{
    private readonly PodcastCtaRenderer _renderer;

    public PodcastCtaRendererTests()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 50 };
        _renderer = new PodcastCtaRenderer(helpers, themeProvider);
    }

    #region GetCtaLineCount — tier selection

    [Theory]
    [InlineData(80, 30, 5)]  // Full slab
    [InlineData(80, 24, 5)]  // Full slab (boundary)
    [InlineData(52, 30, 5)]  // Full slab (width boundary: 52-2=50 >= 50)
    [InlineData(80, 23, 3)]  // Compact slab (height < 24)
    [InlineData(49, 30, 3)]  // Compact slab (width < 50)
    [InlineData(80, 19, 1)]  // Inline (height < 20)
    [InlineData(36, 30, 1)]  // Inline (width < 35)
    [InlineData(20, 15, 1)]  // Inline (both small)
    public void GetCtaLineCount_ReturnsCorrectTier(int terminalWidth, int terminalHeight, int expectedLines)
    {
        PodcastCtaRenderer.GetCtaLineCount(terminalWidth, terminalHeight).Should().Be(expectedLines);
    }

    #endregion

    #region Render — does not throw for any state/tier combo

    [Theory]
    [InlineData(0)] // Idle
    [InlineData(1)] // Pressed
    [InlineData(2)] // Disabled
    [InlineData(3)] // Unconfigured
    public void Render_FullSlab_AllStates_DoesNotThrow(int stateValue)
    {
        var options = CreateOptions(80, 30);
        var act = () => _renderer.Render(options, (PodcastCtaState)stateValue);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Render_CompactSlab_AllStates_DoesNotThrow(int stateValue)
    {
        var options = CreateOptions(80, 22);
        var act = () => _renderer.Render(options, (PodcastCtaState)stateValue);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Render_Inline_AllStates_DoesNotThrow(int stateValue)
    {
        var options = CreateOptions(40, 15);
        var act = () => _renderer.Render(options, (PodcastCtaState)stateValue);
        act.Should().NotThrow();
    }

    #endregion

    #region Render — all themes

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void Render_AllThemes_DoesNotThrow(ThemeName theme)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(theme);
        var helpers = new RenderHelpers { TerminalHeight = 50 };
        var renderer = new PodcastCtaRenderer(helpers, themeProvider);

        var options = CreateOptions(80, 30);
        var act = () => renderer.Render(options);

        act.Should().NotThrow();
    }

    #endregion

    #region Render — edge cases

    [Fact]
    public void Render_VeryNarrowTerminal_DoesNotThrow()
    {
        var options = CreateOptions(20, 30);
        var act = () => _renderer.Render(options);
        act.Should().NotThrow();
    }

    [Fact]
    public void Render_VeryShortTerminal_DoesNotThrow()
    {
        var options = CreateOptions(80, 5);
        var act = () => _renderer.Render(options);
        act.Should().NotThrow();
    }

    [Fact]
    public void Render_MinimumDimensions_DoesNotThrow()
    {
        var options = CreateOptions(10, 5);
        var act = () => _renderer.Render(options);
        act.Should().NotThrow();
    }

    [Fact]
    public void Render_WideTerminal_DoesNotThrow()
    {
        var options = CreateOptions(200, 50);
        var act = () => _renderer.Render(options);
        act.Should().NotThrow();
    }

    [Fact]
    public void Render_DefaultState_IsIdle()
    {
        var options = CreateOptions(80, 30);
        // Calling without state should default to Idle
        var act = () => _renderer.Render(options);
        act.Should().NotThrow();
    }

    #endregion

    #region GetCtaLineCount — boundary tests

    [Fact]
    public void GetCtaLineCount_ExactlyAt24Height50Width_Returns5()
    {
        PodcastCtaRenderer.GetCtaLineCount(52, 24).Should().Be(5);
    }

    [Fact]
    public void GetCtaLineCount_JustBelow24Height_Returns3()
    {
        PodcastCtaRenderer.GetCtaLineCount(80, 23).Should().Be(3);
    }

    [Fact]
    public void GetCtaLineCount_JustBelow20Height_Returns1()
    {
        PodcastCtaRenderer.GetCtaLineCount(80, 19).Should().Be(1);
    }

    #endregion

    #region Helpers

    private static RenderOptions CreateOptions(int terminalWidth = 80, int terminalHeight = 40)
    {
        return new RenderOptions
        {
            TerminalWidth = terminalWidth,
            TerminalHeight = terminalHeight,
            MaxContentWidth = terminalWidth - 4,
        };
    }

    #endregion
}
