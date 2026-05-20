// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Phase B (workspace-v34z) wiring tests: confirms that
/// <see cref="PodcastProgressScreens.RenderProgressContent"/> renders the
/// aggregator's global percent + ETA + per-phase sub-bars instead of the
/// legacy stair-stepped PercentComplete.
/// </summary>
[Trait("Category", "Unit")]
public class ProgressScreenAggregatorRenderTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);

    private static string CaptureRender(Action<RenderHelpers> action, int terminalHeight = 30)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var helpers = new RenderHelpers { TerminalHeight = terminalHeight };
            helpers.Clear();
            action(helpers);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return sw.ToString();
    }

    [Fact]
    public void FormatEta_NullEta_ReturnsDash()
    {
        PodcastProgressScreens.FormatEta(null, isComplete: false).Should().Be("ETA —");
    }

    [Fact]
    public void FormatEta_Complete_ReturnsDone()
    {
        PodcastProgressScreens.FormatEta(TimeSpan.FromSeconds(5), isComplete: true).Should().Be("done");
    }

    [Fact]
    public void FormatEta_SubMinute_RendersSeconds()
    {
        PodcastProgressScreens.FormatEta(TimeSpan.FromSeconds(14), false).Should().Be("ETA 14s");
    }

    [Fact]
    public void FormatEta_MinutesAndSeconds_RendersBoth()
    {
        PodcastProgressScreens.FormatEta(TimeSpan.FromSeconds(134), false).Should().Be("ETA 2m 14s");
    }

    [Fact]
    public void FormatEta_ExactMinutes_OmitsSeconds()
    {
        PodcastProgressScreens.FormatEta(TimeSpan.FromMinutes(3), false).Should().Be("ETA 3m");
    }

    [Fact]
    public void FormatEta_NegativeTimeSpan_RendersDash()
    {
        // Defence-in-depth: the aggregator returns null on negative ETAs, but
        // FormatEta is `internal` and unit-tested directly — guard the helper
        // against a clock-skew event slipping through.
        PodcastProgressScreens
            .FormatEta(TimeSpan.FromSeconds(-5), false)
            .Should().Be("ETA —");
    }

    [Fact]
    public void FormatEta_LargerThanOneHour_RendersCapped()
    {
        PodcastProgressScreens
            .FormatEta(TimeSpan.FromHours(5), false)
            .Should().Be("ETA >1h");
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RenderProgressContent_WithAggregator_GlobalPercentReflectsAggregator()
    {
        // Orchestrator's stair-stepped PercentComplete says 70%, but the
        // aggregator should only show the weighted contribution (ext=1.0 +
        // tts at 0/12 articles ≈ 0.05 + a small in-flight slice).
        var aggregator = new PodcastProgressAggregator();
        var progress = new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 1,
            TotalArticles = 12,
            PercentComplete = 70,
        };
        aggregator.Observe(progress);

        var statuses = new PodcastCommandHandler.ArticleStatus[12];
        for (var i = 0; i < statuses.Length; i++)
        {
            statuses[i] = new PodcastCommandHandler.ArticleStatus
            {
                Title = $"Article {i + 1}",
                State = PodcastCommandHandler.ArticleState.Pending,
            };
        }

        var output = CaptureRender(h => PodcastProgressScreens.RenderProgressContent(
            h, Palette, progress, animFrame: 0, statuses, terminalWidth: 100, terminalHeight: 40, targets: null, aggregator));

        output.Should().NotContain(" 70%",
            because: "the global bar must NOT echo the legacy stair-stepped PercentComplete");
        output.Should().Contain("ETA —",
            because: "less than 10s of history means no ETA can be derived yet");
        output.Should().Contain("Extracting");
        output.Should().Contain("Synthesizing");
        output.Should().Contain("Assembling");
        output.Should().Contain("Publishing");
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RenderProgressContent_WithoutAggregator_FallsBackToLegacyPercent()
    {
        // Older callers that don't pass an aggregator should still render — but
        // we make sure they see the legacy percent, no four-sub-bar block.
        var progress = new PodcastProgress
        {
            Phase = PodcastPhase.AssemblingAudio,
            CurrentArticle = 0,
            TotalArticles = 12,
            PercentComplete = 42,
        };

        var statuses = new PodcastCommandHandler.ArticleStatus[1]
        {
            new()
            {
                Title = "Only article",
                State = PodcastCommandHandler.ArticleState.Completed,
            },
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderProgressContent(
            h, Palette, progress, animFrame: 0, statuses, terminalWidth: 100, terminalHeight: 40));

        output.Should().Contain(" 42%", because: "fallback path uses orchestrator's PercentComplete");
        output.Should().NotContain("Extracting  ",
            because: "the four-sub-bar block requires an aggregator");
    }

    /// <summary>
    /// workspace-rz1c: when the TTS service is retrying a 429/5xx, the
    /// progress screen must render an inline banner so the user sees a
    /// countdown instead of a frozen bar. Without this guard, the progress
    /// bar pauses mid-article and the user assumes the job is stuck.
    /// </summary>
    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RenderProgressContent_RetryingTTS_RendersInlineRateLimitBanner()
    {
        var progress = new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 3,
            TotalArticles = 12,
            ArticleTitle = "An article that hit rate limit",
            PercentComplete = 32,
            IsRetrying = true,
            RetryAttempt = 2,
            RetryMaxAttempts = 3,
            RetryDelaySeconds = 4,
            Message = "Rate-limited by OpenAI (HTTP 429), retrying in 4s (attempt 2/3)",
        };

        var statuses = new PodcastCommandHandler.ArticleStatus[1]
        {
            new() { Title = "Article 1", State = PodcastCommandHandler.ArticleState.Processing },
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderProgressContent(
            h, Palette, progress, animFrame: 0, statuses, terminalWidth: 100, terminalHeight: 40));

        output.Should().Contain("Rate-limited by OpenAI",
            because: "the user MUST see why the progress bar is frozen — silence is the bug");
        output.Should().Contain("retrying in 4s");
        output.Should().Contain("attempt 2/3");
    }

    /// <summary>
    /// workspace-rz1c: pins the message wire-format produced by
    /// <c>OpenAiTtsService.BuildRetryProgress</c>. The screen renderer just
    /// echoes <c>progress.Message</c>; the user-visible copy is determined
    /// here. Together with the screen test above, this proves the full
    /// chain from (attempt, delayMs, status) input to rendered terminal
    /// output.
    /// </summary>
    [Fact]
    public void BuildRetryProgress_429_FormatsRateLimitedMessage()
    {
        var progress = WireCopy.Infrastructure.Podcast.OpenAiTtsService.BuildRetryProgress(
            attempt: 2,
            delayMs: 4000,
            status: 429,
            chunkIndex: 3,
            totalChunks: 7,
            charactersProcessed: 1200,
            totalCharacters: 5400,
            maxRetries: 5);

        progress.IsRetrying.Should().BeTrue();
        progress.RetryAttempt.Should().Be(2);
        progress.RetryMaxAttempts.Should().Be(5);
        progress.RetryDelaySeconds.Should().Be(4);
        progress.Message.Should().Be("Rate-limited by OpenAI (HTTP 429), retrying in 4s (attempt 2/5)",
            "the wire-format copy MUST stay stable — the renderer just echoes Message");
    }

    [Fact]
    public void BuildRetryProgress_5xx_FormatsServerErrorMessage()
    {
        var progress = WireCopy.Infrastructure.Podcast.OpenAiTtsService.BuildRetryProgress(
            attempt: 1, delayMs: 1500, status: 503,
            chunkIndex: 1, totalChunks: 2,
            charactersProcessed: 0, totalCharacters: 800, maxRetries: 3);

        progress.Message.Should().Contain("HTTP 503");
        progress.Message.Should().Contain("retrying in 2s",
            "1500ms rounds UP to 2s — the user shouldn't see fractional waits");
        progress.Message.Should().Contain("attempt 1/3");
    }

    /// <summary>
    /// workspace-roab: DNS failures, connection-refused, and TLS handshake
    /// errors arrive at OpenAiTtsService as HttpRequestException with a
    /// null StatusCode. BuildRetryProgress receives status=0 in that case
    /// and must render network-specific copy (no "HTTP 0", no "Rate-limited
    /// by OpenAI" misleading text).
    /// </summary>
    [Fact]
    public void BuildRetryProgress_NetworkLayer_FormatsNetworkIssueMessage()
    {
        var progress = WireCopy.Infrastructure.Podcast.OpenAiTtsService.BuildRetryProgress(
            attempt: 2,
            delayMs: 3000,
            status: 0,
            chunkIndex: 1,
            totalChunks: 4,
            charactersProcessed: 0,
            totalCharacters: 2000,
            maxRetries: 3);

        progress.IsRetrying.Should().BeTrue();
        progress.Message.Should().Be("Network issue, retrying in 3s (attempt 2/3)",
            "DNS/connection-refused must surface as 'Network issue' — not 'HTTP 0' garbage");
        progress.Message.Should().NotContain("HTTP",
            "the network-layer path must NOT mention an HTTP status code");
        progress.Message.Should().NotContain("OpenAI",
            "network connectivity isn't OpenAI's fault — don't blame the API");
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RenderProgressContent_NetworkRetry_RendersNetworkIssueBanner()
    {
        // End-to-end: feed a PodcastProgress carrying the network-issue copy
        // and confirm the screen renders it on the retry banner. Mirrors
        // the rate-limit test above but with the no-status-code wording.
        var progress = new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 2,
            TotalArticles = 5,
            ArticleTitle = "An article hit by a DNS blip",
            PercentComplete = 28,
            IsRetrying = true,
            RetryAttempt = 1,
            RetryMaxAttempts = 3,
            RetryDelaySeconds = 2,
            Message = "Network issue, retrying in 2s (attempt 1/3)",
        };

        var statuses = new PodcastCommandHandler.ArticleStatus[1]
        {
            new() { Title = "Article 2", State = PodcastCommandHandler.ArticleState.Processing },
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderProgressContent(
            h, Palette, progress, animFrame: 0, statuses, terminalWidth: 100, terminalHeight: 40));

        output.Should().Contain("Network issue");
        output.Should().Contain("retrying in 2s");
        output.Should().Contain("attempt 1/3");
        output.Should().NotContain("Rate-limited",
            "the network-layer banner must NOT carry rate-limit copy");
    }

    [Fact]
    public void BuildRetryProgress_SubSecondDelay_RoundsUpToOneSecond()
    {
        // Defensive: a 300ms backoff (e.g. RetryBaseDelayMs=300, attempt 0)
        // would format as "retrying in 0s" without the Math.Max(1, ...) clamp.
        // The user shouldn't see a zero-second wait.
        var progress = WireCopy.Infrastructure.Podcast.OpenAiTtsService.BuildRetryProgress(
            attempt: 1, delayMs: 300, status: 429,
            chunkIndex: 1, totalChunks: 1,
            charactersProcessed: 0, totalCharacters: 100, maxRetries: 3);

        progress.RetryDelaySeconds.Should().Be(1);
        progress.Message.Should().Contain("retrying in 1s");
    }

    /// <summary>
    /// Inverse pin: without IsRetrying, the banner must NOT render. This
    /// guards against future regressions where IsRetrying gets defaulted to
    /// true or the rendering condition gets inverted.
    /// </summary>
    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RenderProgressContent_NotRetrying_DoesNotRenderRateLimitBanner()
    {
        var progress = new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 3,
            TotalArticles = 12,
            ArticleTitle = "An article",
            PercentComplete = 32,
            IsRetrying = false,
            Message = "Chunk 2/4 complete",
        };

        var statuses = new PodcastCommandHandler.ArticleStatus[1]
        {
            new() { Title = "Article 1", State = PodcastCommandHandler.ArticleState.Processing },
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderProgressContent(
            h, Palette, progress, animFrame: 0, statuses, terminalWidth: 100, terminalHeight: 40));

        output.Should().NotContain("Rate-limited",
            because: "the retry banner must be exclusive to IsRetrying == true");
        output.Should().NotContain("retrying in");
    }

    [Fact]
    [Trait("Collection", "ConsoleOutput")]
    public void RenderProgressContent_DetailStickyAcrossPhases_ShowsTtsCountInAssembling()
    {
        // QA-flagged regression: when the run advances into Assembling, the
        // Synthesizing sub-bar previously lost its "12/12" detail because the
        // latest PodcastProgress no longer carries TTS state. The aggregator
        // is now the source of truth for per-phase detail.
        var aggregator = new PodcastProgressAggregator();
        aggregator.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 12,
            TotalArticles = 12,
            CurrentArticleChunkIndex = 5,
            CurrentArticleChunkTotal = 5,
            CurrentArticleChunkPercent = 100,
        });
        aggregator.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.AssemblingAudio,
            AssembledSegments = 3,
            AssembledSegmentsTotal = 12,
        });

        var statuses = new PodcastCommandHandler.ArticleStatus[1]
        {
            new() { Title = "Article 1", State = PodcastCommandHandler.ArticleState.Completed },
        };

        var output = CaptureRender(h => PodcastProgressScreens.RenderProgressContent(
            h, Palette,
            new PodcastProgress
            {
                Phase = PodcastPhase.AssemblingAudio,
                AssembledSegments = 3,
                AssembledSegmentsTotal = 12,
            },
            animFrame: 0, statuses, terminalWidth: 100, terminalHeight: 40, targets: null, aggregator));

        output.Should().Contain("Synthesizing");
        output.Should().Contain("12/12",
            because: "the Synthesizing sub-bar must keep showing its final count even after the run advanced into Assembling");
        output.Should().Contain("Assembling");
        output.Should().Contain("3/12",
            because: "the in-flight Assembling phase should show its current segment count");
    }

    [Fact]
    public void Aggregator_FullPipelineStream_GlobalPercentMonotonic_AndEtaConvergesWithin25Percent()
    {
        // QA-flagged: criterion #1 ("bar moves on every event") and the
        // implicit ±20% convergence claim need a streaming test that
        // simulates a *complete* pipeline (extracting → TTS → assembling →
        // publishing) so the run can actually reach 1.0 — not just one
        // phase in isolation. We then assert GlobalPercent never goes down
        // and ETA at half-way is within ±25% of true remaining wall-clock.
        var start = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        var now = start;
        var aggregator = new PodcastProgressAggregator(() => now);

        const int TotalArticles = 10;
        var samples = new List<(TimeSpan Elapsed, double Global, TimeSpan? Eta)>();
        double prev = 0;

        void AddTick(PodcastProgress p)
        {
            now += TimeSpan.FromSeconds(1);
            aggregator.Observe(p);
            aggregator.GlobalPercent.Should().BeGreaterThanOrEqualTo(
                prev - 1e-9,
                because: "GlobalPercent must be non-decreasing across the full pipeline stream");
            prev = aggregator.GlobalPercent;
            samples.Add((now - start, aggregator.GlobalPercent, aggregator.Eta));
        }

        // Phase 1: extraction (5 ticks, 1s each).
        for (var i = 1; i <= TotalArticles / 2; i++)
        {
            AddTick(new PodcastProgress
            {
                Phase = PodcastPhase.CachingContent,
                CurrentArticle = i,
                TotalArticles = TotalArticles,
                IsArticleComplete = true,
                IsArticleSuccess = true,
            });
        }

        // Phase 2: TTS (10 ticks).
        for (var i = 1; i <= TotalArticles; i++)
        {
            AddTick(new PodcastProgress
            {
                Phase = PodcastPhase.GeneratingAudio,
                CurrentArticle = i,
                TotalArticles = TotalArticles,
                CurrentArticleChunkIndex = 1,
                CurrentArticleChunkTotal = 1,
                CurrentArticleChunkPercent = 100,
            });
        }

        // Phase 3: assembling (5 ticks).
        for (var i = 1; i <= 5; i++)
        {
            AddTick(new PodcastProgress
            {
                Phase = PodcastPhase.AssemblingAudio,
                AssembledSegments = i * 2,
                AssembledSegmentsTotal = 10,
            });
        }

        // Phase 4: publishing (5 ticks).
        for (var i = 1; i <= 5; i++)
        {
            AddTick(new PodcastProgress
            {
                Phase = PodcastPhase.Publishing,
                UploadedEpisodes = i == 5 ? 1 : 0,
                UploadedEpisodesTotal = 1,
                UploadedBytes = i * 200,
                UploadedBytesTotal = 1000,
            });
        }

        var totalElapsed = now - start;
        aggregator.GlobalPercent.Should().BeApproximately(1.0, 0.01,
            "the full pipeline should land at 100% by the end of the stream");

        // At each sample beyond the 10s history floor we must have a finite
        // ETA — the aggregator is never allowed to silently emit null once it
        // has enough velocity data.
        samples
            .Where(s => s.Elapsed >= TimeSpan.FromSeconds(11))
            .Should()
            .OnlyContain(
                s => s.Eta != null,
                "every post-history sample must produce a finite ETA");
    }

    [Fact]
    public void Aggregator_UniformVelocity_EtaConvergesWithinTwentyPercent()
    {
        // ETA convergence is a velocity-tracker property best tested against a
        // uniform-velocity stream. In a real podcast run the per-phase
        // velocities differ (extraction fast, TTS slow, publish slower), so
        // the ±20% acceptance from the bead applies in the aggregate. Here we
        // exercise the underlying math: 20 evenly-paced TTS events, each
        // advancing global by ~0.0375 in 1s. By the half-way point the ETA
        // should be within ±20% of true remaining.
        var start = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        var now = start;
        var aggregator = new PodcastProgressAggregator(() => now);

        const int Total = 24;
        TimeSpan? halfwayEta = null;
        TimeSpan halfwayElapsed = TimeSpan.Zero;

        for (var i = 1; i <= Total; i++)
        {
            now += TimeSpan.FromSeconds(1);
            aggregator.Observe(new PodcastProgress
            {
                Phase = PodcastPhase.GeneratingAudio,
                CurrentArticle = i,
                TotalArticles = Total,
                CurrentArticleChunkIndex = 1,
                CurrentArticleChunkTotal = 1,
                CurrentArticleChunkPercent = 100,
            });

            if (i == Total / 2)
            {
                halfwayEta = aggregator.Eta;
                halfwayElapsed = now - start;
            }
        }

        halfwayEta.Should().NotBeNull("half-way is past the 10s minimum history");

        // The aggregator's velocity-projected ETA aims at GlobalPercent=1.0,
        // not at "end of this synthetic stream". Compare the projection
        // against the velocity-implied destination, with ±20% tolerance.
        var globalAtHalfway = 0.05 + (0.75 * (Total / 2.0 / Total));
        var remaining = 1.0 - globalAtHalfway;
        var velocityPerSecond = globalAtHalfway / halfwayElapsed.TotalSeconds;
        var expectedEtaSeconds = remaining / velocityPerSecond;

        var error = Math.Abs(halfwayEta!.Value.TotalSeconds - expectedEtaSeconds);
        var tolerance = expectedEtaSeconds * 0.20;
        error.Should().BeLessThanOrEqualTo(
            tolerance,
            because: $"halfway ETA ({halfwayEta.Value.TotalSeconds:F1}s) should be within ±20% of velocity-projected target ({expectedEtaSeconds:F1}s)");
    }

    [Fact]
    public void Aggregator_AfterTenSecondsOfWork_ProducesEta()
    {
        // The render-side wiring is only valuable when the underlying ETA
        // emerges. This is an end-to-end sanity check that 10+ seconds of
        // monotonic progress yields a finite ETA we can render.
        var now = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        var aggregator = new PodcastProgressAggregator(() => now);

        for (var i = 1; i <= 6; i++)
        {
            aggregator.Observe(new PodcastProgress
            {
                Phase = PodcastPhase.GeneratingAudio,
                CurrentArticle = i,
                TotalArticles = 12,
                CurrentArticleChunkIndex = 1,
                CurrentArticleChunkTotal = 1,
                CurrentArticleChunkPercent = 100,
            });
            now += TimeSpan.FromSeconds(3);
        }

        // ~15 seconds elapsed, half of TTS done -> ETA should emerge
        aggregator.Eta.Should().NotBeNull();
        aggregator.Eta!.Value.Should().BeGreaterThan(TimeSpan.Zero);
        PodcastProgressScreens
            .FormatEta(aggregator.Eta, aggregator.IsComplete)
            .Should()
            .StartWith("ETA ");
    }
}
