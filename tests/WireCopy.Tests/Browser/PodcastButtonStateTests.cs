// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
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
    [InlineData(5)] // Generating
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

    #region CollectionRenderer cache indicator

    [Fact]
    public void CollectionRenderer_CachedItem_DoesNotThrow()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var renderer = new CollectionRenderer(helpers, themeProvider);

        var collection = WireCopy.Domain.Entities.Collections.Collection.Create("Test");
        collection.AddItem("https://example.com/1", "Article 1");
        collection.AddItem("https://example.com/2", "Article 2");

        var cachedUrls = new HashSet<string> { "https://example.com/1" };
        var options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 30,
            CachedUrls = cachedUrls,
        };

        var act = () => renderer.RenderCollectionItems(collection, 0, 0, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void CollectionRenderer_CachedSelectedItem_DoesNotThrow()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var renderer = new CollectionRenderer(helpers, themeProvider);

        var collection = WireCopy.Domain.Entities.Collections.Collection.Create("Test");
        collection.AddItem("https://example.com/1", "Article 1");

        var cachedUrls = new HashSet<string> { "https://example.com/1" };
        var options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 30,
            CachedUrls = cachedUrls,
        };

        // Selected item (index 0 == first item)
        var act = () => renderer.RenderCollectionItems(collection, 0, 0, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void CollectionRenderer_NullCachedUrls_DoesNotThrow()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var renderer = new CollectionRenderer(helpers, themeProvider);

        var collection = WireCopy.Domain.Entities.Collections.Collection.Create("Test");
        collection.AddItem("https://example.com/1", "Article 1");

        var options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 30,
            CachedUrls = null,
        };

        var act = () => renderer.RenderCollectionItems(collection, 0, 0, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void CollectionRenderer_NarrowTerminal_CachedItem_DoesNotThrow()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var renderer = new CollectionRenderer(helpers, themeProvider);

        var collection = WireCopy.Domain.Entities.Collections.Collection.Create("Test");
        collection.AddItem("https://example.com/1", "Article 1");

        var cachedUrls = new HashSet<string> { "https://example.com/1" };
        var options = new RenderOptions
        {
            TerminalWidth = 40, // Narrow terminal
            TerminalHeight = 30,
            CachedUrls = cachedUrls,
        };

        var act = () => renderer.RenderCollectionItems(collection, 0, 0, options);
        act.Should().NotThrow();
    }

    #endregion

    #region CollectionRenderer integration with PodcastButtonState

    [Theory]
    [InlineData(0)] // Idle
    [InlineData(1)] // Pressed
    [InlineData(2)] // Disabled
    [InlineData(3)] // Unconfigured
    [InlineData(5)] // Generating
    public void CollectionRenderer_RenderCollectionItems_AllPodcastStates_DoesNotThrow(int podcastButtonState)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 30 };
        var renderer = new CollectionRenderer(helpers, themeProvider);

        var collection = WireCopy.Domain.Entities.Collections.Collection.Create("Test");
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

        var collection = WireCopy.Domain.Entities.Collections.Collection.Create("Empty");

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

    #region Generating state rendering

    [Theory]
    [InlineData(80, 40)]  // Full slab
    [InlineData(80, 30)]  // Compact slab (height <= 35 not eligible for full slab)
    [InlineData(80, 22)]  // Compact slab
    [InlineData(80, 15)]  // Inline
    [InlineData(40, 40)]  // Narrow full slab
    [InlineData(30, 40)]  // Narrow compact slab
    [InlineData(30, 15)]  // Narrow inline
    public void Render_GeneratingState_AllTiers_DoesNotThrow(int width, int height)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = height };
        var renderer = new PodcastCtaRenderer(helpers, themeProvider);

        var options = new RenderOptions
        {
            TerminalWidth = width,
            TerminalHeight = height,
            PodcastButtonState = 5, // Generating
            PodcastProgressFraction = 0.42,
        };

        var act = () => renderer.Render(options, (PodcastCtaState)options.PodcastButtonState);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.28)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    [InlineData(1.0)]
    public void Render_GeneratingState_VariousProgressFractions_DoesNotThrow(double fraction)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 40 };
        var renderer = new PodcastCtaRenderer(helpers, themeProvider);

        var options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 40,
            PodcastButtonState = 5,
            PodcastProgressFraction = fraction,
        };

        var act = () => renderer.Render(options, (PodcastCtaState)options.PodcastButtonState);
        act.Should().NotThrow();
    }

    [Fact]
    public void RenderOptions_PodcastProgressFraction_DefaultsToZero()
    {
        var options = new RenderOptions();
        options.PodcastProgressFraction.Should().Be(0.0);
    }

    [Fact]
    public void RenderOptions_PodcastProgressFraction_CanBeSet()
    {
        var options = new RenderOptions { PodcastProgressFraction = 0.75 };
        options.PodcastProgressFraction.Should().Be(0.75);
    }

    [Fact]
    public void RenderOptions_WithExpression_PreservesPodcastProgressFraction()
    {
        var original = new RenderOptions
        {
            PodcastButtonState = 5,
            PodcastProgressFraction = 0.5,
        };

        var modified = original with { PodcastProgressFraction = 0.8 };

        modified.PodcastProgressFraction.Should().Be(0.8);
        modified.PodcastButtonState.Should().Be(5, "other properties should be preserved");
    }

    [Theory]
    [InlineData(ThemeName.Phosphor)]
    [InlineData(ThemeName.Amber)]
    [InlineData(ThemeName.Dracula)]
    [InlineData(ThemeName.Light)]
    public void Render_GeneratingState_AllThemes_DoesNotThrow(ThemeName theme)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(theme);
        var helpers = new RenderHelpers { TerminalHeight = 40 };
        var renderer = new PodcastCtaRenderer(helpers, themeProvider);

        var options = new RenderOptions
        {
            TerminalWidth = 80,
            TerminalHeight = 40,
            PodcastButtonState = 5,
            PodcastProgressFraction = 0.42,
        };

        var act = () => renderer.Render(options, (PodcastCtaState)options.PodcastButtonState);
        act.Should().NotThrow();
    }

    #endregion
}
