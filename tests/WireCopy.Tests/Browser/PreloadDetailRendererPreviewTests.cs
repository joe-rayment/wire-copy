// Licensed under the MIT License. See LICENSE in the repository root.

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Hand-eye preview dump for the prefetch detail overlay (workspace-v75w).
/// Skipped by default — flip the Skip attribute to "" locally to regenerate
/// the snapshot at /tmp/preload_detail_preview.txt so a human can eyeball it.
/// </summary>
[Trait("Category", "Preview")]
public class PreloadDetailRendererPreviewTests
{
    [Fact(Skip = "preview-only; run manually to regenerate the snapshot file")]
    public void DumpPreview()
    {
        var palette = BuiltInThemes.Get(ThemeName.Phosphor);
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
            sb.AppendLine($"=== terminalWidth={w} ===");
            var lines = PreloadDetailRenderer.BuildPanelLines(progress, palette, w);
            foreach (var l in lines)
            {
                sb.AppendLine($"| {l.PlainText}");
            }

            sb.AppendLine();
        }

        File.WriteAllText("/tmp/preload_detail_preview.txt", sb.ToString());
    }
}
