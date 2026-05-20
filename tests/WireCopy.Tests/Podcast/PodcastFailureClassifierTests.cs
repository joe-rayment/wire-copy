// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
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
}
