// Educational and personal use only.

using FluentAssertions;
using TermReader.Domain.Entities.Bookmarks;
using TermReader.Persistence.Repositories;
using Xunit;

namespace TermReader.Tests.Bookmarks;

public class BookmarkRepositoryTests : TestDatabaseFixture
{
    private readonly BookmarkRepository _sut;

    public BookmarkRepositoryTests()
    {
        _sut = new BookmarkRepository(DbContext);
    }

    #region GetAllAsync

    [Fact]
    public async Task GetAllAsync_EmptyDatabase_ReturnsEmptyList()
    {
        var result = await _sut.GetAllAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsOrderedBySortOrderThenName()
    {
        var b1 = Bookmark.Create("Zebra", "https://z.com", 1);
        var b2 = Bookmark.Create("Alpha", "https://a.com", 0);
        var b3 = Bookmark.Create("Beta", "https://b.com", 0);
        await _sut.AddAsync(b1);
        await _sut.AddAsync(b2);
        await _sut.AddAsync(b3);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetAllAsync();

        result.Should().HaveCount(3);
        result[0].Name.Should().Be("Alpha");
        result[1].Name.Should().Be("Beta");
        result[2].Name.Should().Be("Zebra");
    }

    #endregion

    #region GetByIdAsync

    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsBookmark()
    {
        var bookmark = Bookmark.Create("Test", "https://test.com", 0);
        await _sut.AddAsync(bookmark);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetByIdAsync(bookmark.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test");
        result.Url.Should().Be("https://test.com");
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    #endregion

    #region AddAsync and DeleteAsync

    [Fact]
    public async Task AddAsync_PersistsBookmark()
    {
        var bookmark = Bookmark.Create("New", "https://new.com", 0);
        await _sut.AddAsync(bookmark);
        await DbContext.SaveChangesAsync();

        using var verifyContext = CreateDbContext();
        var repo = new BookmarkRepository(verifyContext);
        var result = await repo.GetByIdAsync(bookmark.Id);
        result.Should().NotBeNull();
        result!.Name.Should().Be("New");
    }

    [Fact]
    public async Task DeleteAsync_RemovesBookmark()
    {
        var bookmark = Bookmark.Create("ToDelete", "https://delete.com", 0);
        await _sut.AddAsync(bookmark);
        await DbContext.SaveChangesAsync();

        await _sut.DeleteAsync(bookmark);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetByIdAsync(bookmark.Id);
        result.Should().BeNull();
    }

    #endregion

    #region GetNextSortOrderAsync

    [Fact]
    public async Task GetNextSortOrderAsync_EmptyTable_ReturnsZero()
    {
        var result = await _sut.GetNextSortOrderAsync();
        result.Should().Be(0);
    }

    [Fact]
    public async Task GetNextSortOrderAsync_WithBookmarks_ReturnsMaxPlusOne()
    {
        await _sut.AddAsync(Bookmark.Create("A", "https://a.com", 5));
        await _sut.AddAsync(Bookmark.Create("B", "https://b.com", 2));
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetNextSortOrderAsync();
        result.Should().Be(6);
    }

    #endregion

    #region SeedDefaultsAsync

    [Fact]
    public async Task SeedDefaultsAsync_EmptyTable_SeedsThreeDefaults()
    {
        await _sut.SeedDefaultsAsync();
        await DbContext.SaveChangesAsync();

        var all = await _sut.GetAllAsync();
        all.Should().HaveCount(3);
        all.Select(b => b.Name).Should().Contain("Maclean's");
    }

    [Fact]
    public async Task SeedDefaultsAsync_NonEmptyTable_DoesNotSeed()
    {
        await _sut.AddAsync(Bookmark.Create("Existing", "https://existing.com", 0));
        await DbContext.SaveChangesAsync();

        await _sut.SeedDefaultsAsync();
        await DbContext.SaveChangesAsync();

        var all = await _sut.GetAllAsync();
        all.Should().HaveCount(1);
    }

    #endregion
}
