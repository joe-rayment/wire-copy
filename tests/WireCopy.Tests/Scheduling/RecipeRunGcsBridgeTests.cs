// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-frpl.17 (B13) — regression for the real bug the live e2e surfaced: the
/// scheduled/run-now path never bridged the user's GcsBucketName setting into the
/// (config-bound) GcsConfiguration, so scheduled runs published to a null bucket. The
/// pipeline must bridge it (as the manual flow does) before generation.
/// </summary>
[Trait("Category", "Unit")]
public class RecipeRunGcsBridgeTests
{
    [Fact]
    public async Task Generation_BridgesGcsBucketNameFromSettings_WhenConfigBlank()
    {
        var gcs = Options.Create(new GcsConfiguration { BucketName = null });
        var settings = Substitute.For<IUserSettingsStore>();
        settings.Get("GcsBucketName").Returns("tr_list_reader");

        var loader = Substitute.For<IUnattendedSectionLoader>();
        loader.LoadLinksAndConfigAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UnattendedSectionLoad
            {
                Outcome = LoadOutcome.Ok,
                Links = new List<LinkInfo> { new() { Url = "u1", DisplayText = "A", Type = LinkType.Content, ImportanceScore = 50, ParentSelector = "div.top a" } },
                Config = new SiteHierarchyConfig { Domain = "x", UrlPattern = ".*", Sections = new() { new() { Name = "Top", SortOrder = 0, ParentSelectors = new() { "div.top" } } }, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ModelVersion = "t" },
            });

        var orchestrator = Substitute.For<IPodcastOrchestrator>();
        orchestrator.ResolveTargetsAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>())
            .Returns(new PodcastTargets { LocalFilePath = "/tmp/o.m4b" });
        orchestrator.GeneratePodcastAsync(Arg.Any<Collection>(), Arg.Any<IProgress<PodcastProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(PodcastResult.Successful("https://feed/x.xml", "/tmp/o.m4b", TimeSpan.FromMinutes(1), 1, 0, 1024));

        var pipeline = new RecipeRunPipeline(
            loader,
            new SectionResolver(),
            orchestrator,
            Substitute.For<IPodcastBackgroundJobManager>(),
            Substitute.For<IScheduledRunRepository>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IScheduleStore>(),
            NullLogger<RecipeRunPipeline>.Instance,
            semanticRecovery: null,
            gcsOptions: gcs,
            settingsStore: settings);

        var recipe = ScheduleRecipe.Create("Brief", Cadence.Daily(new TimeOnly(7, 0)),
            new[] { RecipeStep.Create("https://x/", "x", "x", "Top", required: true) });
        var run = ScheduledRun.Start(recipe.Id, recipe.Name, "2026-06-01@07:00");

        await pipeline.RunAsync(recipe, run);

        gcs.Value.BucketName.Should().Be("tr_list_reader", "the scheduled path must bridge the bucket setting before publishing");
    }

    [Fact]
    public async Task Generation_DoesNotOverrideAnAlreadyConfiguredBucket()
    {
        var gcs = Options.Create(new GcsConfiguration { BucketName = "existing-bucket" });
        var settings = Substitute.For<IUserSettingsStore>();
        settings.Get("GcsBucketName").Returns("tr_list_reader");

        var loader = Substitute.For<IUnattendedSectionLoader>();
        loader.LoadLinksAndConfigAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new UnattendedSectionLoad
            {
                Outcome = LoadOutcome.Ok,
                Links = new List<LinkInfo> { new() { Url = "u1", DisplayText = "A", Type = LinkType.Content, ImportanceScore = 50, ParentSelector = "div.top a" } },
                Config = new SiteHierarchyConfig { Domain = "x", UrlPattern = ".*", Sections = new() { new() { Name = "Top", SortOrder = 0, ParentSelectors = new() { "div.top" } } }, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ModelVersion = "t" },
            });
        var orchestrator = Substitute.For<IPodcastOrchestrator>();
        orchestrator.ResolveTargetsAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>()).Returns(new PodcastTargets { LocalFilePath = "/tmp/o.m4b" });
        orchestrator.GeneratePodcastAsync(Arg.Any<Collection>(), Arg.Any<IProgress<PodcastProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(PodcastResult.Successful("https://feed/x.xml", "/tmp/o.m4b", TimeSpan.FromMinutes(1), 1, 0, 1024));

        var pipeline = new RecipeRunPipeline(loader, new SectionResolver(), orchestrator,
            Substitute.For<IPodcastBackgroundJobManager>(), Substitute.For<IScheduledRunRepository>(),
            Substitute.For<IUnitOfWork>(), Substitute.For<IScheduleStore>(), NullLogger<RecipeRunPipeline>.Instance,
            semanticRecovery: null, gcsOptions: gcs, settingsStore: settings);
        var recipe = ScheduleRecipe.Create("Brief", Cadence.Daily(new TimeOnly(7, 0)), new[] { RecipeStep.Create("https://x/", "x", "x", "Top", required: true) });

        await pipeline.RunAsync(recipe, ScheduledRun.Start(recipe.Id, recipe.Name, "2026-06-01@07:00"));

        gcs.Value.BucketName.Should().Be("existing-bucket", "an explicitly-configured bucket is never overridden");
    }
}
