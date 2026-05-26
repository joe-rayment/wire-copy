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
    [InlineData(80, 40, 7)]  // Hero box (height >= 22, width-2 >= 50)
    [InlineData(80, 36, 7)]  // Hero box
    [InlineData(80, 30, 7)]  // Hero box (typical 30-line terminal)
    [InlineData(80, 24, 7)]  // Hero box (standard 24-line terminal)
    [InlineData(80, 22, 7)]  // Hero box (boundary)
    [InlineData(52, 40, 7)]  // Hero box (width boundary: 52-2=50 >= 50)
    [InlineData(49, 40, 3)]  // Compact slab (width-2=47 < 50 but >= 35)
    [InlineData(80, 21, 3)]  // Compact slab (height < 22 but >= 18)
    [InlineData(80, 18, 3)]  // Compact slab (height boundary)
    [InlineData(80, 17, 1)]  // Inline (height < 18)
    [InlineData(80, 5, 1)]   // Inline (very short)
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
    public void GetCtaLineCount_ExactlyAt22Height50Width_Returns7()
    {
        PodcastCtaRenderer.GetCtaLineCount(52, 22).Should().Be(7);
    }

    [Fact]
    public void GetCtaLineCount_At21Height_ReturnsCompact()
    {
        PodcastCtaRenderer.GetCtaLineCount(80, 21).Should().Be(3);
    }

    [Fact]
    public void GetCtaLineCount_JustBelow18Height_Returns1()
    {
        PodcastCtaRenderer.GetCtaLineCount(80, 17).Should().Be(1);
    }

    #endregion

    #region workspace-y41e — generating hero box with subscribe URL

    [Fact]
    public void Render_GeneratingHeroBox_WithFeedUrl_ShowsWalkAwayAndSubscribeUrl()
    {
        var captured = CaptureRender(opts =>
            opts with
            {
                PodcastButtonState = (int)PodcastCtaState.Generating,
                PodcastProgressFraction = 0.42,
                PodcastFeedUrl = "https://storage.googleapis.com/my-bucket/podcasts/abc123/feed.xml",
            });

        captured.Should().Contain("GENERATING",
            because: "the existing hero title row must stay during generating state");
        captured.Should().Contain("Takes a few min",
            because: "the walk-away copy must surface so users know it's safe to step away");
        captured.Should().Contain("subscribe in your podcast app",
            because: "the walk-away copy must direct the user to subscribe");
        captured.Should().Contain("Subscribe:",
            because: "the new subscribe row must label the URL");
        // The URL is long enough that it ends up middle-elided at width 80; either
        // the host or the tail must remain visible so the user can recognise it.
        var hasHost = captured.Contains("storage.googleapis.com");
        var hasTail = captured.Contains("feed.xml");
        (hasHost || hasTail).Should().BeTrue(
            because: "the host or the final segment of the URL must survive truncation");
        captured.Should().NotContain("mixing 1 article",
            because: "the local-only subtitle is replaced when a feed URL is present");
    }

    [Fact]
    public void Render_GeneratingHeroBox_WithoutFeedUrl_FallsBackToOriginalSubtitle()
    {
        var captured = CaptureRender(opts =>
            opts with
            {
                PodcastButtonState = (int)PodcastCtaState.Generating,
                PodcastProgressFraction = 0.42,
                PodcastArticleCount = 3,
                PodcastFeedUrl = null,
            });

        captured.Should().Contain("GENERATING",
            because: "title row stays the same");
        captured.Should().Contain("mixing 3 articles",
            because: "local-only mode keeps the original subtitle so the box doesn't resize");
        captured.Should().NotContain("Subscribe:",
            because: "no feed URL means no subscribe row");
        captured.Should().NotContain("Takes a few min",
            because: "no feed URL means no walk-away copy (nothing to subscribe to)");
    }

    private static string CaptureRender(Func<RenderOptions, RenderOptions> mutateOptions)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 50 };
        var renderer = new PodcastCtaRenderer(helpers, themeProvider);

        var options = mutateOptions(CreateOptions(80, 40));
        var state = (PodcastCtaState)options.PodcastButtonState;

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderer.Render(options, state, articleCount: options.PodcastArticleCount);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return sw.ToString();
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
