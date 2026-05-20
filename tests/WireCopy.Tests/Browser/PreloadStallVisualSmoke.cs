// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-fh7g visual smoke. Dumps the prefetch detail panel at three
/// stall stages (calm <c>4s</c>, warning <c>12s</c>, stuck <c>45s</c>) and
/// at three terminal widths so a reviewer can confirm:
/// <list type="bullet">
///   <item>The calm state has no elapsed suffix on Now and no stuck hint.</item>
///   <item>The warning state appends "(12s)" to Now and uses the warning ANSI color.</item>
///   <item>The stuck state shows the "looks stuck — Shift+R to retry" line under the stage chip.</item>
/// </list>
/// Output is written to <c>/tmp/wirecopy-preload-stall-fh7g.txt</c>. Captured
/// both with and without ANSI so the reviewer can see the colors AND the
/// layout.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PreloadStallVisualSmoke
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    private readonly ITestOutputHelper _output;

    public PreloadStallVisualSmoke(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DumpsCalmWarningStuckStatesAtThreeWidths()
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), "wirecopy-preload-stall-fh7g.txt");
        using var dump = new StreamWriter(dumpPath);

        foreach (var width in new[] { 80, 100, 140 })
        {
            foreach (var (label, elapsed) in new[]
            {
                ("CALM (3s)", TimeSpan.FromSeconds(3)),
                ("WARNING (12s)", TimeSpan.FromSeconds(12)),
                ("STUCK (45s)", TimeSpan.FromSeconds(45)),
            })
            {
                var progress = MakeProgress(elapsed);
                var lines = PreloadDetailRenderer.BuildPanelLines(progress, Palette, width);

                dump.WriteLine("========================================");
                dump.WriteLine($"WIDTH {width}, STATE {label}, elapsed={elapsed.TotalSeconds}s");
                dump.WriteLine("========================================");
                dump.WriteLine("[Plain]");
                foreach (var line in lines)
                {
                    dump.WriteLine(line.PlainText);
                }

                dump.WriteLine("[Styled, ANSI sanitized to <FG> / <RESET>]");
                foreach (var line in lines)
                {
                    dump.WriteLine(SanitizeAnsi(line.StyledText));
                }

                dump.WriteLine();
            }
        }

        _output.WriteLine($"Visual dump written to: {dumpPath}");
        File.Exists(dumpPath).Should().BeTrue();

        // The dump existing isn't enough — assert the warning + stuck markers
        // actually show up in the captured frames at width 100 so a regression
        // that drops the elapsed suffix or the stuck hint fails this test
        // even if the file is still produced.
        var dumpText = File.ReadAllText(dumpPath);
        dumpText.Should().Contain("WIDTH 100, STATE WARNING");
        dumpText.Should().Contain("(12s)",
            because: "the warning frame at width 100 must include the elapsed suffix");
        dumpText.Should().Contain("looks stuck — Shift+R to retry",
            because: "the stuck frame must include the recovery hint");
    }

    private static PreloadProgress MakeProgress(TimeSpan elapsed)
    {
        return new PreloadProgress
        {
            TotalCacheableLinks = 12,
            CachedCount = 5,
            CurrentlyFetchingUrl = "https://www.example.com/article-being-fetched-right-now",
            ElapsedOnCurrent = elapsed,
            CurrentStage = PreloadStage.Fetching,
            IsActivelyFetching = true,
            UpcomingUrls = new List<string>
            {
                "https://example.com/queue/one",
                "https://example.com/queue/two",
            },
            RecentItems = new List<PreloadHistoryEntry>
            {
                new() { Url = "https://example.com/done/a", Outcome = PreloadOutcome.Cached, ElapsedMs = 320 },
            },
        };
    }

    /// <summary>
    /// Replace ANSI escape sequences with readable markers so the dump shows
    /// where color was applied without dumping raw control bytes.
    /// </summary>
    private static string SanitizeAnsi(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var start = i;
                i += 2;
                while (i < text.Length && !(text[i] >= '@' && text[i] <= '~'))
                {
                    i++;
                }

                if (i < text.Length)
                {
                    i++;
                }

                var raw = text.Substring(start, i - start);
                if (raw == "\x1b[0m")
                {
                    result.Append("<RESET>");
                }
                else
                {
                    result.Append("<FG=").Append(raw.Substring(2, raw.Length - 3)).Append('>');
                }

                continue;
            }

            result.Append(text[i]);
            i++;
        }

        return result.ToString();
    }
}
