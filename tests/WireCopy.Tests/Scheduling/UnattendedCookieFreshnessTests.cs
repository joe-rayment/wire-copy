// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Application.Interfaces.Scheduling;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-frpl.11 (B8) — unattended cookie freshness for paywalled scheduled
/// loads. A successful headless load opportunistically refreshes cookies; a
/// Blocked (logged-out/paywalled) load records an actionable human diagnostic
/// that reaches the failure surface instead of producing a silent empty episode.
/// </summary>
[Trait("Category", "Unit")]
public class HeadlessCookieFreshnessTests
{
    private readonly IPreloadService _preload = Substitute.For<IPreloadService>();
    private readonly ILinkExtractor _extractor = Substitute.For<ILinkExtractor>();
    private readonly IHierarchyConfigStore _configStore = Substitute.For<IHierarchyConfigStore>();
    private readonly IAutoCookieRefresher _refresher = Substitute.For<IAutoCookieRefresher>();

    private UnattendedSectionLoadAdapter Adapter() => new(_preload, _extractor, _configStore, _refresher);

    [Fact]
    public async Task SuccessfulLoad_RefreshesCookies_WithLoadedHtmlAndFinalUrl()
    {
        _preload.LoadRenderedHtmlAsync("https://www.nytimes.com/", Arg.Any<CancellationToken>())
            .Returns(new RenderedLoad { Outcome = LoadOutcome.Ok, Html = "<html>logged in</html>", FinalUrl = "https://www.nytimes.com/" });
        _extractor.ExtractLinksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<LinkInfo>());

        await Adapter().LoadLinksAndConfigAsync("https://www.nytimes.com/");

        await _refresher.Received(1).MaybeRefreshAsync(
            "https://www.nytimes.com/", "<html>logged in</html>", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockedLoad_DoesNotAttemptCookieRefresh()
    {
        _preload.LoadRenderedHtmlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RenderedLoad { Outcome = LoadOutcome.Blocked, FinalUrl = "https://www.nytimes.com/" });

        await Adapter().LoadLinksAndConfigAsync("https://www.nytimes.com/");

        await _refresher.DidNotReceive().MaybeRefreshAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullRefresher_SuccessfulLoad_StillWorks()
    {
        _preload.LoadRenderedHtmlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RenderedLoad { Outcome = LoadOutcome.Ok, Html = "<html/>", FinalUrl = "https://x/" });
        _extractor.ExtractLinksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<LinkInfo>());

        var adapter = new UnattendedSectionLoadAdapter(_preload, _extractor, _configStore); // no refresher

        var result = await adapter.LoadLinksAndConfigAsync("https://x/");

        result.Outcome.Should().Be(LoadOutcome.Ok);
    }

    [Fact]
    public async Task BlockedStep_RecordsActionable_RefreshBySigningIn_Diagnostic_OnTheRun()
    {
        // A required step whose source is Blocked: the run fails the quality floor
        // (no silent empty episode) AND the recorded step outcome carries the human
        // "sign in to refresh your session" guidance for B11 to surface.
        var loader = Substitute.For<IUnattendedSectionLoader>();
        loader.LoadLinksAndConfigAsync("https://www.nytimes.com/", Arg.Any<CancellationToken>())
            .Returns(new UnattendedSectionLoad { Outcome = LoadOutcome.Blocked });

        var orchestrator = Substitute.For<IPodcastOrchestrator>();
        var runRepo = Substitute.For<IScheduledRunRepository>();
        var pipeline = new RecipeRunPipeline(
            loader,
            new SectionResolver(),
            orchestrator,
            Substitute.For<IPodcastBackgroundJobManager>(),
            runRepo,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IScheduleStore>(),
            NullLogger<RecipeRunPipeline>.Instance);

        var recipe = ScheduleRecipe.Create(
            "NYT Brief",
            Cadence.Daily(new TimeOnly(7, 0)),
            new[] { RecipeStep.Create("https://www.nytimes.com/", "nytimes.com", "nyt", "Front", required: true) });
        var run = ScheduledRun.Start(recipe.Id, recipe.Name, "2026-05-30@07:00");

        await pipeline.RunAsync(recipe, run);

        run.StepOutcomesJson.Should().NotBeNull();
        run.StepOutcomesJson.Should().Contain("sign in to refresh your session");
        run.StepOutcomesJson.Should().Contain("nytimes.com");
        await orchestrator.DidNotReceive().GeneratePodcastAsync(
            Arg.Any<Collection>(), Arg.Any<IProgress<PodcastProgress>?>(), Arg.Any<CancellationToken>());
    }
}
