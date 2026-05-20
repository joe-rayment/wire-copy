// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Tests for workspace-v34z (Phase B velocity-based ETA). Covers
/// weighted-global-percent computation, per-phase fractions, velocity
/// derivation, the 10-second history floor, the cache-hit reweight, ETA
/// rounding, and "no events yet" / "completed" edge cases. Tests pass an
/// explicit clock so they can fast-forward deterministically.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastProgressAggregatorTests
{
    private DateTime _now = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    private PodcastProgressAggregator MakeAggregator() => new(() => _now);

    private void Advance(TimeSpan delta)
    {
        _now += delta;
    }

    [Fact]
    public void GlobalPercent_NoEvents_IsZero()
    {
        var agg = MakeAggregator();
        agg.GlobalPercent.Should().Be(0);
        agg.Eta.Should().BeNull("nothing to extrapolate from when no events have arrived");
    }

    [Fact]
    public void GlobalPercent_AfterAudioCompletion_ReflectsTtsWeight()
    {
        var agg = MakeAggregator();
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 12,
            TotalArticles = 12,
            CurrentArticleChunkIndex = 5,
            CurrentArticleChunkTotal = 5,
            CurrentArticleChunkPercent = 100,
        });

        // Extracting=1.0 (entered TTS) + TTS=1.0; assembly and publish still 0.
        // Default weights: ext 0.05 + tts 0.75 + asm 0 + pub 0 = 0.80
        agg.GlobalPercent.Should().BeApproximately(0.80, 0.01);
    }

    [Fact]
    public void GlobalPercent_AllPhasesAt100_ReachesOne()
    {
        var agg = MakeAggregator();
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.Publishing,
            UploadedEpisodes = 3,
            UploadedEpisodesTotal = 3,
        });

        agg.GlobalPercent.Should().BeApproximately(1.0, 0.001);
        agg.IsComplete.Should().BeTrue();
        agg.Eta.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Eta_FirstTenSeconds_IsNull()
    {
        var agg = MakeAggregator();
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 1,
            TotalArticles = 12,
            CurrentArticleChunkPercent = 50,
        });

        Advance(TimeSpan.FromSeconds(3));
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 2,
            TotalArticles = 12,
        });

        agg.Eta.Should().BeNull("less than 10s of history is too noisy to extrapolate from");
    }

    [Fact]
    public void Eta_AfterHistoryAccrues_IsDerivedFromVelocity()
    {
        var agg = MakeAggregator();

        // Tick through 6 articles in 30 seconds — steady 5s/article.
        for (var i = 1; i <= 6; i++)
        {
            agg.Observe(new PodcastProgress
            {
                Phase = PodcastPhase.GeneratingAudio,
                CurrentArticle = i,
                TotalArticles = 12,
                CurrentArticleChunkPercent = 100,
                CurrentArticleChunkTotal = 1,
                CurrentArticleChunkIndex = 1,
            });
            Advance(TimeSpan.FromSeconds(5));
        }

        var eta = agg.Eta;
        eta.Should().NotBeNull();
        eta!.Value.Should().BeGreaterThan(TimeSpan.Zero);
        // Half done in 30s → ~30s remaining for the other half. ETA should land
        // somewhere in that ballpark; with the small bucket and rounding it's
        // generous enough.
        eta.Value.TotalSeconds.Should().BeInRange(10, 120);
    }

    [Fact]
    public void Eta_NoProgressFor60Seconds_GrowsMonotonically()
    {
        var agg = MakeAggregator();
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 1,
            TotalArticles = 12,
            CurrentArticleChunkPercent = 100,
            CurrentArticleChunkTotal = 1,
            CurrentArticleChunkIndex = 1,
        });

        Advance(TimeSpan.FromSeconds(10));
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 2,
            TotalArticles = 12,
            CurrentArticleChunkPercent = 100,
            CurrentArticleChunkTotal = 1,
            CurrentArticleChunkIndex = 1,
        });

        var eta1 = agg.Eta;

        // No new events for 60s — velocity falls toward zero, ETA grows.
        Advance(TimeSpan.FromSeconds(60));

        // Re-feeding the same article keeps the global percent constant —
        // velocity over the window drops, ETA expands.
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 2,
            TotalArticles = 12,
            CurrentArticleChunkPercent = 100,
            CurrentArticleChunkTotal = 1,
            CurrentArticleChunkIndex = 1,
        });

        var eta2 = agg.Eta;

        // Either eta2 is now null (velocity zero in window) OR it's greater
        // than eta1. Both indicate the aggregator correctly stops claiming
        // it knows the ETA when nothing is happening.
        if (eta1 is not null && eta2 is not null)
        {
            eta2.Value.Should().BeGreaterThan(eta1.Value);
        }
        else
        {
            // eta2 null is also acceptable — "I don't know" is better than a lie.
            eta2.Should().BeNull();
        }
    }

    [Fact]
    public void CacheHitReweight_HighCachedRatio_ShrinksTtsWeight()
    {
        var agg = MakeAggregator();
        // 80% cached: TTS weight should collapse, assembly+publish get the freed mass.
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 1,
            TotalArticles = 10,
            CachedArticleCount = 8,
            UncachedArticleCount = 2,
        });

        var w = agg.GetEffectiveWeights();
        w.Tts.Should().BeApproximately(0.75 * 0.2, 0.01, "TTS scales by uncached ratio (0.2)");
        (w.Extracting + w.Tts + w.Assembling + w.Publishing)
            .Should().BeApproximately(1.0, 0.001, "weights sum to 1 — freed TTS mass is redistributed");
        w.Assembling.Should().BeGreaterThan(PodcastProgressAggregator.DefaultWeightAssembling,
            "freed TTS mass partially goes to assembly");
        w.Publishing.Should().BeGreaterThan(PodcastProgressAggregator.DefaultWeightPublishing,
            "freed TTS mass partially goes to publishing");
    }

    [Fact]
    public void CacheHitReweight_NoCache_UsesDefaultWeights()
    {
        var agg = MakeAggregator();
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 1,
            TotalArticles = 5,
            CachedArticleCount = 0,
            UncachedArticleCount = 5,
        });

        var w = agg.GetEffectiveWeights();
        w.Extracting.Should().Be(PodcastProgressAggregator.DefaultWeightExtracting);
        w.Tts.Should().Be(PodcastProgressAggregator.DefaultWeightTts);
        w.Assembling.Should().Be(PodcastProgressAggregator.DefaultWeightAssembling);
        w.Publishing.Should().Be(PodcastProgressAggregator.DefaultWeightPublishing);
    }

    [Fact]
    public void Observe_AssemblingPhase_FlushesEarlierPhasesToOne()
    {
        var agg = MakeAggregator();
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.AssemblingAudio,
            AssembledSegments = 2,
            AssembledSegmentsTotal = 5,
        });

        agg.GetPhasePercent(PodcastPhase.CachingContent).Should().Be(1.0);
        agg.GetPhasePercent(PodcastPhase.GeneratingAudio).Should().Be(1.0);
        agg.GetPhasePercent(PodcastPhase.AssemblingAudio).Should().BeApproximately(0.4, 0.001);
        agg.GetPhasePercent(PodcastPhase.Publishing).Should().Be(0);
    }

    [Fact]
    public void Observe_PublishingPhase_ReportsBytePartialPercent()
    {
        var agg = MakeAggregator();
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.Publishing,
            UploadedEpisodes = 0,
            UploadedEpisodesTotal = 1,
            UploadedBytes = 512,
            UploadedBytesTotal = 1024,
        });

        agg.GetPhasePercent(PodcastPhase.Publishing).Should().BeApproximately(0.5, 0.001,
            "byte-progress within the in-flight episode contributes a fractional episode count");
    }

    [Fact]
    public void RoundEta_OverTwoMinutes_RoundedToNearestFiveSeconds()
    {
        var agg = MakeAggregator();
        // Tiny advancement over 11 seconds → small velocity → very long ETA.
        agg.Observe(new PodcastProgress { Phase = PodcastPhase.GeneratingAudio, CurrentArticle = 1, TotalArticles = 100, CurrentArticleChunkPercent = 100, CurrentArticleChunkTotal = 1, CurrentArticleChunkIndex = 1 });
        Advance(TimeSpan.FromSeconds(11));
        agg.Observe(new PodcastProgress { Phase = PodcastPhase.GeneratingAudio, CurrentArticle = 2, TotalArticles = 100, CurrentArticleChunkPercent = 100, CurrentArticleChunkTotal = 1, CurrentArticleChunkIndex = 1 });

        var eta = agg.Eta;
        eta.Should().NotBeNull();

        // If above 2min, rounding mod 5 should mean total seconds divisible by 5.
        if (eta!.Value > TimeSpan.FromMinutes(2))
        {
            ((int)eta.Value.TotalSeconds % 5).Should().Be(0,
                "ETAs over 2 min are bucketed to the nearest 5 seconds to reduce visual jitter");
        }
    }

    [Fact]
    public void GlobalPercent_DuringTtsInflight_BlendsArticleAndChunkProgress()
    {
        var agg = MakeAggregator();
        agg.Observe(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 5,
            TotalArticles = 10,
            CurrentArticleChunkIndex = 3,
            CurrentArticleChunkTotal = 5,
            CurrentArticleChunkPercent = 60,
        });

        // TTS fraction: 4 articles done / 10 + 0.6 / 10 (in-flight) = 0.46
        // Global: ext(0.05) + tts(0.75 * 0.46) = 0.05 + 0.345 = 0.395
        agg.GlobalPercent.Should().BeApproximately(0.395, 0.01);
    }
}
