// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-wef6.6 visual smoke (mirrors PreloadDetailOverlayVisualSmoke).
/// Renders the composed status line for every theme across representative
/// states — idle with hints, HITL alert + transient, speed-read, prefetch
/// activity — at widths 45/60/80/120, and dumps the ANSI-stripped output to
/// /tmp/wirecopy-status-line-wef6.txt for human review.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public sealed class StatusLineVisualSmoke
{
    private readonly ITestOutputHelper _output;

    public StatusLineVisualSmoke(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void DumpsComposedStatusLine_AllThemesAndStates()
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), "wirecopy-status-line-wef6.txt");
        using var dump = new StreamWriter(dumpPath);

        var states = new (string Name, NavigationContext Context, PreloadProgress? Progress, HumanActionRequired? Action)[]
        {
            ("idle link view (hints fill the slack)",
                new NavigationContext { ViewMode = ViewMode.Hierarchical, BackHistoryCount = 1 },
                null,
                null),
            ("HITL alert + save transient",
                new NavigationContext
                {
                    ViewMode = ViewMode.Hierarchical,
                    ActiveAnnouncement = new StatusAnnouncement
                    {
                        Glyph = "✓",
                        Text = "Saved (3)",
                        Keys = new[] { new StatusKeyHint("c", "list") },
                        ShortText = "✓3",
                    },
                },
                null,
                new HumanActionRequired(HumanActionVariant.Login, "nytimes.com")),
            ("speed-read announcement",
                new NavigationContext
                {
                    ViewMode = ViewMode.Readable,
                    IsSpeedReadActive = true,
                    SpeedReadWpm = 350,
                    ActiveAnnouncement = new StatusAnnouncement
                    {
                        Glyph = "▶",
                        Text = "Speed reading 350 WPM",
                        Keys = new[]
                        {
                            new StatusKeyHint("<", "slower"),
                            new StatusKeyHint(">", "faster"),
                            new StatusKeyHint("f", "stop"),
                        },
                        ShortText = "▶350",
                    },
                },
                null,
                null),
            ("prefetch activity",
                new NavigationContext { ViewMode = ViewMode.Hierarchical },
                new PreloadProgress
                {
                    TotalCacheableLinks = 12,
                    CachedCount = 5,
                    IsActivelyFetching = true,
                    CurrentlyFetchingUrl = "https://example.com/next",
                },
                null),
        };

        foreach (var theme in new[] { ThemeName.Phosphor, ThemeName.Amber, ThemeName.Dracula, ThemeName.Light })
        {
            var palette = BuiltInThemes.Get(theme);
            foreach (var (name, context, progress, action) in states)
            {
                foreach (var width in new[] { 45, 60, 80, 120 })
                {
                    var model = StatusBarRenderer.ComposeStatusLine(
                        context,
                        context.ViewMode,
                        width,
                        progress,
                        readerTotalLines: context.ViewMode == ViewMode.Readable ? 200 : 0,
                        readerViewportHeight: 24,
                        requiredAction: action);

                    var line = StatusBarRenderer.FormatStatusLine(model, palette);
                    var plain = StripAnsi(line);

                    dump.WriteLine($"== {theme} · {name} · width {width} ==");
                    dump.WriteLine($"|{plain}|");

                    plain.Length.Should().BeLessThanOrEqualTo(width - 1 + plain.Count(char.IsSurrogate) / 2 + 8,
                        "sanity: the painted plain text tracks the composed budget");
                }

                dump.WriteLine();
            }
        }

        _output.WriteLine($"Visual dump written to: {dumpPath}");
        File.Exists(dumpPath).Should().BeTrue();
    }

    private static string StripAnsi(string input)
        => Regex.Replace(input, "\x1b\\[[0-9;]*[A-Za-z]", string.Empty);
}
