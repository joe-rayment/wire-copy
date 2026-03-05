// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Infrastructure.Bookmarks;
using TermReader.Persistence.Repositories;
using Xunit;

namespace TermReader.Tests.Bookmarks;

public class BookmarkServiceTests : TestDatabaseFixture
{
    private readonly BookmarkRepository _repository;
    private readonly BookmarkService _sut;
    private readonly ILogger<BookmarkService> _logger;

    public BookmarkServiceTests()
    {
        _repository = new BookmarkRepository(DbContext);
        _logger = Substitute.For<ILogger<BookmarkService>>();
        _sut = new BookmarkService(_repository, _logger);
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

    #endregion

    #region EnsureSeededAsync

    [Fact]
    public async Task EnsureSeededAsync_SeedsDefaultBookmarks()
    {
        await _sut.EnsureSeededAsync();

        var all = await _sut.GetAllBookmarksAsync();
        all.Should().HaveCount(3);
        all.Select(b => b.Name).Should().Contain("Maclean's");
        all.Select(b => b.Name).Should().Contain("NYT Today's Paper");
        all.Select(b => b.Name).Should().Contain("The Verge");
    }

    [Fact]
    public async Task EnsureSeededAsync_DoesNotDuplicateDefaults()
    {
        await _sut.EnsureSeededAsync();
        await _sut.EnsureSeededAsync(); // Second call

        var all = await _sut.GetAllBookmarksAsync();
        all.Should().HaveCount(3);
    }

    [Fact]
    public async Task EnsureSeededAsync_DoesNotSeedWhenBookmarksExist()
    {
        await _sut.AddBookmarkAsync("Custom", "https://custom.com");
        await _sut.EnsureSeededAsync();

        var all = await _sut.GetAllBookmarksAsync();
        all.Should().HaveCount(1); // Only the custom one
        all[0].Name.Should().Be("Custom");
    }

    #endregion
}
