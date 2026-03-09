// Educational and personal use only.

using FluentAssertions;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

public class PodcastButtonStateTests
{
    #region RenderOptions defaults

    [Fact]
    public void RenderOptions_PodcastButtonState_DefaultsToIdle()
    {
        var options = new RenderOptions();
        options.PodcastButtonState.Should().Be(0, "default should be 0 (Idle)");
    }

    [Theory]
    [InlineData(0)] // Idle
    [InlineData(1)] // Pressed
    [InlineData(2)] // Disabled
    [InlineData(3)] // Unconfigured
    public void RenderOptions_PodcastButtonState_CanBeSetToAnyValidState(int state)
    {
        var options = new RenderOptions { PodcastButtonState = state };
        options.PodcastButtonState.Should().Be(state);
    }

    [Fact]
    public void RenderOptions_WithExpression_PreservesPodcastButtonState()
    {
        var original = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 24,
            PodcastButtonState = 3,
        };

        var modified = original with { PodcastButtonState = 1 };

        modified.PodcastButtonState.Should().Be(1);
        modified.TerminalWidth.Should().Be(80, "other properties should be preserved");
    }

    #endregion

    #region Unconfigured state rendering

    [Theory]
    [InlineData(80, 30)]  // Full slab
    [InlineData(80, 22)]  // Compact slab
    [InlineData(80, 15)]  // Inline
    public void Render_UnconfiguredState_AllTiers_DoesNotThrow(int width, int height)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = height };
        var renderer = new PodcastCtaRenderer(helpers, themeProvider);

        var options = new RenderOptions
        {
            TerminalWidth = width,
            TerminalHeight = height,
            PodcastButtonState = 3,
        };

        var act = () => renderer.Render(options, (PodcastCtaState)options.PodcastButtonState);
        act.Should().NotThrow();
    }

    [Fact]
    public void Render_PressedState_ThenIdle_DoesNotThrow()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var renderer = new PodcastCtaRenderer(helpers, themeProvider);

        var options = new RenderOptions { TerminalWidth = 80, TerminalHeight = 30 };

        // Simulate press feedback: render Pressed then Idle
        var pressedOptions = options with { PodcastButtonState = 1 };
        var act1 = () => renderer.Render(pressedOptions, (PodcastCtaState)pressedOptions.PodcastButtonState);
        act1.Should().NotThrow();

        helpers = new RenderHelpers { TerminalHeight = 30 };
        renderer = new PodcastCtaRenderer(helpers, themeProvider);
        var act2 = () => renderer.Render(options, (PodcastCtaState)options.PodcastButtonState);
        act2.Should().NotThrow();
    }

    #endregion

    #region CollectionRenderer integration with PodcastButtonState

    [Theory]
    [InlineData(0)] // Idle
    [InlineData(1)] // Pressed
    [InlineData(2)] // Disabled
    [InlineData(3)] // Unconfigured
    public void CollectionRenderer_RenderCollectionItems_AllPodcastStates_DoesNotThrow(int podcastButtonState)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var renderer = new CollectionRenderer(helpers, themeProvider);

        var collection = TermReader.Domain.Entities.Collections.Collection.Create("Test");
        collection.AddItem("https://example.com/1", "Article 1");
        collection.AddItem("https://example.com/2", "Article 2");

        var options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 30,
            PodcastButtonState = podcastButtonState,
        };

        var act = () => renderer.RenderCollectionItems(collection, 0, 0, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void CollectionRenderer_EmptyCollection_NoPodcastButton_DoesNotThrow()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var renderer = new CollectionRenderer(helpers, themeProvider);

        var collection = TermReader.Domain.Entities.Collections.Collection.Create("Empty");

        var options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 30,
            PodcastButtonState = 3, // Unconfigured, but shouldn't render for empty
        };

        var act = () => renderer.RenderCollectionItems(collection, 0, 0, options);
        act.Should().NotThrow();
    }

    #endregion
}
