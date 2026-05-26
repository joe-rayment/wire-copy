// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Tests;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// workspace-n49i Phase 4 visual smoke. Dumps the four result-screen
/// shapes (A/B/C plus the typed Shape-D failure tuple) to
/// <c>/tmp/wirecopy-result-screen-n49i.txt</c> at two terminal widths so a
/// reviewer can confirm the headline glyph, labelled metadata block,
/// shape-specific extras, and (for failure) the (Step, Reason, Fix) block
/// all render cleanly.
/// </summary>
namespace WireCopy.Tests.Podcast;

[Trait("Category", "Unit")]
[Collection(ConsoleSerialCollection.Name)]
public sealed class CompletionScreenVisualSmoke
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    private readonly ITestOutputHelper _output;

    public CompletionScreenVisualSmoke(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DumpsAllFourShapesAtTwoWidths()
    {
        var dumpPath = Path.Combine(Path.GetTempPath(), "wirecopy-result-screen-n49i.txt");
        using (var dump = new StreamWriter(dumpPath))
        {
            DumpAllFrames(dump);
        }

        _output.WriteLine($"Visual dump written to: {dumpPath}");
        File.Exists(dumpPath).Should().BeTrue();

        var content = File.ReadAllText(dumpPath);
        content.Should().Contain("Podcast generated and published",
            "Shape A headline must be present");
        content.Should().Contain("local-only",
            "Shape B headline must mark it as local-only");
        content.Should().Contain("2 article failures",
            "Shape C headline must include the failure count");
        content.Should().Contain("OpenAI API returned 429",
            "Shape D must show the typed Reason from the classifier");
        content.Should().Contain("allUsers:objectViewer",
            "Shape D must surface the bucket-public IAM grant remediation when the typed detail is BucketNotPublic (workspace-3a2k → workspace-p1px)");
    }

    private static void DumpAllFrames(StreamWriter dump)
    {

        foreach (var width in new[] { 80, 100 })
        {
            // ---- Shape A: full success + RSS publish ----
            DumpFrame(
                dump,
                width,
                "SHAPE A — FULL SUCCESS + PUBLISH",
                PodcastResult.Successful(
                    feedUrl: "https://storage.googleapis.com/my-bucket/feed.xml",
                    localFilePath: "/home/user/.local/share/WireCopy/output/reading-list-2026-05-20.m4b",
                    totalDuration: TimeSpan.FromMinutes(38),
                    articlesProcessed: 12,
                    articlesFailed: 0,
                    fileSizeBytes: 38L * 1024 * 1024,
                    articlesCached: 3,
                    totalCost: 0.71m));

            // ---- Shape B: local-only success ----
            DumpFrame(
                dump,
                width,
                "SHAPE B — LOCAL-ONLY SUCCESS",
                PodcastResult.Successful(
                    feedUrl: null,
                    localFilePath: "/home/user/.local/share/WireCopy/output/reading-list-2026-05-20.m4b",
                    totalDuration: TimeSpan.FromMinutes(38),
                    articlesProcessed: 12,
                    articlesFailed: 0,
                    fileSizeBytes: 38L * 1024 * 1024,
                    articlesCached: 3,
                    totalCost: 0.71m));

            // ---- Shape C: partial failure ----
            var failures = new[]
            {
                new ArticleFailure { Title = "NYT article", Url = "https://nyt.com/2026/05/article", Reason = "paywall detected (HITL required)" },
                new ArticleFailure { Title = "Example article", Url = "https://example.com/x", Reason = "extraction returned <100 words (quality gate)" },
            };
            DumpFrame(
                dump,
                width,
                "SHAPE C — PARTIAL FAILURE (2 articles failed)",
                PodcastResult.Successful(
                    feedUrl: null,
                    localFilePath: "/home/user/.local/share/WireCopy/output/reading-list-2026-05-20.m4b",
                    totalDuration: TimeSpan.FromMinutes(32),
                    articlesProcessed: 10,
                    articlesFailed: 2,
                    fileSizeBytes: 32L * 1024 * 1024,
                    articlesCached: 2,
                    totalCost: 0.64m,
                    failedArticleDetails: failures));

            // ---- Shape D classification (rendered separately by ShowErrorScreenAsync) ----
            var d_429 = PodcastFailureClassifier.Classify(
                "OpenAI API returned 429 (rate_limit_exceeded) while synthesizing article 3 of 12",
                Array.Empty<ArticleFailure>());
            dump.WriteLine("========================================");
            dump.WriteLine($"WIDTH {width}, SHAPE D — TOTAL FAILURE (typed classification — 429 example)");
            dump.WriteLine("========================================");
            dump.WriteLine($"  ✗ Podcast generation failed");
            dump.WriteLine();
            dump.WriteLine($"  At step:     {d_429.Step}");
            dump.WriteLine($"  Reason:      {d_429.Reason}");
            dump.WriteLine($"  Fix:         {d_429.Fix}");
            dump.WriteLine();
            dump.WriteLine("  Enter:back");
            dump.WriteLine();

            // ---- Shape D via typed PodcastFailureDetail (workspace-3a2k Phase E) ----
            // Exercises the typed-classification path the orchestrator now
            // takes when publish fails with a configured bucket. Confirms the
            // bucket-public IAM grant remediation lands verbatim in the
            // Fix line — the bead's headline acceptance criterion.
            var bucketPublicDetail = new PodcastFailureDetail(
                Step: "Publishing",
                FailureClass: FeedPublishFailureClass.BucketNotPublic,
                RawMessage: "feed.xml uploaded but URL returned HTTP 403 from public internet.",
                RemediationCopy:
                    "feed.xml uploaded but the bucket isn't world-readable. Grant allUsers:objectViewer on "
                    + "the bucket (Cloud Console → Buckets → Permissions → Add: allUsers, role Storage "
                    + "Object Viewer), or run `gsutil iam ch allUsers:objectViewer gs://<your-bucket>`.");
            var d_typed = PodcastFailureClassifier.Classify(
                bucketPublicDetail,
                errorMessage: bucketPublicDetail.RawMessage,
                failedArticles: Array.Empty<ArticleFailure>());
            dump.WriteLine("========================================");
            dump.WriteLine($"WIDTH {width}, SHAPE D — TOTAL FAILURE (workspace-3a2k typed bucket-not-public)");
            dump.WriteLine("========================================");
            dump.WriteLine($"  ✗ Podcast generation failed");
            dump.WriteLine();
            dump.WriteLine($"  At step:     {d_typed.Step}");
            dump.WriteLine($"  Reason:      {d_typed.Reason}");
            dump.WriteLine($"  Fix:         {d_typed.Fix}");
            dump.WriteLine();
            dump.WriteLine("  Enter:back");
            dump.WriteLine();
        }
    }

    private static void DumpFrame(StreamWriter dump, int width, string title, PodcastResult result)
    {
        dump.WriteLine("========================================");
        dump.WriteLine($"WIDTH {width}, {title}");
        dump.WriteLine("========================================");
        var lines = PodcastProgressScreens.BuildCompletionLines(Palette, result, width);
        foreach (var line in lines)
        {
            dump.WriteLine(StripAnsi(line));
        }

        dump.WriteLine();
    }

    private static string StripAnsi(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '[')
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

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }
}
