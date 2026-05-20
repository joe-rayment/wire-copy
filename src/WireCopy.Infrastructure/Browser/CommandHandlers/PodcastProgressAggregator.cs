// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Podcast;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Phase B of workspace-rn4j (workspace-v34z). Pure helper that consumes the
/// real progress signals plumbed by Phase A and produces a weighted global
/// percent + velocity-based ETA for the progress screen — replacing the
/// previous "0% → 10% → 70% → 85% → 100%" staircase that sat frozen on the
/// 70% step for most of the wall-clock time.
///
/// <para>
/// The aggregator is stateful: feed every <see cref="PodcastProgress"/> the
/// orchestrator emits via <see cref="Observe(PodcastProgress)"/>, then read
/// <see cref="GlobalPercent"/> / <see cref="GetPhasePercent(PodcastPhase)"/>
/// / <see cref="Eta"/> at the next render tick. No external clock — uses
/// <see cref="DateTime.UtcNow"/> internally so unit tests pass an explicit
/// time source via the constructor.
/// </para>
/// </summary>
internal sealed class PodcastProgressAggregator
{
    /// <summary>
    /// Default phase weights (sum to 1.0) from empirical podcast runs. TTS
    /// dominates because each chunk is a network round-trip; publishing is a
    /// distant second; extraction and assembly are minor.
    /// </summary>
    public const double DefaultWeightExtracting = 0.05;
    public const double DefaultWeightTts = 0.75;
    public const double DefaultWeightAssembling = 0.08;
    public const double DefaultWeightPublishing = 0.12;

    private static readonly TimeSpan VelocityWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinHistoryForEta = TimeSpan.FromSeconds(10);

    private readonly Func<DateTime> _clock;
    private readonly List<Sample> _history = new();

    private double _extractingFraction;
    private double _ttsFraction;
    private double _assemblingFraction;
    private double _publishingFraction;
    private int _cachedArticleCount;
    private int _uncachedArticleCount;
    private bool _hasAnyEvent;

    // workspace-v34z QA fix: persist the last meaningful per-phase "n/total"
    // detail across phase transitions so that, e.g., the Synthesizing sub-bar
    // still reads "12/12" after the run advances into Assembling. Without this
    // the detail string vanishes the moment the phase changes, which is
    // exactly when the user wants to confirm the previous phase actually
    // finished.
    private string _extractingDetail = string.Empty;
    private string _ttsDetail = string.Empty;
    private string _assemblingDetail = string.Empty;
    private string _publishingDetail = string.Empty;

    public PodcastProgressAggregator(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Weighted global percent in [0, 1] across all four phases. Reflects
    /// real work completed — no hard-coded jumps.
    /// </summary>
    public double GlobalPercent => ComputeGlobalPercent();

    /// <summary>
    /// Velocity-based ETA. Null when we have less than ~10 seconds of
    /// history (too noisy) OR when the global percent is at 1.0 (done).
    /// </summary>
    public TimeSpan? Eta => ComputeEta();

    /// <summary>
    /// Whether the run has reached 100%. Once true the aggregator stops
    /// updating velocity samples.
    /// </summary>
    public bool IsComplete => GlobalPercent >= 0.999;

    /// <summary>
    /// Feeds one progress event into the aggregator. Idempotent — re-feeding
    /// the same event has no effect on velocity (the sample is keyed by
    /// timestamp + global percent).
    /// </summary>
    public void Observe(PodcastProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        _hasAnyEvent = true;

        if (progress.CachedArticleCount > 0 || progress.UncachedArticleCount > 0)
        {
            _cachedArticleCount = progress.CachedArticleCount;
            _uncachedArticleCount = progress.UncachedArticleCount;
        }

        switch (progress.Phase)
        {
            case PodcastPhase.CachingContent:
                if (progress.TotalArticles > 0)
                {
                    var done = Math.Max(0, progress.CurrentArticle - (progress.IsArticleComplete ? 0 : 1));
                    _extractingFraction = Math.Clamp((double)done / progress.TotalArticles, 0, 1);
                    _extractingDetail = $"{Math.Min(progress.CurrentArticle, progress.TotalArticles)}/{progress.TotalArticles}";
                }

                break;

            case PodcastPhase.GeneratingAudio:
                _extractingFraction = 1.0; // entering TTS means extraction is done
                _ttsFraction = ComputeTtsFraction(progress);
                if (progress.TotalArticles > 0)
                {
                    _ttsDetail = $"{Math.Min(progress.CurrentArticle, progress.TotalArticles)}/{progress.TotalArticles}";
                }

                if (_extractingDetail.Length == 0 && progress.TotalArticles > 0)
                {
                    // First event we've seen is TTS — synthesize the "all done"
                    // detail for the prior phase so the sub-bar reads sanely.
                    _extractingDetail = $"{progress.TotalArticles}/{progress.TotalArticles}";
                }

                break;

            case PodcastPhase.AssemblingAudio:
                _extractingFraction = 1.0;
                _ttsFraction = 1.0;
                _assemblingFraction = ComputeAssembleFraction(progress);
                if (progress.AssembledSegmentsTotal > 0)
                {
                    _assemblingDetail = $"{progress.AssembledSegments}/{progress.AssembledSegmentsTotal}";
                }

                if (_ttsDetail.Length == 0 && progress.TotalArticles > 0)
                {
                    _ttsDetail = $"{progress.TotalArticles}/{progress.TotalArticles}";
                }

                if (_extractingDetail.Length == 0 && progress.TotalArticles > 0)
                {
                    _extractingDetail = $"{progress.TotalArticles}/{progress.TotalArticles}";
                }

                break;

            case PodcastPhase.Publishing:
                _extractingFraction = 1.0;
                _ttsFraction = 1.0;
                _assemblingFraction = 1.0;
                _publishingFraction = ComputePublishFraction(progress);
                if (progress.UploadedEpisodesTotal > 0)
                {
                    _publishingDetail = $"{progress.UploadedEpisodes}/{progress.UploadedEpisodesTotal}";
                }

                if (_assemblingDetail.Length == 0 && progress.AssembledSegmentsTotal > 0)
                {
                    _assemblingDetail = $"{progress.AssembledSegmentsTotal}/{progress.AssembledSegmentsTotal}";
                }

                if (_ttsDetail.Length == 0 && progress.TotalArticles > 0)
                {
                    _ttsDetail = $"{progress.TotalArticles}/{progress.TotalArticles}";
                }

                if (_extractingDetail.Length == 0 && progress.TotalArticles > 0)
                {
                    _extractingDetail = $"{progress.TotalArticles}/{progress.TotalArticles}";
                }

                break;
        }

        var now = _clock();
        var global = ComputeGlobalPercent();
        _history.Add(new Sample(now, global));
        TrimHistory(now);
    }

    /// <summary>
    /// Returns the normalized 0–1 progress for a single phase. Useful for
    /// rendering per-phase sub-bars on the progress screen.
    /// </summary>
    public double GetPhasePercent(PodcastPhase phase)
    {
        return phase switch
        {
            PodcastPhase.CachingContent => _extractingFraction,
            PodcastPhase.GeneratingAudio => _ttsFraction,
            PodcastPhase.AssemblingAudio => _assemblingFraction,
            PodcastPhase.Publishing => _publishingFraction,
            _ => 0,
        };
    }

    /// <summary>
    /// Returns the latest "n/total" detail string captured for a phase. Sticky
    /// across phase transitions: once Synthesizing has reported "12/12", that
    /// string stays even after the run advances into Assembling so the user
    /// can confirm at a glance that the previous phase finished cleanly.
    /// Returns the empty string until a meaningful event has been observed
    /// for the phase.
    /// </summary>
    public string GetPhaseDetail(PodcastPhase phase)
    {
        return phase switch
        {
            PodcastPhase.CachingContent => _extractingDetail,
            PodcastPhase.GeneratingAudio => _ttsDetail,
            PodcastPhase.AssemblingAudio => _assemblingDetail,
            PodcastPhase.Publishing => _publishingDetail,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Returns the phase weights actually used for the current run. Cache-hit
    /// reweighting shifts TTS's mass into assembly+publish when most articles
    /// are cached. Exposed so callers (and tests) can verify the rebalancing
    /// is happening.
    /// </summary>
    public PhaseWeights GetEffectiveWeights()
    {
        var total = _cachedArticleCount + _uncachedArticleCount;
        if (total <= 0 || _cachedArticleCount == 0)
        {
            return new PhaseWeights(
                DefaultWeightExtracting,
                DefaultWeightTts,
                DefaultWeightAssembling,
                DefaultWeightPublishing);
        }

        // Cache-hit reweight: TTS weight scales by the uncached ratio. The
        // freed-up mass is redistributed proportionally between assembly and
        // publish so the total stays at 1.0.
        var uncachedRatio = (double)_uncachedArticleCount / total;
        var ttsAdjusted = DefaultWeightTts * uncachedRatio;
        var freed = DefaultWeightTts - ttsAdjusted;
        var asmShare = DefaultWeightAssembling / (DefaultWeightAssembling + DefaultWeightPublishing);
        var pubShare = DefaultWeightPublishing / (DefaultWeightAssembling + DefaultWeightPublishing);

        return new PhaseWeights(
            DefaultWeightExtracting,
            ttsAdjusted,
            DefaultWeightAssembling + (freed * asmShare),
            DefaultWeightPublishing + (freed * pubShare));
    }

    private double ComputeGlobalPercent()
    {
        if (!_hasAnyEvent)
        {
            return 0;
        }

        var w = GetEffectiveWeights();
        var global = (_extractingFraction * w.Extracting)
                   + (_ttsFraction * w.Tts)
                   + (_assemblingFraction * w.Assembling)
                   + (_publishingFraction * w.Publishing);
        return Math.Clamp(global, 0, 1);
    }

    private TimeSpan? ComputeEta()
    {
        if (_history.Count == 0)
        {
            return null;
        }

        var global = ComputeGlobalPercent();
        if (global >= 0.999)
        {
            return TimeSpan.Zero;
        }

        var now = _clock();
        var windowStart = now - VelocityWindow;
        Sample? oldest = null;
        foreach (var s in _history)
        {
            if (s.Timestamp >= windowStart)
            {
                oldest ??= s;
                break;
            }

            oldest = s;
        }

        if (oldest is null)
        {
            return null;
        }

        var elapsed = now - oldest.Value.Timestamp;
        if (elapsed < MinHistoryForEta)
        {
            return null;
        }

        var advancement = global - oldest.Value.GlobalPercent;
        if (advancement <= 0)
        {
            return null;
        }

        var velocity = advancement / elapsed.TotalSeconds;
        var remaining = 1.0 - global;
        var secondsRemaining = remaining / velocity;

        if (double.IsInfinity(secondsRemaining) || secondsRemaining < 0)
        {
            return null;
        }

        return RoundEta(TimeSpan.FromSeconds(secondsRemaining));
    }

#pragma warning disable SA1204 // Static helpers grouped next to the method that uses them for readability.
    private static TimeSpan RoundEta(TimeSpan raw)
    {
        // Reduce visual jitter: round to nearest 5s when >2min remaining,
        // nearest second otherwise. Negative or absurdly large values clamped
        // to a sane upper bound.
        if (raw > TimeSpan.FromHours(1))
        {
            return TimeSpan.FromHours(1);
        }

        if (raw > TimeSpan.FromMinutes(2))
        {
            var bucketed = Math.Round(raw.TotalSeconds / 5.0) * 5.0;
            return TimeSpan.FromSeconds(bucketed);
        }

        return TimeSpan.FromSeconds(Math.Round(raw.TotalSeconds));
    }

    private static double ComputeTtsFraction(PodcastProgress p)
    {
        if (p.TotalArticles <= 0)
        {
            return 0;
        }

        var perArticle = 1.0 / p.TotalArticles;
        var doneArticles = Math.Max(0, p.CurrentArticle - 1);
        var inflight = p.CurrentArticleChunkTotal > 0
            ? Math.Clamp(p.CurrentArticleChunkPercent / 100.0, 0, 1) * perArticle
            : 0;

        return Math.Clamp((doneArticles * perArticle) + inflight, 0, 1);
    }

    private static double ComputeAssembleFraction(PodcastProgress p)
    {
        if (p.AssembledSegmentsTotal <= 0)
        {
            // Fall back to the global percent the orchestrator emitted (75 -> 85)
            // mapped into 0..1 within the assembly phase.
            var pct = (p.PercentComplete - 70) / 15.0;
            return Math.Clamp(pct, 0, 1);
        }

        var probe = (double)p.AssembledSegments / p.AssembledSegmentsTotal;
        return Math.Clamp(probe, 0, 1);
    }

    private static double ComputePublishFraction(PodcastProgress p)
    {
        if (p.UploadedEpisodesTotal <= 0)
        {
            return Math.Clamp((p.PercentComplete - 85) / 15.0, 0, 1);
        }

        var perEpisode = 1.0 / p.UploadedEpisodesTotal;
        var done = p.UploadedEpisodes * perEpisode;
        var inflight = p.UploadedBytesTotal > 0
            ? Math.Clamp((double)p.UploadedBytes / p.UploadedBytesTotal, 0, 1) * perEpisode
            : 0;
        return Math.Clamp(done + inflight, 0, 1);
    }

#pragma warning restore SA1204

    private void TrimHistory(DateTime now)
    {
        // Keep at least 1 sample older than the window so ETA can compute
        // velocity even if events are sparse.
        var cutoff = now - VelocityWindow - TimeSpan.FromSeconds(5);
        while (_history.Count > 2 && _history[0].Timestamp < cutoff)
        {
            _history.RemoveAt(0);
        }
    }

    /// <summary>
    /// Per-phase weights used by the aggregator. Sum to 1.0 (subject to
    /// floating-point rounding). Exposed for tests and for the progress
    /// screen to render the per-phase sub-bars at the correct visual
    /// proportion.
    /// </summary>
    public readonly record struct PhaseWeights(
        double Extracting,
        double Tts,
        double Assembling,
        double Publishing);

    /// <summary>A single observation of (timestamp, global percent).</summary>
    private readonly record struct Sample(DateTime Timestamp, double GlobalPercent);
}
