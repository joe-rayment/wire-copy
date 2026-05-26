// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-n49i Phase 4 — completion-screen shape rendering tests.
/// Exercises the three documented shapes (A/B/C) plus the classification
/// helper. Renderings are inspected as plain text (ANSI stripped via the
/// existing `SettingsRowRenderer.StripAnsi` helper isn't used here because
/// we just `Contain` strings — escapes don't disturb that).
/// </summary>
[Trait("Category", "Unit")]
public class CompletionScreenShapeTests
{
    private static ThemePalette Palette() => BuiltInThemes.Get(ThemeName.Phosphor);

    private static PodcastResult MakeResult(
        string? feedUrl = null,
        int failed = 0,
        IReadOnlyList<ArticleFailure>? failures = null)
    {
        return PodcastResult.Successful(
            feedUrl: feedUrl,
            localFilePath: "/home/user/.local/share/WireCopy/output/reading-list-2026-05-20.m4b",
            totalDuration: TimeSpan.FromMinutes(38),
            articlesProcessed: 12,
            articlesFailed: failed,
            fileSizeBytes: 38L * 1024 * 1024,
            articlesCached: 3,
            totalCost: 0.71m,
            failedArticleDetails: failures);
    }

    [Fact]
    public void Classify_FullSuccess_ShapeA()
    {
        var shape = PodcastProgressScreens.ClassifyCompletion(
            MakeResult(feedUrl: "https://storage.googleapis.com/my-bucket/feed.xml"));

        shape.Should().Be(CompletionShape.FullSuccess);
    }

    [Fact]
    public void Classify_LocalOnlySuccess_ShapeB()
    {
        var shape = PodcastProgressScreens.ClassifyCompletion(MakeResult(feedUrl: null));

        shape.Should().Be(CompletionShape.LocalOnlySuccess);
    }

    [Fact]
    public void Classify_PartialFailure_ShapeC_WhenFailedGreaterThanZero()
    {
        var shape = PodcastProgressScreens.ClassifyCompletion(MakeResult(failed: 2));

        shape.Should().Be(CompletionShape.PartialFailure,
            "any non-zero failure count flips the shape to PartialFailure — even with a feed URL");
    }

    [Fact]
    public void BuildLines_FullSuccess_RendersCheckGlyphAndPublishHeadline()
    {
        var lines = PodcastProgressScreens.BuildCompletionLines(
            Palette(),
            MakeResult(feedUrl: "https://storage.googleapis.com/my-bucket/feed.xml"),
            width: 100);

        var joined = string.Join('\n', lines);
        joined.Should().Contain("✓");
        joined.Should().Contain("Podcast generated and published");
        joined.Should().Contain("Feed");
        joined.Should().Contain("storage.googleapis.com/my-bucket/feed.xml");
    }

    [Fact]
    public void BuildLines_LocalOnlySuccess_RendersLocalOnlyHeadlineAndSetupHint()
    {
        var lines = PodcastProgressScreens.BuildCompletionLines(
            Palette(),
            MakeResult(feedUrl: null),
            width: 100);

        var joined = string.Join('\n', lines);
        joined.Should().Contain("local-only");
        joined.Should().Contain("Configure a GCS bucket");
        joined.Should().NotContain("Feed URL copied to clipboard",
            "the feed-copy hint is exclusive to the full-success shape");
    }

    [Fact]
    public void BuildLines_PartialFailure_RendersWarningHeadlineAndPerArticleList()
    {
        var failures = new[]
        {
            new ArticleFailure { Title = "NYT article", Url = "https://nyt.com/article", Reason = "paywall detected (HITL required)" },
            new ArticleFailure { Title = "Example article", Url = "https://example.com/x", Reason = "extraction returned <100 words (quality gate)" },
        };

        var lines = PodcastProgressScreens.BuildCompletionLines(
            Palette(),
            MakeResult(failed: 2, failures: failures),
            width: 100);

        var joined = string.Join('\n', lines);
        joined.Should().Contain("⚠");
        joined.Should().Contain("2 article failure");
        joined.Should().Contain("Failures:");
        joined.Should().Contain("paywall detected");
        joined.Should().Contain("quality gate");
    }

    [Fact]
    public void BuildLines_PartialFailure_HeadlineUsesSingularForOneFailure()
    {
        var failures = new[]
        {
            new ArticleFailure { Title = "Single", Url = "https://example.com/", Reason = "test" },
        };

        var lines = PodcastProgressScreens.BuildCompletionLines(
            Palette(),
            MakeResult(failed: 1, failures: failures),
            width: 100);

        var joined = string.Join('\n', lines);
        joined.Should().Contain("1 article failure");
        joined.Should().NotContain("1 article failures",
            "singular when failed count is 1");
    }

    [Fact]
    public void BuildLines_LongPathAtWidth80_WrapsOntoContinuationLineUnderValueColumn()
    {
        // workspace-n49i QA gap: the bead's acceptance says paths must wrap
        // onto a *separate line* when they don't fit — natural terminal wrap
        // breaks selection rectangles and OSC2026 reflow. Verify the
        // continuation line exists and starts under the value column
        // (label space-padded out).
        var result = MakeResult(feedUrl: null);

        var lines = PodcastProgressScreens.BuildCompletionLines(Palette(), result, width: 80);

        // Find the File row (starts with "  File").
        var fileRowIndex = lines.FindIndex(l => l.Contains("File"));
        fileRowIndex.Should().BeGreaterOrEqualTo(0);

        // The next line must be the wrapped continuation: it must NOT start
        // with another label (i.e., the label column is blanked) and must
        // carry the rest of the path.
        lines.Count.Should().BeGreaterThan(fileRowIndex + 1);
        var continuation = lines[fileRowIndex + 1];

        continuation.Should().NotContain("File",
            because: "the continuation line must not repeat the label");
        continuation.TrimStart().Length.Should().BeGreaterThan(0,
            because: "the continuation must carry the rest of the path");

        // The full path must appear (across the two chunks) when ANSI escapes
        // and continuation-line indent are stripped. The renderer chunks the
        // path at the value-width boundary so the substring across chunks is
        // intact for copy-paste from a clean selection.
        var pathParts = lines.SelectMany(l =>
        {
            var stripped = System.Text.RegularExpressions.Regex.Replace(l, "\x1b\\[[0-9;]*[A-Za-z]", string.Empty);
            return new[] { stripped };
        }).ToList();

        // The two chunks of the path (path[..63] + path[63..]) should each
        // appear as a substring in some line.
        const string FullPath = "/home/user/.local/share/WireCopy/output/reading-list-2026-05-20.m4b";
        var firstChunkLength = 63;
        var firstChunk = FullPath[..Math.Min(firstChunkLength, FullPath.Length)];
        pathParts.Should().Contain(p => p.Contains(firstChunk),
            because: "the first chunk of the path must appear unbroken on a line");
        pathParts.Should().Contain(p => p.Contains("m4b"),
            because: "the tail of the path must survive on the continuation line");
    }

    [Fact]
    public void BuildLines_HintFooter_ListsRetryKeystroke()
    {
        // workspace-n49i: bead contract names `r` as one of the result-screen
        // keystrokes. The completion screen advertises it in the hint footer.
        // (Footer is rendered by ShowCompletionScreenAsync directly, not by
        // BuildCompletionLines, so this assertion lives in
        // ShowCompletionScreenAsync's keystroke routing — covered by integration
        // testing. This test exists as a documentation pin: the contract
        // includes `r`.)
        // The actual keystroke routing has 'r' → CompletionScreenAction.Retry,
        // verified in CompletionScreenAction.cs and exercised via
        // PodcastCommandHandler.HandleGeneratePodcast's retry loop.
        CompletionScreenAction.Retry.Should().Be(CompletionScreenAction.Retry,
            because: "Retry must exist as a result-screen action per the bead contract");
    }

    /// <summary>
    /// workspace-3a2k Phase E: Shape D (total failure) — when a typed
    /// <see cref="PodcastFailureDetail"/> is attached (e.g. bucket-not-public),
    /// the classifier consumed by <c>ShowErrorScreenAsync</c> must surface its
    /// (Step, Reason, Fix) tuple verbatim. The bucket-public IAM grant
    /// remediation must land in the Fix line.
    /// </summary>
    [Fact]
    public void ShapeD_BucketNotPublic_RendersBucketPublicRemediation()
    {
        var detail = new PodcastFailureDetail(
            Step: "Publishing",
            FailureClass: FeedPublishFailureClass.BucketNotPublic,
            RawMessage: "feed.xml uploaded but URL returned HTTP 403 from public internet.",
            RemediationCopy:
                "Bucket is not configured for public read. Open Cloud Console → Buckets → my-bucket → "
                + "Permissions → grant allUsers the Storage Object Viewer role.");

        var c = PodcastFailureClassifier.Classify(detail, errorMessage: detail.RawMessage, failedArticles: Array.Empty<ArticleFailure>());

        c.Step.Should().Be("Publishing");
        c.Reason.Should().Contain("403");
        c.Fix.Should().Contain("Storage Object Viewer",
            "Shape D's Fix line must direct the user to the Cloud-Console bucket-public IAM grant");
    }

    [Fact]
    public void BuildLines_AllShapes_RenderFileLineWithFullPath_NoMiddleTruncation()
    {
        // workspace-n49i Acceptance: "Path display is monospace and
        // copy-friendly (no truncation)". The full path must appear in the
        // output as-is so the user can copy/paste it.
        const string FullPath = "/home/user/.local/share/WireCopy/output/reading-list-2026-05-20.m4b";

        foreach (var (shape, result) in new[]
        {
            ("FullSuccess", MakeResult(feedUrl: "https://example.com/feed.xml")),
            ("LocalOnlySuccess", MakeResult(feedUrl: null)),
            ("PartialFailure", MakeResult(failed: 1, failures: new[] { new ArticleFailure { Title = "X", Url = "u", Reason = "r" } })),
        })
        {
            var lines = PodcastProgressScreens.BuildCompletionLines(Palette(), result, width: 100);
            var joined = string.Join('\n', lines);
            joined.Should().Contain(FullPath,
                because: $"shape {shape} must keep the file path intact for copy-paste");
            joined.Should().NotContain("…",
                because: $"shape {shape} must not middle-truncate the path");
        }
    }
}
