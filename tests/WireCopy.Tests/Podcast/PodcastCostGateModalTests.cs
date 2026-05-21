// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.ValueObjects.Podcast;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Podcast;

/// <summary>
/// Tests for workspace-lr80 (Phase 2: cost-gate modal). Covers:
/// (1) <see cref="PodcastCostGateConfig.ShouldShowGate(CacheAnalysis)"/> threshold semantics,
/// (2) the modal summary line copy at typical analyses,
/// (3) <c>LoadCostGateConfig</c> parses the user settings keys correctly with sane fallbacks.
/// The actual modal interaction (Enter/Esc) is a thin wrapper around
/// <c>ctx.InputHandler.WaitForInputAsync</c> and is exercised end-to-end
/// via the PodcastCommandHandler integration paths.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastCostGateModalTests
{
    [Fact]
    public void ShouldShowGate_BelowBothThresholds_ReturnsFalse()
    {
        var cfg = new PodcastCostGateConfig();
        var analysis = AnalysisOf(cached: 0, uncached: 5, cost: 0.50m);

        cfg.ShouldShowGate(analysis).Should().BeFalse(
            "small cheap runs proceed silently per the bead's 'one-keystroke' contract");
    }

    [Fact]
    public void ShouldShowGate_AboveCostThreshold_ReturnsTrue()
    {
        var cfg = new PodcastCostGateConfig { ThresholdUsd = 1.00m };
        var analysis = AnalysisOf(cached: 0, uncached: 3, cost: 1.50m);

        cfg.ShouldShowGate(analysis).Should().BeTrue();
    }

    [Fact]
    public void ShouldShowGate_AboveArticleThreshold_ReturnsTrue()
    {
        var cfg = new PodcastCostGateConfig { ArticleThreshold = 10 };
        var analysis = AnalysisOf(cached: 0, uncached: 12, cost: 0.20m);

        cfg.ShouldShowGate(analysis).Should().BeTrue(
            "12 uncached articles exceeds the 10-article gate even though the cost is tiny");
    }

    [Fact]
    public void ShouldShowGate_ManyCachedArticles_DoesNotTriggerArticleGate()
    {
        var cfg = new PodcastCostGateConfig { ArticleThreshold = 10 };
        var analysis = AnalysisOf(cached: 49, uncached: 1, cost: 0.05m);

        cfg.ShouldShowGate(analysis).Should().BeFalse(
            "the article gate counts uncached articles only — a 50-article run with 49 cached is essentially a 1-article run");
    }

    [Fact]
    public void ShouldShowGate_AlwaysShow_ReturnsTrueEvenForTinyJob()
    {
        var cfg = new PodcastCostGateConfig { AlwaysShow = true };
        var analysis = AnalysisOf(cached: 1, uncached: 0, cost: 0m);

        cfg.ShouldShowGate(analysis).Should().BeTrue(
            "AlwaysShow forces the gate to pop regardless of cost or article count");
    }

    [Fact]
    public void ShouldShowGate_EqualCostThreshold_ReturnsFalse()
    {
        var cfg = new PodcastCostGateConfig { ThresholdUsd = 1.00m };
        var analysis = AnalysisOf(cached: 0, uncached: 3, cost: 1.00m);

        cfg.ShouldShowGate(analysis).Should().BeFalse(
            "the gate uses strict greater-than — exactly-equal cost is not large enough to interrupt");
    }

    [Fact]
    public void BuildSummaryLine_StandardJob_RendersCountCostDuration()
    {
        var analysis = AnalysisOf(cached: 0, uncached: 12, cost: 0.84m);

        var line = PodcastCostGateModal.BuildSummaryLine(analysis);

        line.Should().Contain("Generate 12 articles");
        line.Should().Contain("$0.84");
        line.Should().Contain("min", "the summary surfaces the rough duration so the user knows how long the run will take");
    }

    [Fact]
    public void BuildSummaryLine_HighlyCachedJob_SurfacesCacheSavingsInline()
    {
        var analysis = AnalysisOf(cached: 8, uncached: 4, cost: 0.18m);

        var line = PodcastCostGateModal.BuildSummaryLine(analysis);

        line.Should().Contain("8 cached",
            "when cached articles dominate, the summary surfaces the cache savings so the user knows they're not paying for the whole list");
        line.Should().Contain("$0.18");
    }

    [Fact]
    public void BuildSummaryLine_SingleArticle_UsesSingularNoun()
    {
        var analysis = AnalysisOf(cached: 0, uncached: 1, cost: 0.07m);

        var line = PodcastCostGateModal.BuildSummaryLine(analysis);

        line.Should().Contain("1 article ");
        line.Should().NotContain("articles");
    }

    [Fact]
    public void LoadCostGateConfig_NoSettings_ReturnsDefaults()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.Get(Arg.Any<string>()).Returns((string?)null);

        var cfg = PodcastCommandHandler.LoadCostGateConfig(store);

        cfg.ThresholdUsd.Should().Be(5.00m, "workspace-reym raised the default so the gate doesn't fire on every realistic Reading List");
        cfg.ArticleThreshold.Should().Be(50, "workspace-reym raised the default so the gate doesn't fire on every realistic Reading List");
        cfg.AlwaysShow.Should().BeFalse();
    }

    [Fact]
    public void LoadCostGateConfig_ParsesUserOverrides()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.Get("PodcastCostGateThresholdUsd").Returns("2.50");
        store.Get("PodcastCostGateArticleThreshold").Returns("25");
        store.Get("PodcastCostGateAlwaysShow").Returns("true");

        var cfg = PodcastCommandHandler.LoadCostGateConfig(store);

        cfg.ThresholdUsd.Should().Be(2.50m);
        cfg.ArticleThreshold.Should().Be(25);
        cfg.AlwaysShow.Should().BeTrue();
    }

    [Fact]
    public void LoadCostGateConfig_GarbageValues_FallsBackToDefaults()
    {
        var store = Substitute.For<IUserSettingsStore>();
        store.Get("PodcastCostGateThresholdUsd").Returns("not-a-number");
        store.Get("PodcastCostGateArticleThreshold").Returns("-5");
        store.Get("PodcastCostGateAlwaysShow").Returns("maybe");

        var cfg = PodcastCommandHandler.LoadCostGateConfig(store);

        cfg.ThresholdUsd.Should().Be(5.00m, "non-numeric input falls back to the workspace-reym default");
        cfg.ArticleThreshold.Should().Be(50, "non-positive input falls back to the workspace-reym default");
        cfg.AlwaysShow.Should().BeFalse("non-bool input falls back to default false");
    }

    private static CacheAnalysis AnalysisOf(int cached, int uncached, decimal cost)
    {
        return new CacheAnalysis
        {
            TotalArticles = cached + uncached,
            CachedArticles = cached,
            UncachedArticles = uncached,
            EstimatedCost = cost,
            ArticleStatuses = [],
        };
    }
}
