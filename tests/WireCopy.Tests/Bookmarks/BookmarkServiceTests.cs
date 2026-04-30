// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Infrastructure.Bookmarks;
using WireCopy.Persistence;
using WireCopy.Persistence.Repositories;
using Xunit;

namespace WireCopy.Tests.Bookmarks;

[Trait("Category", "Unit")]
public class BookmarkServiceTests : TestDatabaseFixture, IDisposable
{
    private readonly BookmarkRepository _repository;
    private readonly BookmarkService _sut;
    private readonly ILogger<BookmarkService> _logger;
    private readonly string _tempDir;
    private readonly JsonBookmarkConfigStore _configStore;
    private readonly BookmarkReconciler _reconciler;
    private bool _disposed;

    public BookmarkServiceTests()
    {
        _repository = new BookmarkRepository(DbContext);
        var unitOfWork = new UnitOfWork(DbContext, Substitute.For<ILogger<UnitOfWork>>());
        _logger = Substitute.For<ILogger<BookmarkService>>();

        _tempDir = Path.Combine(Path.GetTempPath(), $"wirecopy-bookmark-svc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var configPath = Path.Combine(_tempDir, "bookmarks.json");
        _configStore = new JsonBookmarkConfigStore(
            Substitute.For<ILogger<JsonBookmarkConfigStore>>(),
            configPath,
            typeof(AppDbContext).Assembly);
        _reconciler = new BookmarkReconciler(
            _configStore,
            _repository,
            unitOfWork,
            Substitute.For<ILogger<BookmarkReconciler>>());

        _sut = new BookmarkService(_repository, unitOfWork, _configStore, _reconciler, _logger);
    }

    #region GetAllBookmarksAsync

    [Fact]
    public async Task GetAllBookmarksAsync_WhenEmpty_ReturnsEmptyList()
    {
        var result = await _sut.GetAllBookmarksAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllBookmarksAsync_ReturnsBookmarksOrderedBySortOrder()
    {
        await _sut.AddBookmarkAsync("Zeta", "https://zeta.com");
        await _sut.AddBookmarkAsync("Alpha", "https://alpha.com");

        var result = await _sut.GetAllBookmarksAsync();

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("Zeta");  // SortOrder 0
        result[1].Name.Should().Be("Alpha"); // SortOrder 1
    }

    #endregion

    #region AddBookmarkAsync

    [Fact]
    public async Task AddBookmarkAsync_CreatesBookmarkWithAutoSortOrder()
    {
        var first = await _sut.AddBookmarkAsync("First", "https://first.com");
        var second = await _sut.AddBookmarkAsync("Second", "https://second.com");

        first.SortOrder.Should().Be(0);
        second.SortOrder.Should().Be(1);
    }

    [Fact]
    public async Task AddBookmarkAsync_PersistsToDatabase()
    {
        await _sut.AddBookmarkAsync("Test", "https://test.com");

        var all = await _sut.GetAllBookmarksAsync();
        all.Should().HaveCount(1);
        all[0].Name.Should().Be("Test");
        all[0].Url.Should().Be("https://test.com");
    }

    [Fact]
    public async Task AddBookmarkAsync_WritesToConfigFile()
    {
        await _sut.AddBookmarkAsync("Test", "https://test.com");

        File.Exists(_configStore.UserConfigPath).Should().BeTrue();
        var config = await _configStore.LoadUserConfigAsync();
        config.Should().NotBeNull();
        config!.Bookmarks.Should().ContainSingle(e => e.Url == "https://test.com" && e.Name == "Test");
    }

    #endregion

    #region DeleteBookmarkAsync

    [Fact]
    public async Task DeleteBookmarkAsync_RemovesBookmark()
    {
        var bookmark = await _sut.AddBookmarkAsync("ToDelete", "https://delete.com");

        await _sut.DeleteBookmarkAsync(bookmark.Id);

        var all = await _sut.GetAllBookmarksAsync();
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteBookmarkAsync_WithInvalidId_ThrowsException()
    {
        var act = () => _sut.DeleteBookmarkAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeleteBookmarkAsync_WritesToConfigFile()
    {
        var keep = await _sut.AddBookmarkAsync("Keep", "https://keep.com");
        var drop = await _sut.AddBookmarkAsync("Drop", "https://drop.com");

        await _sut.DeleteBookmarkAsync(drop.Id);

        var config = await _configStore.LoadUserConfigAsync();
        config!.Bookmarks.Should().ContainSingle();
        config.Bookmarks[0].Url.Should().Be("https://keep.com");
        // Sanity: the kept bookmark id matches the DB row.
        keep.Id.Should().NotBe(drop.Id);
    }

    #endregion

    #region RenameBookmarkAsync

    [Fact]
    public async Task RenameBookmarkAsync_UpdatesName()
    {
        var bookmark = await _sut.AddBookmarkAsync("Old", "https://test.com");

        await _sut.RenameBookmarkAsync(bookmark.Id, "New");

        var all = await _sut.GetAllBookmarksAsync();
        all[0].Name.Should().Be("New");
    }

    [Fact]
    public async Task RenameBookmarkAsync_WithInvalidId_ThrowsException()
    {
        var act = () => _sut.RenameBookmarkAsync(Guid.NewGuid(), "New");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RenameBookmarkAsync_WritesToConfigFile()
    {
        var bookmark = await _sut.AddBookmarkAsync("Old", "https://rename.com");

        await _sut.RenameBookmarkAsync(bookmark.Id, "New");

        var config = await _configStore.LoadUserConfigAsync();
        config!.Bookmarks.Single(e => e.Url == "https://rename.com").Name.Should().Be("New");
    }

    #endregion

    #region MoveBookmarkUpAsync

    [Fact]
    public async Task MoveBookmarkUpAsync_SwapsWithPrevious()
    {
        var first = await _sut.AddBookmarkAsync("First", "https://first.com");
        var second = await _sut.AddBookmarkAsync("Second", "https://second.com");

        await _sut.MoveBookmarkUpAsync(second.Id);

        var all = await _sut.GetAllBookmarksAsync();
        all[0].Name.Should().Be("Second");
        all[1].Name.Should().Be("First");

        // Sanity-check the original IDs survived the swap rather than being recreated.
        first.Id.Should().NotBe(second.Id);
        all.Select(b => b.Id).Should().Contain(new[] { first.Id, second.Id });
    }

    [Fact]
    public async Task MoveBookmarkUpAsync_AtTop_DoesNothing()
    {
        var first = await _sut.AddBookmarkAsync("First", "https://first.com");
        await _sut.AddBookmarkAsync("Second", "https://second.com");

        await _sut.MoveBookmarkUpAsync(first.Id);

        var all = await _sut.GetAllBookmarksAsync();
        all[0].Name.Should().Be("First");
        all[1].Name.Should().Be("Second");
    }

    [Fact]
    public async Task MoveBookmarkUpAsync_WithInvalidId_ThrowsException()
    {
        var act = () => _sut.MoveBookmarkUpAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MoveBookmarkUpAsync_WritesToConfigFile_WithNewOrder()
    {
        await _sut.AddBookmarkAsync("First", "https://first.com");
        var second = await _sut.AddBookmarkAsync("Second", "https://second.com");

        await _sut.MoveBookmarkUpAsync(second.Id);

        var config = await _configStore.LoadUserConfigAsync();
        config!.Bookmarks.Should().HaveCount(2);
        config.Bookmarks[0].Url.Should().Be("https://second.com");
        config.Bookmarks[1].Url.Should().Be("https://first.com");
    }

    #endregion

    #region MoveBookmarkDownAsync

    [Fact]
    public async Task MoveBookmarkDownAsync_SwapsWithNext()
    {
        var first = await _sut.AddBookmarkAsync("First", "https://first.com");
        await _sut.AddBookmarkAsync("Second", "https://second.com");

        await _sut.MoveBookmarkDownAsync(first.Id);

        var all = await _sut.GetAllBookmarksAsync();
        all[0].Name.Should().Be("Second");
        all[1].Name.Should().Be("First");
    }

    [Fact]
    public async Task MoveBookmarkDownAsync_AtBottom_DoesNothing()
    {
        await _sut.AddBookmarkAsync("First", "https://first.com");
        var second = await _sut.AddBookmarkAsync("Second", "https://second.com");

        await _sut.MoveBookmarkDownAsync(second.Id);

        var all = await _sut.GetAllBookmarksAsync();
        all[0].Name.Should().Be("First");
        all[1].Name.Should().Be("Second");
    }

    [Fact]
    public async Task MoveBookmarkDownAsync_WithInvalidId_ThrowsException()
    {
        var act = () => _sut.MoveBookmarkDownAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region ReconcileAsync

    [Fact]
    public async Task ReconcileAsync_EmptyDbAndConfig_PopulatesFromShippedDefaults()
    {
        await _sut.ReconcileAsync();

        var all = await _sut.GetAllBookmarksAsync();
        all.Should().NotBeEmpty();
        all.Select(b => b.Url).Should().Contain("https://www.wired.com");
        all.Select(b => b.Url).Should().Contain("https://www.newyorker.com");
        File.Exists(_configStore.UserConfigPath).Should().BeTrue();
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotDuplicateOnSecondRun()
    {
        await _sut.ReconcileAsync();
        var firstCount = (await _sut.GetAllBookmarksAsync()).Count;
        await _sut.ReconcileAsync();
        var secondCount = (await _sut.GetAllBookmarksAsync()).Count;

        secondCount.Should().Be(firstCount);
    }

    #endregion

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
