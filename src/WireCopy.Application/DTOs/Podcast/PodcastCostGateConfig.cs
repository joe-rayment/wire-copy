// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Thresholds for the cost-gate modal (workspace-lr80). A single confirm
/// modal pops between cache analysis and the progress screen when the
/// estimated spend OR the article count exceeds these caps. Below the
/// thresholds: silent kickoff.
/// </summary>
public sealed record PodcastCostGateConfig
{
    /// <summary>
    /// USD threshold above which the cost-gate modal appears. Default $5.00
    /// (workspace-reym: raised from $1.00 because the lower default tripped
    /// on every realistic Reading List and broke the umbrella's
    /// one-keystroke contract). Settable via the user settings key
    /// <c>PodcastCostGateThresholdUsd</c>.
    /// </summary>
    public decimal ThresholdUsd { get; init; } = 5.00m;

    /// <summary>
    /// Article-count threshold above which the cost-gate modal appears.
    /// Default 50 (workspace-reym: raised from 10 — the old cap fired on
    /// any reading list big enough to be worth listening to). Helps catch
    /// very large reading-list runs that are individually cheap but still
    /// represent a non-trivial spend in aggregate. Settable via
    /// <c>PodcastCostGateArticleThreshold</c>.
    /// </summary>
    public int ArticleThreshold { get; init; } = 50;

    /// <summary>
    /// When true the modal pops on every Generate run, even for tiny jobs.
    /// Settable via <c>PodcastCostGateAlwaysShow</c>. Default false.
    /// </summary>
    public bool AlwaysShow { get; init; }

    /// <summary>
    /// Returns true when the analysis triggers the gate per the configured
    /// thresholds. <see cref="AlwaysShow"/> overrides the thresholds, but
    /// only when there is actually something to confirm: a zero-cost,
    /// fully-cached job has nothing for the user to approve, so the gate
    /// must stay silent even with AlwaysShow set (workspace-ls53 —
    /// otherwise pressing Generate Podcast on a cached 1-article list
    /// still requires an extra Enter to dismiss a "$0.00" modal).
    /// </summary>
    public bool ShouldShowGate(CacheAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var hasSomethingToConfirm = analysis.UncachedArticles > 0 || analysis.EstimatedCost > 0m;

        if (AlwaysShow && hasSomethingToConfirm)
        {
            return true;
        }

        if (analysis.EstimatedCost > ThresholdUsd)
        {
            return true;
        }

        // Article-count gate fires on the count of articles that actually
        // need fresh TTS (cached ones are free), not the total — a 50-article
        // run with 49 cached is no different from a 1-article run.
        if (analysis.UncachedArticles >= ArticleThreshold)
        {
            return true;
        }

        return false;
    }
}
