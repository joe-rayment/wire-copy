// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Infrastructure.Bookmarks;
using WireCopy.Persistence;
using WireCopy.Persistence.Repositories;
using Xunit;

namespace WireCopy.Tests.Bookmarks;

[Trait("Category", "Unit")]
public sealed class BookmarkReconcilerTests : TestDatabaseFixture, IDisposable
{
    private readonly BookmarkRepository _repository;
    private readonly UnitOfWork _unitOfWork;
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly JsonBookmarkConfigStore _configStore;
    private readonly BookmarkReconciler _sut;
    private bool _disposed;

    public BookmarkReconcilerTests()
    {
        _repository = new BookmarkRepository(DbContext);
        _unitOfWork = new UnitOfWork(DbContext, Substitute.For<ILogger<UnitOfWork>>());

        _tempDir = Path.Combine(Path.GetTempPath(), $"wirecopy-bookmark-recon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "bookmarks.json");

        _configStore = new JsonBookmarkConfigStore(
            Substitute.For<ILogger<JsonBookmarkConfigStore>>(),
            _configPath,
            typeof(AppDbContext).Assembly);

        _sut = new BookmarkReconciler(
            _configStore,
            _repository,
            _unitOfWork,
            Substitute.For<ILogger<BookmarkReconciler>>());
    }

    [Fact]
    public async Task Reconcile_EmptyDbAndConfig_PopulatesFromShippedDefaults()
    {
        File.Exists(_configPath).Should().BeFalse();

        await _sut.ReconcileAsync();

        File.Exists(_configPath).Should().BeTrue();
        var dbBookmarks = await _repository.GetAllAsync();
        dbBookmarks.Should().NotBeEmpty();
        dbBookmarks.Select(b => b.Url).Should().Contain("https://www.wired.com");
        dbBookmarks.Select(b => b.Url).Should().Contain("https://www.newyorker.com");

        // The user config should now mirror the shipped defaults.
        var config = await _configStore.LoadUserConfigAsync();
        config.Should().NotBeNull();
        config!.Version.Should().BeGreaterThan(0);
        config.Bookmarks.Select(e => e.Url).Should().Contain("https://www.wired.com");
    }

    [Fact]
    public async Task Reconcile_NewDefaultInShippedFile_AddedToExistingDb()
    {
        // Simulate an "old user" whose config file pre-dates a default. Drop
        // Wired from the user file but keep the others, then reconcile.
        var shipped = await _configStore.LoadShippedDefaultsAsync();
        var trimmed = new BookmarkConfigFile(
            shipped.Version,
            shipped.Bookmarks.Where(e => e.Url != "https://www.wired.com").ToList());
        await _configStore.SaveUserConfigAsync(trimmed);

        // Mirror that to the DB so the existing-user-upgrade branch isn't taken.
        for (var i = 0; i < trimmed.Bookmarks.Count; i++)
        {
            await _repository.AddAsync(Bookmark.Create(trimmed.Bookmarks[i].Name, trimmed.Bookmarks[i].Url, i));
        }

        await _unitOfWork.SaveChangesAsync();

        await _sut.ReconcileAsync();

        var dbBookmarks = await _repository.GetAllAsync();
        dbBookmarks.Select(b => b.Url).Should().Contain("https://www.wired.com");
        var config = await _configStore.LoadUserConfigAsync();
        config!.Bookmarks.Select(e => e.Url).Should().Contain("https://www.wired.com");
    }

    [Fact]
    public async Task Reconcile_UserHasCustomBookmark_PreservedAfterReconcile()
    {
        // Write a config that contains one custom bookmark only.
        var custom = new BookmarkConfigFile(
            JsonBookmarkConfigStore.CurrentSchemaVersion,
            new List<BookmarkConfigEntry> { new("My Site", "https://my.site") });
        await _configStore.SaveUserConfigAsync(custom);

        await _sut.ReconcileAsync();

        var dbBookmarks = await _repository.GetAllAsync();
        dbBookmarks.Select(b => b.Url).Should().Contain("https://my.site");
        // Reconciler also applies missing shipped defaults additively.
        dbBookmarks.Select(b => b.Url).Should().Contain("https://www.wired.com");
    }

    [Fact]
    public async Task Reconcile_RemovedDefault_StillInUserDb_PreservedNotDeleted()
    {
        // User has a bookmark in the DB whose URL isn't in the shipped defaults
        // and isn't in the (empty) user config: it must survive reconciliation.
        await _repository.AddAsync(Bookmark.Create("Defunct Site", "https://defunct.example", 0));
        await _unitOfWork.SaveChangesAsync();

        // Existing-user upgrade path: no config file, but DB has rows.
        await _sut.ReconcileAsync();

        var dbBookmarks = await _repository.GetAllAsync();
        dbBookmarks.Select(b => b.Url).Should().Contain("https://defunct.example");
    }

    [Fact]
    public async Task Reconcile_BookmarkRenamedInConfig_NameUpdatedInDb()
    {
        await _repository.AddAsync(Bookmark.Create("Wired", "https://www.wired.com", 0));
        await _unitOfWork.SaveChangesAsync();

        var renamed = new BookmarkConfigFile(
            JsonBookmarkConfigStore.CurrentSchemaVersion,
            new List<BookmarkConfigEntry> { new("WIRED Magazine", "https://www.wired.com") });
        await _configStore.SaveUserConfigAsync(renamed);

        await _sut.ReconcileAsync();

        var dbBookmark = (await _repository.GetAllAsync())
            .Single(b => b.Url == "https://www.wired.com");
        dbBookmark.Name.Should().Be("WIRED Magazine");
    }

    [Fact]
    public async Task Reconcile_ExistingUserUpgrade_DbExportedToConfigOnFirstRun()
    {
        await _repository.AddAsync(Bookmark.Create("Custom A", "https://a.example", 0));
        await _repository.AddAsync(Bookmark.Create("Custom B", "https://b.example", 1));
        await _unitOfWork.SaveChangesAsync();

        File.Exists(_configPath).Should().BeFalse();

        await _sut.ReconcileAsync();

        File.Exists(_configPath).Should().BeTrue();
        var config = await _configStore.LoadUserConfigAsync();
        config.Should().NotBeNull();
        config!.Bookmarks.Select(e => e.Url).Should().Contain(new[] { "https://a.example", "https://b.example" });
        // Reconciler also adds shipped defaults additively to the now-extant config.
        config.Bookmarks.Select(e => e.Url).Should().Contain("https://www.wired.com");
    }

    [Fact]
    public async Task Reconcile_AppliesConfigOrderToDbSortOrder()
    {
        var config = new BookmarkConfigFile(
            JsonBookmarkConfigStore.CurrentSchemaVersion,
            new List<BookmarkConfigEntry>
            {
                new("Third", "https://c.example"),
                new("First", "https://a.example"),
                new("Second", "https://b.example"),
            });
        await _configStore.SaveUserConfigAsync(config);

        await _sut.ReconcileAsync();

        var dbBookmarks = await _repository.GetAllAsync();
        // Filter out shipped defaults that may have been added.
        var ours = dbBookmarks.Where(b => b.Url is "https://a.example" or "https://b.example" or "https://c.example")
            .OrderBy(b => b.SortOrder)
            .ToList();
        ours.Should().HaveCount(3);
        ours[0].Url.Should().Be("https://c.example");
        ours[1].Url.Should().Be("https://a.example");
        ours[2].Url.Should().Be("https://b.example");
    }

    public new void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }

        _disposed = true;
    }
}
