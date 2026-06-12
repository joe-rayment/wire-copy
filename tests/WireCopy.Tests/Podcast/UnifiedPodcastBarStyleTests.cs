// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-m8es.1: every podcast surface renders ONE bar style — the
/// reading-list CTA's treatment (celebration fill while running, success fill
/// at completion, muted track). These tests pin the shared helper and prove
/// the generating modal routes through it (no warning-colored bar remains).
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public class UnifiedPodcastBarStyleTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    [Fact]
    public void PodcastBar_InProgress_UsesCelebrationFill()
    {
        var bar = Indicators.PodcastBar(Palette, 0.5, 10);

        bar.Should().Contain(Palette.GetCelebrationFg().AnsiFg);
        bar.Should().NotContain(Palette.GetWarningFg().AnsiFg,
            "the modal's old warning-colored treatment is retired");
    }

    [Fact]
    public void PodcastBar_Complete_UsesSuccessFill()
    {
        var bar = Indicators.PodcastBar(Palette, 1.0, 10);

        bar.Should().Contain(Palette.GetSuccessFg().AnsiFg);
        bar.Should().NotContain(Palette.GetCelebrationFg().AnsiFg);
    }

    [Fact]
    public void GeneratingModal_GlobalBar_UsesTheUnifiedStyle()
    {
        var output = CaptureRender(helpers =>
            PodcastProgressScreens.RenderProgressContent(
                helpers,
                Palette,
                new PodcastProgress { Phase = PodcastPhase.GeneratingAudio, PercentComplete = 40 },
                animFrame: 0,
                statuses: Array.Empty<PodcastCommandHandler.ArticleStatus>(),
                terminalWidth: 100,
                terminalHeight: 30));

        // The bar block characters must be celebration-colored, never the old
        // warning color. (The warning color legitimately appears elsewhere on
        // the screen — e.g. the ⟳ glyph — so assert on the bar run itself.)
        var celebrationBar = Palette.GetCelebrationFg().AnsiFg + "█";
        var warningBar = Palette.GetWarningFg().AnsiFg + "█";
        output.Should().Contain(celebrationBar);
        output.Should().NotContain(warningBar);
    }

    [Fact]
    public void PhaseSubBars_UseTheUnifiedStyle()
    {
        var aggregator = new PodcastProgressAggregator();
        aggregator.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            TotalArticles = 4,
            CurrentArticle = 3, // 2 done of 4 → TTS sub-bar half full
        });

        var output = CaptureRender(helpers =>
            PodcastProgressScreens.RenderPhaseSubBars(helpers, Palette, aggregator, width: 100));

        var celebrationBar = Palette.GetCelebrationFg().AnsiFg + "█";
        output.Should().Contain(celebrationBar, "an in-flight phase fills with the celebration color");
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
