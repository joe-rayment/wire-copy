// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-c8v3 visual smoke. Renders the Hierarchical, Readable, and
/// Launcher views WITH and WITHOUT the prefetch detail overlay enabled at
/// three terminal widths so a human reviewer can confirm the overlay does
/// not wipe the status bar (the prior <c>RenderPreloadDetailOverlay</c>
/// implementation called <c>_helpers.Clear()</c> + <c>ClearRemainingLines()</c>
/// which would erase the chrome below). Captured output is ANSI-stripped
/// and dumped to <c>/tmp/wirecopy-preload-overlay-c8v3.txt</c>.
/// </summary>
[Trait("Category", "Unit")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)]
public sealed class PreloadDetailOverlayVisualSmoke
{
    private readonly ITestOutputHelper _output;

    public PreloadDetailOverlayVisualSmoke(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void DumpsHierarchicalAndLauncherWithOverlayOnOff()
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), "wirecopy-preload-overlay-c8v3.txt");
        using var dump = new StreamWriter(dumpPath);

        foreach (var width in new[] { 80, 100, 140 })
        {
            foreach (var showOverlay in new[] { false, true })
            {
                var rendered = RenderHierarchicalView(width, terminalHeight: 30, showOverlay);
                dump.WriteLine($"========================================");
                dump.WriteLine($"WIDTH {width}, HIERARCHICAL VIEW, OVERLAY={showOverlay}");
                dump.WriteLine($"========================================");
                dump.WriteLine(StripAnsi(rendered));
                dump.WriteLine();
            }
        }

        _output.WriteLine($"Visual dump written to: {dumpPath}");
        File.Exists(dumpPath).Should().BeTrue();
    }

    private static string RenderHierarchicalView(int terminalWidth, int terminalHeight, bool showOverlay)
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var logger = Substitute.For<ILogger<TerminalPageRenderer>>();
        var renderer = new TerminalPageRenderer(themeProvider, logger);

        var html = "<html><head><title>Demo</title></head><body>Demo body</body></html>";
        var metadata = new Domain.ValueObjects.Browser.PageMetadata { Title = "Demo article" };
        var page = Domain.Entities.Browser.Page.Create("https://example.com/article", html, metadata);
        var links = new List<Domain.ValueObjects.Browser.LinkInfo>
        {
            new() { Url = "https://example.com/article/one", DisplayText = "First link", Type = LinkType.Content, ImportanceScore = 80 },
            new() { Url = "https://example.com/article/two", DisplayText = "Second link", Type = LinkType.Content, ImportanceScore = 60 },
            new() { Url = "https://example.com/article/three", DisplayText = "Third link", Type = LinkType.Content, ImportanceScore = 40 },
        };
        page.SetLinkTree(Domain.Entities.Browser.NavigationTree.Build(links));
        var context = new Domain.ValueObjects.Browser.NavigationContext { ViewMode = ViewMode.Hierarchical };

        var preloadProgress = showOverlay ? BuildRepresentativeProgress() : null;
        var options = new RenderOptions
        {
            TerminalWidth = terminalWidth,
            TerminalHeight = terminalHeight,
            MaxContentWidth = terminalWidth - 4,
            ShowPreloadDetail = showOverlay,
            CacheProgress = preloadProgress,
        };

        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            renderer.RenderHierarchical(page, context, options);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return sw.ToString();
    }

    private static PreloadProgress BuildRepresentativeProgress()
    {
        return new PreloadProgress
        {
            TotalCacheableLinks = 12,
            CachedCount = 5,
            NeedsBrowserCount = 1,
            PaywalledLinkCount = 0,
            IsActivelyFetching = true,
            CurrentlyFetchingUrl = "https://www.example.com/long-article-url-that-might-need-truncation",
            CurrentStage = PreloadStage.ExtractingContent,
            UpcomingUrls = new List<string>
            {
                "https://example.com/queue/one",
                "https://example.com/queue/two",
                "https://example.com/queue/three",
            },
            RecentItems = new List<PreloadHistoryEntry>
            {
                new() { Url = "https://example.com/done/a", Outcome = PreloadOutcome.Cached, ElapsedMs = 423 },
                new() { Url = "https://example.com/done/b", Outcome = PreloadOutcome.Failed, ElapsedMs = 2150 },
                new() { Url = "https://example.com/done/c", Outcome = PreloadOutcome.Skipped, ElapsedMs = 87 },
            },
        };
    }

    private static string StripAnsi(string text)
    {
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
