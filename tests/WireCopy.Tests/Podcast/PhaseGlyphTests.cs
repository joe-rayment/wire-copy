// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-lxga — the four sub-bars on the progress screen must show a
/// state glyph so the user can identify the in-flight phase at a glance:
/// ✓ done, ⟳ in-flight, · pending. Pins the classifier + the rendered
/// terminal output.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class PhaseGlyphTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    [Fact]
    public void ClassifyPhaseState_Complete_ReturnsCheckGlyph()
    {
        var (glyph, _) = PodcastProgressScreens.ClassifyPhaseState(1.0, Palette);
        glyph.Should().Be("✓");
    }

    [Fact]
    public void ClassifyPhaseState_NearlyComplete_StillCheck()
    {
        // 0.999 is the threshold — floating-point noise shouldn't flip a
        // 100%-but-not-quite phase back to in-flight.
        var (glyph, _) = PodcastProgressScreens.ClassifyPhaseState(0.999, Palette);
        glyph.Should().Be("✓");
    }

    [Fact]
    public void ClassifyPhaseState_InFlight_ReturnsRefreshGlyph()
    {
        var (glyph, _) = PodcastProgressScreens.ClassifyPhaseState(0.5, Palette);
        glyph.Should().Be("⟳");
    }

    [Fact]
    public void ClassifyPhaseState_JustStarted_StillInFlight()
    {
        // 0.001 — barely started, but the user pressed `p` and we want them
        // to know the pipeline is moving.
        var (glyph, _) = PodcastProgressScreens.ClassifyPhaseState(0.001, Palette);
        glyph.Should().Be("⟳");
    }

    [Fact]
    public void ClassifyPhaseState_Pending_ReturnsDotGlyph()
    {
        var (glyph, _) = PodcastProgressScreens.ClassifyPhaseState(0.0, Palette);
        glyph.Should().Be("·");
    }

    /// <summary>
    /// End-to-end: when the run is in CachingContent (Extracting in flight,
    /// Synthesizing/Assembling/Publishing all pending), the rendered output
    /// must contain ✓ once we observe the phase complete + ⟳ for the
    /// current + · for the rest. Drives RenderProgressContent through the
    /// real PodcastProgressAggregator.
    /// </summary>
    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RenderProgressContent_AdvancedToTtsPhase_RendersCheckRefreshDotDot()
    {
        var aggregator = new PodcastProgressAggregator();

        // Caching complete: emit a CachingContent IsArticleComplete for the
        // single article so the aggregator's caching phase reaches 1.0.
        aggregator.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.CachingContent,
            CurrentArticle = 1,
            TotalArticles = 1,
            IsArticleComplete = true,
            IsArticleSuccess = true,
            PercentComplete = 100,
        });

        // TTS in-flight: 5 of 10 chunks done on article 1 of 1.
        aggregator.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 1,
            TotalArticles = 1,
            CurrentArticleChunkIndex = 5,
            CurrentArticleChunkTotal = 10,
            CurrentArticleChunkPercent = 50,
        });

        var statuses = new PodcastCommandHandler.ArticleStatus[1]
        {
            new() { Title = "Article 1", State = PodcastCommandHandler.ArticleState.Processing },
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderProgressContent(
            h, Palette,
            new PodcastProgress { Phase = PodcastPhase.GeneratingAudio, CurrentArticle = 1, TotalArticles = 1 },
            animFrame: 0, statuses, terminalWidth: 100, terminalHeight: 40, targets: null, aggregator));

        output.Should().Contain("Extracting");
        output.Should().Contain("Synthesizing");
        output.Should().Contain("Assembling");
        output.Should().Contain("Publishing");

        // The Extracting sub-bar should now show a ✓ (complete), and
        // Assembling + Publishing should show · (pending). Synthesizing is
        // in-flight, so ⟳.
        output.Should().Contain("✓",
            "Extracting must render ✓ now that caching reached 100%");
        output.Should().Contain("⟳",
            "Synthesizing is in flight — must render the in-flight glyph");
        output.Should().Contain("·",
            "Assembling + Publishing haven't started — must render the pending dot");

        // workspace-lxga: pin glyph-to-label adjacency so an implementation
        // that emits the right glyph counts but on the wrong rows would fail.
        // ANSI escapes can sit between the glyph and the label — strip them
        // first, then assert each "glyph Label" prefix appears on its own
        // line. Without this regex pin, swapping the glyphs across phases
        // would still pass the bare Contain() checks.
        var stripped = System.Text.RegularExpressions.Regex.Replace(output, "\x1b\\[[0-9;]*[A-Za-z]", string.Empty);
        stripped.Should().MatchRegex(@"✓\s+Extracting",
            because: "Extracting is at 100% and MUST be marked complete");
        stripped.Should().MatchRegex(@"⟳\s+Synthesizing",
            because: "Synthesizing is the in-flight phase");
        stripped.Should().MatchRegex(@"·\s+Assembling",
            because: "Assembling hasn't started");
        stripped.Should().MatchRegex(@"·\s+Publishing",
            because: "Publishing hasn't started");
    }

    private static string CaptureRender(Action<RenderHelpers> action, int terminalHeight = 30)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var helpers = new RenderHelpers { TerminalHeight = terminalHeight };
            helpers.Clear();
            action(helpers);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return sw.ToString();
    }
}
