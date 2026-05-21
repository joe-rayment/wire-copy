// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Podcast;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Podcast;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Persistence;
using WireCopy.Persistence.Repositories;
using Xunit;

namespace WireCopy.Tests.Podcast.Persistence;

[Trait("Category", "Unit")]
public class PodcastJobJournalTests
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly IServiceScopeFactory _scopeFactory;

    public PodcastJobJournalTests()
    {
        _connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(_options));
        services.AddScoped<IPodcastJobRepository, PodcastJobRepository>();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(
            sp.GetRequiredService<AppDbContext>(),
            NullLogger<UnitOfWork>.Instance));
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static Collection CreateCollection(string name = "Reading List")
    {
        var c = Collection.Create(name);
        return c;
    }

    [Fact]
    public async Task CreateAsync_PersistsRunningRow_WithTargets()
    {
        var collection = CreateCollection();

        var journal = await PodcastJobJournal.CreateAsync(
            _scopeFactory,
            collection,
            "/tmp/foo.m4a",
            "https://x/feed.xml",
            TimeSpan.FromMilliseconds(1),
            NullLogger.Instance,
            CancellationToken.None);

        journal.JobId.Should().NotBe(Guid.Empty);

        using var ctx = new AppDbContext(_options);
        var row = await ctx.PodcastJobs.FirstAsync(j => j.Id == journal.JobId);
        row.Status.Should().Be(PodcastJobStatus.Running);
        row.Phase.Should().Be(PodcastJobPhase.NotStarted);
        row.CollectionId.Should().Be(collection.Id);
        row.CollectionTitle.Should().Be(collection.Name);
        row.TargetLocalPath.Should().Be("/tmp/foo.m4a");
        row.TargetFeedUrl.Should().Be("https://x/feed.xml");
    }

    [Fact]
    public async Task FinishAsync_FullSuccessResult_MarksFullSuccess()
    {
        var journal = await PodcastJobJournal.CreateAsync(
            _scopeFactory,
            CreateCollection(),
            null,
            null,
            TimeSpan.FromMilliseconds(1),
            NullLogger.Instance,
            CancellationToken.None);

        var result = PodcastResult.Successful(
            feedUrl: "https://x/feed.xml",
            localFilePath: "/tmp/foo.m4a",
            totalDuration: TimeSpan.FromMinutes(3),
            articlesProcessed: 5,
            articlesFailed: 0,
            fileSizeBytes: 1024);

        await journal.FinishAsync(result, null, CancellationToken.None);

        using var ctx = new AppDbContext(_options);
        var row = await ctx.PodcastJobs.FirstAsync(j => j.Id == journal.JobId);
        row.Status.Should().Be(PodcastJobStatus.FullSuccess);
        row.Phase.Should().Be(PodcastJobPhase.Done);
        row.ErrorClass.Should().BeNull();
    }

    [Fact]
    public async Task FinishAsync_PartialSuccess_MarksPartialSuccess()
    {
        var journal = await PodcastJobJournal.CreateAsync(
            _scopeFactory,
            CreateCollection(),
            null,
            null,
            TimeSpan.FromMilliseconds(1),
            NullLogger.Instance,
            CancellationToken.None);

        var result = PodcastResult.Successful(
            feedUrl: "https://x/feed.xml",
            localFilePath: "/tmp/foo.m4a",
            totalDuration: TimeSpan.FromMinutes(3),
            articlesProcessed: 3,
            articlesFailed: 2,  // <- non-zero forces PartialSuccess
            fileSizeBytes: 1024);

        await journal.FinishAsync(result, null, CancellationToken.None);

        using var ctx = new AppDbContext(_options);
        var row = await ctx.PodcastJobs.FirstAsync(j => j.Id == journal.JobId);
        row.Status.Should().Be(PodcastJobStatus.PartialSuccess);
        row.ErrorClass.Should().Be("Partial");
    }

    [Fact]
    public async Task FinishAsync_FailureResult_MarksTotalFailure()
    {
        var journal = await PodcastJobJournal.CreateAsync(
            _scopeFactory,
            CreateCollection(),
            null,
            null,
            TimeSpan.FromMilliseconds(1),
            NullLogger.Instance,
            CancellationToken.None);

        var result = PodcastResult.Failure("FFmpeg is not installed.");

        await journal.FinishAsync(result, null, CancellationToken.None);

        using var ctx = new AppDbContext(_options);
        var row = await ctx.PodcastJobs.FirstAsync(j => j.Id == journal.JobId);
        row.Status.Should().Be(PodcastJobStatus.TotalFailure);
        row.ErrorMessage.Should().Be("FFmpeg is not installed.");
    }

    [Fact]
    public async Task MarkCancelledAsync_FlipsStatus()
    {
        var journal = await PodcastJobJournal.CreateAsync(
            _scopeFactory,
            CreateCollection(),
            null,
            null,
            TimeSpan.FromMilliseconds(1),
            NullLogger.Instance,
            CancellationToken.None);

        await journal.MarkCancelledAsync(null, CancellationToken.None);

        using var ctx = new AppDbContext(_options);
        var row = await ctx.PodcastJobs.FirstAsync(j => j.Id == journal.JobId);
        row.Status.Should().Be(PodcastJobStatus.Cancelled);
        row.ErrorClass.Should().Be("Cancelled");
    }

    [Fact]
    public async Task MarkFailedAsync_FlipsStatus_WithExceptionDetails()
    {
        var journal = await PodcastJobJournal.CreateAsync(
            _scopeFactory,
            CreateCollection(),
            null,
            null,
            TimeSpan.FromMilliseconds(1),
            NullLogger.Instance,
            CancellationToken.None);

        await journal.MarkFailedAsync(
            new InvalidOperationException("boom"),
            null,
            CancellationToken.None);

        using var ctx = new AppDbContext(_options);
        var row = await ctx.PodcastJobs.FirstAsync(j => j.Id == journal.JobId);
        row.Status.Should().Be(PodcastJobStatus.TotalFailure);
        row.ErrorClass.Should().Be(nameof(InvalidOperationException));
        row.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public async Task ReportProgress_PersistsLatestSnapshot()
    {
        var journal = await PodcastJobJournal.CreateAsync(
            _scopeFactory,
            CreateCollection(),
            null,
            null,
            TimeSpan.FromMilliseconds(1),
            NullLogger.Instance,
            CancellationToken.None);

        journal.ReportProgress(new PodcastProgress
        {
            Phase = PodcastPhase.GeneratingAudio,
            CurrentArticle = 2,
            TotalArticles = 5,
            ArticleTitle = "Article Two",
            PercentComplete = 30,
        });

        // The orchestrator's wrapper updates lastSnapshot and the journal's
        // FinishAsync writes it. Simulate that pathway with a finalize.
        var snapshot = new PodcastProgress
        {
            Phase = PodcastPhase.Publishing,
            CurrentArticle = 5,
            TotalArticles = 5,
            PercentComplete = 95,
        };
        await journal.FinishAsync(
            PodcastResult.Successful(
                feedUrl: "https://x/feed.xml",
                localFilePath: "/tmp/foo.m4a",
                totalDuration: TimeSpan.FromMinutes(3),
                articlesProcessed: 5,
                articlesFailed: 0,
                fileSizeBytes: 1024),
            snapshot,
            CancellationToken.None);

        using var ctx = new AppDbContext(_options);
        var row = await ctx.PodcastJobs.FirstAsync(j => j.Id == journal.JobId);
        row.Status.Should().Be(PodcastJobStatus.FullSuccess);
        row.LastProgressJson.Should().NotBeNullOrEmpty();
        row.LastProgressJson.Should().Contain("\"percentComplete\":95");
    }

    [Fact]
    public void MapStatus_RespectsExplicitTotalFailureClassification()
    {
        var result = PodcastResult.Successful(
            feedUrl: "https://x/feed.xml",
            localFilePath: "/tmp/foo.m4a",
            totalDuration: TimeSpan.FromMinutes(3),
            articlesProcessed: 5,
            articlesFailed: 0,
            fileSizeBytes: 1024) with
        {
            Classification = PodcastResultClassification.TotalFailure,
        };

        PodcastJobJournal.MapStatus(result).Should().Be(PodcastJobStatus.TotalFailure);
    }

    [Fact]
    public void MapPhase_TranslatesEveryPodcastPhase()
    {
        PodcastJobJournal.MapPhase(PodcastPhase.CachingContent).Should().Be(PodcastJobPhase.Extraction);
        PodcastJobJournal.MapPhase(PodcastPhase.GeneratingAudio).Should().Be(PodcastJobPhase.Synthesis);
        PodcastJobJournal.MapPhase(PodcastPhase.AssemblingAudio).Should().Be(PodcastJobPhase.Assembly);
        PodcastJobJournal.MapPhase(PodcastPhase.Publishing).Should().Be(PodcastJobPhase.Publish);
    }
}
