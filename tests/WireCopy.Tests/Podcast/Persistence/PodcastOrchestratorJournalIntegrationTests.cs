// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Podcast;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Cache;
using WireCopy.Persistence;
using WireCopy.Persistence.Repositories;
using Xunit;

namespace WireCopy.Tests.Podcast.Persistence;

/// <summary>
/// End-to-end check that <see cref="PodcastOrchestrator.GeneratePodcastAsync"/>
/// (workspace-hzjs F.3) creates a PodcastJob row, threads progress events
/// through the journal, and finalizes the row with the correct terminal
/// status. Uses an in-memory SQLite database for the journal so the real
/// repository/EF Core path is exercised. Narration goes through a substituted
/// <see cref="IPodcastNarrator"/> (workspace-c3ec4.2).
/// </summary>
[Trait("Category", "Unit")]
public class PodcastOrchestratorJournalIntegrationTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly ServiceProvider _provider;
    private readonly string _tempDir;

    public PodcastOrchestratorJournalIntegrationTests()
    {
        _connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = new AppDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(_dbOptions));
        services.AddScoped<IPodcastJobRepository, PodcastJobRepository>();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(
            sp.GetRequiredService<AppDbContext>(),
            NullLogger<UnitOfWork>.Instance));
        _provider = services.BuildServiceProvider();

        _tempDir = Path.Combine(Path.GetTempPath(), $"orch-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best effort
        }

        GC.SuppressFinalize(this);
    }

    private PodcastOrchestrator BuildOrchestrator(
        IPodcastNarrator narrator,
        IPodcastPublisher publisher)
    {
        var pageLoader = Substitute.For<IPageLoader>();
        var contentExtractor = Substitute.For<IReadableContentExtractor>();
        var preloadService = Substitute.For<IPreloadService>();
        var pageCache = Substitute.For<IPageCache>();
        var browserSession = Substitute.For<IBrowserSession>();
        browserSession.IsBrowserAvailable.Returns(true);
        var articleCache = Substitute.For<IArticleContentCache>();
        var pageAccessQueue = Substitute.For<IPageAccessQueue>();
        pageAccessQueue.AcquireAsync(Arg.Any<PageAccessPriority>(), Arg.Any<CancellationToken>())
            .Returns(_ => new PageLease(Substitute.For<Microsoft.Playwright.IPage>(), () => { }));

        var contentProvider = new ReadingListContentProvider(
            pageLoader,
            contentExtractor,
            preloadService,
            pageCache,
            browserSession,
            pageAccessQueue,
            articleCache,
            NullLogger<ReadingListContentProvider>.Instance);

        var podcastConfig = Options.Create(new PodcastConfiguration
        {
            Title = "Test Podcast",
            Description = "Test description",
            Author = "Tester",
            Language = "en-us",
            TempDirectory = _tempDir,
        });

        return new PodcastOrchestrator(
            contentProvider,
            narrator,
            publisher,
            podcastConfig,
            NullLogger<PodcastOrchestrator>.Instance,
            settingsStore: null,
            scopeFactory: _provider.GetRequiredService<IServiceScopeFactory>());
    }

    // Guards:   a run that fails pre-flight because the narrator isn't configured never writes a PodcastJob row.
    // Fails if: the orchestrator opens the job journal before checking IPodcastNarrator.IsConfigured.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorJournalIntegrationTests.GeneratePodcastAsync_NarratorNotConfigured_DoesNotCreateJobRow"
    [Fact]
    public async Task GeneratePodcastAsync_NarratorNotConfigured_DoesNotCreateJobRow()
    {
        var narrator = Substitute.For<IPodcastNarrator>();
        narrator.IsConfigured.Returns(false);
        var publisher = Substitute.For<IPodcastPublisher>();

        var orch = BuildOrchestrator(narrator, publisher);
        var collection = Collection.Create("List");

        var result = await orch.GeneratePodcastAsync(collection);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("audio-engine");
        using var ctx = new AppDbContext(_dbOptions);
        (await ctx.PodcastJobs.CountAsync()).Should().Be(0,
            "pre-flight failures must not pollute the job table");
    }

    // Guards:   once pre-flight passes, a run with no articles still creates a PodcastJob row and finalizes it
    //           as TotalFailure / Done with a target path, so the orphan sweep and resume path can see it.
    // Fails if: the journal is created only after extraction, or the empty-collection failure path skips
    //           finalizing the row.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~PodcastOrchestratorJournalIntegrationTests.GeneratePodcastAsync_AllPreflightOk_NoArticles_CreatesAndFinalizesJob"
    [Fact]
    public async Task GeneratePodcastAsync_AllPreflightOk_NoArticles_CreatesAndFinalizesJob()
    {
        var narrator = Substitute.For<IPodcastNarrator>();
        narrator.IsConfigured.Returns(true);
        narrator.EngineName.Returns("openai");
        var publisher = Substitute.For<IPodcastPublisher>();
        publisher.ResolveFeedUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://example.com/feed.xml");

        var orch = BuildOrchestrator(narrator, publisher);
        var collection = Collection.Create("Empty List");

        var result = await orch.GeneratePodcastAsync(collection);

        // Pipeline returns Failure because the collection has no articles —
        // but the journal SHOULD have been created (we're past pre-flight)
        // and finalized as TotalFailure.
        result.Success.Should().BeFalse();

        using var ctx = new AppDbContext(_dbOptions);
        var row = await ctx.PodcastJobs.FirstAsync();
        row.CollectionId.Should().Be(collection.Id);
        row.CollectionTitle.Should().Be("Empty List");
        row.Status.Should().Be(PodcastJobStatus.TotalFailure);
        row.Phase.Should().Be(PodcastJobPhase.Done);
        row.TargetLocalPath.Should().NotBeNullOrEmpty();
    }
}
