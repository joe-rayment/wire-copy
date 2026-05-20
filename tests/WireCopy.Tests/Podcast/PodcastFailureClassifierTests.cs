// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// workspace-n49i Phase 4 — typed failure classification tests. Each common
/// failure mode (FFmpeg, missing key, 429, 401, 403, 5xx, GCS, network,
/// content extraction) maps to a typed (Step, Reason, Fix) tuple that the
/// Shape D result screen renders. Generic fallback keeps the screen honest
/// when the error doesn't match anything specific.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastFailureClassifierTests
{
    private static IReadOnlyList<ArticleFailure> NoFailures => Array.Empty<ArticleFailure>();

    [Fact]
    public void Classify_FFmpegMissing_PointsAtInstallCommand()
    {
        var c = PodcastFailureClassifier.Classify(
            "FFmpeg is not installed or not found in PATH.",
            NoFailures);

        c.Step.Should().Contain("audio assembly");
        c.Reason.Should().Contain("FFmpeg");
        c.Fix.Should().Contain("brew install ffmpeg");
        c.Fix.Should().Contain("apt install ffmpeg");
    }

    [Fact]
    public void Classify_MissingApiKey_PointsAtSetup()
    {
        var c = PodcastFailureClassifier.Classify("TTS service is not configured", NoFailures);

        c.Step.Should().Contain("credentials");
        c.Reason.Should().Contain("OpenAI API key");
        c.Fix.Should().Contain("Setup");
    }

    [Fact]
    public void Classify_RateLimit429_PointsAtUsagePage()
    {
        var c = PodcastFailureClassifier.Classify(
            "OpenAI returned status 429 (rate_limit_exceeded)",
            NoFailures);

        c.Step.Should().Contain("TTS");
        c.Reason.Should().Contain("429");
        c.Fix.Should().Contain("platform.openai.com/usage");
    }

    [Fact]
    public void Classify_Unauthorized401_PointsAtApiKeysPage()
    {
        var c = PodcastFailureClassifier.Classify("HTTP 401 unauthorized", NoFailures);

        c.Step.Should().Contain("authentication");
        c.Fix.Should().Contain("platform.openai.com/api-keys");
    }

    [Fact]
    public void Classify_Forbidden403_PointsAtBilling()
    {
        var c = PodcastFailureClassifier.Classify("HTTP 403 insufficient_credits", NoFailures);

        c.Step.Should().Contain("authorization");
        c.Fix.Should().Contain("platform.openai.com/billing");
    }

    [Fact]
    public void Classify_ServerError5xx_AdvisesRetry()
    {
        var c = PodcastFailureClassifier.Classify("OpenAI 502 Bad Gateway", NoFailures);

        c.Reason.Should().Contain("5xx");
        c.Fix.Should().Contain("Retry");
    }

    [Fact]
    public void Classify_GcsBucketProblem_OffersGsutilCommand()
    {
        var c = PodcastFailureClassifier.Classify(
            "Failed to upload feed.xml to GCS bucket: 403 Forbidden",
            NoFailures);

        c.Step.Should().Contain("publish");
        c.Fix.Should().Contain("gsutil iam");
        c.Fix.Should().Contain("allUsers:objectViewer");
    }

    [Fact]
    public void Classify_NetworkError_AdvisesConnectionCheck()
    {
        var c = PodcastFailureClassifier.Classify("DNS resolution failed for api.openai.com", NoFailures);

        c.Step.Should().Be("Network");
        c.Fix.Should().Contain("internet connection");
    }

    [Fact]
    public void Classify_ContentExtractionFailure_AdvisesBrowserPreload()
    {
        var failures = new[]
        {
            new ArticleFailure { Title = "Article 1", Url = "https://example.com/1", Reason = "challenge-platform detected" },
        };

        var c = PodcastFailureClassifier.Classify("No readable articles available", failures);

        c.Step.Should().Be("Content extraction");
        c.Fix.Should().Contain("browser");
    }

    [Fact]
    public void Classify_CaptchaInArticleFailure_TriggersContentExtractionClass()
    {
        // Even if the top-level error message is generic, a captcha keyword
        // in the per-article reason should pick up the content-extraction
        // classification.
        var failures = new[]
        {
            new ArticleFailure { Title = "Locked article", Url = "https://example.com/x", Reason = "captcha required" },
        };

        var c = PodcastFailureClassifier.Classify("All articles failed", failures);

        c.Step.Should().Be("Content extraction");
    }

    [Fact]
    public void Classify_UnknownError_FallsBackToGenericTuple()
    {
        var c = PodcastFailureClassifier.Classify(
            "Some completely unexpected new failure mode",
            NoFailures);

        c.Step.Should().Be("Unknown");
        c.Reason.Should().Contain("Some completely unexpected");
        c.Fix.Should().Contain("logs");
    }

    [Fact]
    public void Classify_NullErrorMessage_FallsBackToGenericWithSensibleReason()
    {
        var c = PodcastFailureClassifier.Classify(null, NoFailures);

        c.Step.Should().Be("Unknown");
        c.Reason.Should().NotBeNullOrWhiteSpace("the result screen never wants to render an empty Reason line");
    }

    [Fact]
    public void Classify_PreflightWinsOverGenericNetwork_WhenBothKeywordsPresent()
    {
        // Defensive ordering: "FFmpeg missing because the network is down" —
        // FFmpeg is more specific and actionable than the network fallback.
        var c = PodcastFailureClassifier.Classify(
            "FFmpeg not found; ffmpeg-check ran with network timeout",
            NoFailures);

        c.Step.Should().Contain("audio assembly");
    }

    /// <summary>
    /// workspace-3a2k Phase E: when the orchestrator attaches a typed
    /// <see cref="PodcastFailureDetail"/>, the classifier must surface its
    /// fields verbatim instead of pattern-matching on the error string. This
    /// preserves the bucket-public remediation copy through the pipeline so
    /// the Shape D screen tells the user exactly what to do — not some
    /// heuristic best-guess.
    /// </summary>
    [Fact]
    public void Classify_TypedDetail_BypassesHeuristic_AndReturnsItsFieldsVerbatim()
    {
        var detail = new PodcastFailureDetail(
            Step: "Publishing",
            FailureClass: FeedPublishFailureClass.FeedNotReachable,
            RawMessage: "Anonymous HTTP GET returned 403 — bucket may not grant allUsers:objectViewer.",
            RemediationCopy: "Grant allUsers:objectViewer on the bucket, or run gsutil iam ch allUsers:objectViewer gs://<your-bucket>.");

        var c = PodcastFailureClassifier.Classify(
            detail,
            errorMessage: "Anonymous HTTP GET returned 403 — bucket may not grant allUsers:objectViewer.",
            failedArticles: NoFailures);

        c.Step.Should().Be("Publishing");
        c.Reason.Should().Contain("403");
        c.Fix.Should().Contain("allUsers:objectViewer",
            "the bucket-public remediation MUST land in the Fix line — the bead's headline acceptance");
    }

    [Fact]
    public void Classify_TypedDetailNull_FallsBackToHeuristic()
    {
        var c = PodcastFailureClassifier.Classify(
            typedDetail: null,
            errorMessage: "FFmpeg is not installed or not found in PATH.",
            failedArticles: NoFailures);

        c.Step.Should().Contain("audio assembly",
            "the legacy heuristic path must still run when the orchestrator didn't attach a typed detail");
    }

    [Fact]
    public void Classify_TypedDetailWithEmptyRawMessage_FallsBackToFailureClassName()
    {
        // Defensive: if for some reason RawMessage is empty (shouldn't happen
        // but cheap to guard), the Reason line should not be blank — fall
        // back to the FailureClass enum name so the user still sees what
        // broke.
        var detail = new PodcastFailureDetail(
            Step: "Publishing",
            FailureClass: FeedPublishFailureClass.FeedNotParseable,
            RawMessage: string.Empty,
            RemediationCopy: "Retry the publish — encoding/transfer issue in the just-uploaded blob.");

        var c = PodcastFailureClassifier.Classify(detail, errorMessage: null, failedArticles: NoFailures);

        c.Reason.Should().Be("FeedNotParseable",
            "an empty raw message must not render an empty Reason — fall back to the FailureClass");
    }
}
