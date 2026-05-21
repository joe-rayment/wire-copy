// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-v34z visual smoke. Dumps the new progress screen at three
/// terminal widths and the four pipeline phases so a human reviewer can
/// eyeball the layout. Output is stripped of ANSI escapes and written via
/// <see cref="ITestOutputHelper"/> so the test framework surfaces it — and
/// also written to /tmp so it can be diffed across runs.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public sealed class ProgressScreenVisualSmoke
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    private readonly ITestOutputHelper _output;

    public ProgressScreenVisualSmoke(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void DumpsRenderAtThreeWidthsAndAllPhases()
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), "wirecopy-progress-screen-v34z.txt");
        using var dump = new StreamWriter(dumpPath);

        foreach (var width in new[] { 80, 100, 140 })
        {
            foreach (var phase in new[]
            {
                PodcastPhase.CachingContent,
                PodcastPhase.GeneratingAudio,
                PodcastPhase.AssemblingAudio,
                PodcastPhase.Publishing,
            })
            {
                var (rendered, progress) = RenderForPhase(phase, width);
                dump.WriteLine($"========================================");
                dump.WriteLine($"WIDTH {width}, PHASE {phase}, percent={progress.PercentComplete}");
                dump.WriteLine($"========================================");
                dump.WriteLine(StripAnsi(rendered));
                dump.WriteLine();
            }
        }

        _output.WriteLine($"Visual dump written to: {dumpPath}");
        File.Exists(dumpPath).Should().BeTrue();
    }

    private static (string, PodcastProgress) RenderForPhase(PodcastPhase phase, int width)
    {
        var now = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new PodcastProgressAggregator(() => now);
        const int Total = 12;
        PodcastProgress lastProgress = new() { Phase = PodcastPhase.CachingContent };

        // Pre-roll extracting through the requested phase so the aggregator
        // shows realistic per-phase percentages and details.
        for (var i = 1; i <= 12; i++)
        {
            now += TimeSpan.FromSeconds(1);
            lastProgress = new PodcastProgress
            {
                Phase = PodcastPhase.CachingContent,
                CurrentArticle = i,
                TotalArticles = Total,
                IsArticleComplete = true,
                IsArticleSuccess = true,
            };
            aggregator.Observe(lastProgress);
            if (phase == PodcastPhase.CachingContent && i == 6)
            {
                break;
            }
        }

        if (phase is PodcastPhase.GeneratingAudio or PodcastPhase.AssemblingAudio or PodcastPhase.Publishing)
        {
            var ttsTicks = phase == PodcastPhase.GeneratingAudio ? 6 : Total;
            for (var i = 1; i <= ttsTicks; i++)
            {
                now += TimeSpan.FromSeconds(2);
                lastProgress = new PodcastProgress
                {
                    Phase = PodcastPhase.GeneratingAudio,
                    CurrentArticle = i,
                    TotalArticles = Total,
                    CurrentArticleChunkIndex = 1,
                    CurrentArticleChunkTotal = 1,
                    CurrentArticleChunkPercent = 100,
                };
                aggregator.Observe(lastProgress);
            }
        }

        if (phase is PodcastPhase.AssemblingAudio or PodcastPhase.Publishing)
        {
            var asmTicks = phase == PodcastPhase.AssemblingAudio ? 4 : 12;
            for (var i = 1; i <= asmTicks; i++)
            {
                now += TimeSpan.FromSeconds(1);
                lastProgress = new PodcastProgress
                {
                    Phase = PodcastPhase.AssemblingAudio,
                    AssembledSegments = i,
                    AssembledSegmentsTotal = 12,
                };
                aggregator.Observe(lastProgress);
            }
        }

        if (phase == PodcastPhase.Publishing)
        {
            now += TimeSpan.FromSeconds(2);
            lastProgress = new PodcastProgress
            {
                Phase = PodcastPhase.Publishing,
                UploadedEpisodes = 0,
                UploadedEpisodesTotal = 1,
                UploadedBytes = 500_000,
                UploadedBytesTotal = 2_500_000,
            };
            aggregator.Observe(lastProgress);
        }

        var statuses = new PodcastCommandHandler.ArticleStatus[Total];
        for (var i = 0; i < Total; i++)
        {
            statuses[i] = new PodcastCommandHandler.ArticleStatus
            {
                Title = $"Article {i + 1} — example.com",
                State = i < lastProgress.CurrentArticle - 1
                    ? PodcastCommandHandler.ArticleState.Completed
                    : i == lastProgress.CurrentArticle - 1
                        ? PodcastCommandHandler.ArticleState.Processing
                        : PodcastCommandHandler.ArticleState.Pending,
            };
        }

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var helpers = new RenderHelpers { TerminalHeight = 40 };
            helpers.Clear();
            PodcastProgressScreens.RenderProgressContent(
                helpers,
                Palette,
                lastProgress,
                animFrame: 0,
                statuses,
                terminalWidth: width,
                terminalHeight: 40,
                targets: null,
                aggregator);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return (sw.ToString(), lastProgress);
    }

    private static string StripAnsi(string text)
    {
        // Drop ANSI CSI / OSC sequences so the dump is readable in a plain
        // text reviewer. The renderer emits \x1b[..m (color) and \x1b[..K
        // (clear-line) — both are CSI.
        var result = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\x1b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                i += 2;
                while (i < text.Length && !(text[i] >= '@' && text[i] <= '~'))
                {
                    i++;
                }

                if (i < text.Length)
                {
                    i++;
                }

                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }
}
