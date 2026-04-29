// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Infrastructure.Bookmarks;
using TermReader.Persistence;
using TermReader.Persistence.Repositories;
using Xunit;

namespace TermReader.Tests.Bookmarks;

[Trait("Category", "Unit")]
public class BookmarkServiceTests : TestDatabaseFixture
{
    private readonly BookmarkRepository _repository;
    private readonly BookmarkService _sut;
    private readonly ILogger<BookmarkService> _logger;

    public BookmarkServiceTests()
    {
        _repository = new BookmarkRepository(DbContext);
        var unitOfWork = new UnitOfWork(DbContext, Substitute.For<ILogger<UnitOfWork>>());
        _logger = Substitute.For<ILogger<BookmarkService>>();
        _sut = new BookmarkService(_repository, unitOfWork, _logger);
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

    #region EnsureSeededAsync

    [Fact]
    public async Task EnsureSeededAsync_SeedsDefaultBookmarks()
    {
        await _sut.EnsureSeededAsync();

        var all = await _sut.GetAllBookmarksAsync();
        all.Should().HaveCount(9);
        all.Select(b => b.Name).Should().Contain("Maclean's");
        all.Select(b => b.Name).Should().Contain("CBC News");
        all.Select(b => b.Name).Should().Contain("NYT Today's Paper");
        all.Select(b => b.Name).Should().Contain("The Verge");
        all.Select(b => b.Name).Should().Contain("The Toronto Star");
        all.Select(b => b.Name).Should().Contain("Techmeme");
        all.Select(b => b.Name).Should().Contain("Wall Street Journal");
        all.Select(b => b.Name).Should().Contain("Wired");
        all.Select(b => b.Name).Should().Contain("The New Yorker");
    }

    [Fact]
    public async Task EnsureSeededAsync_DoesNotDuplicateDefaults()
    {
        await _sut.EnsureSeededAsync();
        await _sut.EnsureSeededAsync(); // Second call

        var all = await _sut.GetAllBookmarksAsync();
        all.Should().HaveCount(9);
    }

    [Fact]
    public async Task EnsureSeededAsync_AddsMissingDefaults_AlongsideUserBookmarks()
    {
        await _sut.AddBookmarkAsync("Custom", "https://custom.com");
        await _sut.EnsureSeededAsync();

        var all = await _sut.GetAllBookmarksAsync();
        all.Should().Contain(b => b.Url == "https://custom.com", "user-added bookmark preserved");
        all.Should().Contain(b => b.Url == "https://www.wired.com", "missing default added");
        all.Where(b => b.Url == "https://custom.com").Should().HaveCount(1, "no duplicates of the user bookmark");
    }

    #endregion
}
