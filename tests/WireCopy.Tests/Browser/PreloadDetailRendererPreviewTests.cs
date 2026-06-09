// Licensed under the MIT License. See LICENSE in the repository root.

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Hand-eye preview dump for the prefetch detail overlay (workspace-v75w).
/// Skipped by default — flip the Skip attribute to "" locally to regenerate
/// the snapshot at /tmp/preload_detail_preview.txt so a human can eyeball it.
///
/// Captures the FULL rendered output (title bar + borders + content +
/// bottom border + ANSI styling), then strips ANSI for human readability.
/// This is the snapshot the bead's test plan asks for — the previous version
/// only dumped <c>BuildPanelLines</c> interior content, missing the panel
/// chrome that the snapshot is supposed to verify (QA punch-list item 1).
/// </summary>
[Trait("Category", "Preview")]
[Collection(WireCopy.Tests.ConsoleSerialCollection.Name)] // workspace-dl21: serializes Console.Out swap
public class PreloadDetailRendererPreviewTests
{
    private static readonly Regex AnsiCsi = new("\x1b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    [Fact(Skip = "preview-only; run manually to regenerate the snapshot file")]
    public void DumpPreview()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);

        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 8,
            CachedCount = 3,
            NeedsBrowserCount = 1,
            IsActivelyFetching = true,
            CurrentStage = PreloadStage.ExtractingContent,
            CurrentlyFetchingUrl = "https://www.nytimes.com/2026/05/20/some/article-headline",
            UpcomingUrls = new[]
            {
                "https://www.nytimes.com/2026/05/20/world/europe/some-news.html",
                "https://www.nytimes.com/2026/05/20/business/article-two.html",
                "https://www.nytimes.com/2026/05/20/opinion/long/path/with/many/segments/here.html",
            },
            RecentItems = new[]
            {
                new PreloadHistoryEntry { Url = "https://www.nytimes.com/done-0", Outcome = PreloadOutcome.Cached, ElapsedMs = 850 },
                new PreloadHistoryEntry { Url = "https://www.nytimes.com/done-x-very-long-trailing-segment-here", Outcome = PreloadOutcome.Failed, ElapsedMs = 4200 },
                new PreloadHistoryEntry { Url = "https://www.nytimes.com/done-y", Outcome = PreloadOutcome.Skipped, ElapsedMs = 30 },
            },
        };

        var sb = new StringBuilder();
        foreach (var w in new[] { 80, 100, 120, 140 })
        {
            sb.AppendLine($"=== terminalWidth={w} (height=30) — rendered overlay including chrome ===");
            sb.AppendLine(CaptureRenderedAsPlain(progress, themeProvider, w, terminalHeight: 30));
            sb.AppendLine();
        }

        // Also capture the no-overlay path at a tiny terminal so the snapshot
        // documents what the user sees when the terminal is too small.
        sb.AppendLine("=== terminalWidth=30 (height=24) — too small, overlay suppressed ===");
        sb.AppendLine(CaptureRenderedAsPlain(progress, themeProvider, terminalWidth: 30, terminalHeight: 24));

        File.WriteAllText("/tmp/preload_detail_preview.txt", sb.ToString());
    }

    private static string CaptureRenderedAsPlain(PreloadProgress progress, IThemeProvider themeProvider, int terminalWidth, int terminalHeight)
    {
        var helpers = new RenderHelpers { TerminalHeight = terminalHeight };
        var renderer = new PreloadDetailRenderer(helpers, themeProvider);

        var originalOut = System.Console.Out;
        using var sw = new StringWriter();
        System.Console.SetOut(sw);
        try
        {
            renderer.Render(progress, terminalWidth, terminalHeight);
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }

        var raw = sw.ToString();
        var stripped = AnsiCsi.Replace(raw, string.Empty);
        // Strip cursor-position sequences too (already covered by AnsiCsi) and
        // collapse final null bytes that some terminal helpers emit.
        return stripped.Replace("\0", string.Empty);
    }
}
