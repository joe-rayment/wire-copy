// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-wef6.5 — the unified activity slot: ONE animated indicator
/// proving liveness, fed by a producer registry with priorities
/// foreground load (0) &gt; AI analysis (1) &gt; podcast (2) &gt; prefetch
/// (derived fallback).
/// </summary>
[Trait("Category", "Unit")]
public class ActivitySlotTests
{
    private static NavigationService MakeService()
        => new(Substitute.For<ILogger<NavigationService>>());

    [Fact]
    public void SetActivity_PublishesToContext()
    {
        var service = MakeService();

        service.SetActivity("ai", "✨ analyzing layout…", priority: 1);

        var activity = service.CurrentContext.ActiveActivity;
        activity.Should().NotBeNull();
        activity!.Text.Should().Be("✨ analyzing layout…");
        activity.Source.Should().Be("ai");
    }

    [Fact]
    public void ClearActivity_RemovesTheEntry()
    {
        var service = MakeService();
        service.SetActivity("ai", "✨ analyzing layout…", priority: 1);

        service.ClearActivity("ai");

        service.CurrentContext.ActiveActivity.Should().BeNull();
    }

    [Fact]
    public void TopActivity_PicksLowestPriority_LoadBeatsAi()
    {
        var service = MakeService();
        service.SetActivity("ai", "✨ analyzing layout…", priority: 1);
        service.SetActivity("load", "Extracting content… (3s)", priority: 0);

        service.CurrentContext.ActiveActivity!.Source.Should().Be("load",
            "the foreground load outranks AI analysis in the single slot");

        service.ClearActivity("load");
        service.CurrentContext.ActiveActivity!.Source.Should().Be("ai",
            "AI takes the slot back when the load finishes");
    }

    [Fact]
    public void SetActivity_SameSource_ReplacesNotDuplicates()
    {
        var service = MakeService();
        service.SetActivity("load", "Loading… (1s)", priority: 0);
        service.SetActivity("load", "Loading… (2s)", priority: 0);

        service.CurrentContext.ActiveActivity!.Text.Should().Be("Loading… (2s)");
    }

    // ---- Rendering ----

    [Fact]
    public void RegisteredActivity_RendersWithSpinner()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            ActiveActivity = new ActivityIndicator { Source = "ai", Text = "✨ analyzing layout…", Priority = 1 },
        };

        var model = StatusBarRenderer.ComposeStatusLine(context, ViewMode.Hierarchical, 160);

        model.PlainText.Should().Contain("✨ analyzing layout…",
            "the 30-60s AnalyzeAsync was previously invisible — the slot makes it visible");

        // The Braille spinner frames live in U+2800..U+28FF.
        model.PlainText.Should().MatchRegex("[⠀-⣿]", "the slot animates to prove liveness");
    }

    [Fact]
    public void RegisteredActivity_OutranksPrefetch()
    {
        var context = new NavigationContext
        {
            ViewMode = ViewMode.Hierarchical,
            ActiveActivity = new ActivityIndicator { Source = "load", Text = "Extracting content… (3s)", Priority = 0 },
        };
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 10,
            CachedCount = 3,
            IsActivelyFetching = true,
            CurrentlyFetchingUrl = "https://example.com/next",
        };

        var model = StatusBarRenderer.ComposeStatusLine(context, ViewMode.Hierarchical, 160, progress);

        model.PlainText.Should().Contain("Extracting content…");
        model.PlainText.Should().NotContain("3/10",
            "exactly ONE activity indicator renders — the higher-priority producer wins the slot");
    }

    [Fact]
    public void PrefetchFallback_RendersWhenNothingRegistered()
    {
        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var progress = new PreloadProgress
        {
            TotalCacheableLinks = 10,
            CachedCount = 3,
            IsActivelyFetching = true,
            CurrentlyFetchingUrl = "https://example.com/next",
        };

        var model = StatusBarRenderer.ComposeStatusLine(context, ViewMode.Hierarchical, 160, progress);

        model.PlainText.Should().Contain("3/10", "prefetch remains the derived lowest-priority producer");
        model.PlainText.Should().MatchRegex("[⠀-⣿]");
    }

    [Fact]
    public void PodcastJob_RendersAsActivityWithPercentAndRestoreKey()
    {
        var manager = Substitute.For<IPodcastBackgroundJobManager>();
        manager.HasActiveJob.Returns(true);
        manager.LastSnapshot.Returns(new WireCopy.Application.DTOs.Podcast.PodcastProgress
        {
            Phase = WireCopy.Application.DTOs.Podcast.PodcastPhase.GeneratingAudio,
            PercentComplete = 67,
            CurrentArticle = 3,
            TotalArticles = 5,
        });

        var context = new NavigationContext { ViewMode = ViewMode.Hierarchical };
        var model = StatusBarRenderer.ComposeStatusLine(
            context, ViewMode.Hierarchical, 200, podcastJobManager: manager);

        model.PlainText.Should().Contain("Generating 67%");
        model.PlainText.Should().Contain("Shift+P:restore");
        model.PlainText.Should().MatchRegex("[⠀-⣿]", "podcast generation animates in the slot too");
    }
}
