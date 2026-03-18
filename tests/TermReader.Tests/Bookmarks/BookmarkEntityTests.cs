// Educational and personal use only.

using FluentAssertions;
using TermReader.Domain.Entities.Bookmarks;
using Xunit;

namespace TermReader.Tests.Bookmarks;

[Trait("Category", "Unit")]
public class BookmarkEntityTests
{
    #region Create

    [Fact]
    public void Create_WithValidNameAndUrl_SetsPropertiesAndGeneratesId()
    {
        var bookmark = Bookmark.Create("Hacker News", "https://news.ycombinator.com");

        bookmark.Id.Should().NotBe(Guid.Empty);
        bookmark.Name.Should().Be("Hacker News");
        bookmark.Url.Should().Be("https://news.ycombinator.com");
        bookmark.SortOrder.Should().Be(0);
        bookmark.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Create_WithSortOrder_SetsSortOrder()
    {
        var bookmark = Bookmark.Create("Test", "https://test.com", sortOrder: 5);

        bookmark.SortOrder.Should().Be(5);
    }

    [Fact]
    public void Create_TrimsNameAndUrl()
    {
        var bookmark = Bookmark.Create("  Padded Name  ", "  https://padded.com  ");

        bookmark.Name.Should().Be("Padded Name");
        bookmark.Url.Should().Be("https://padded.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithInvalidName_ThrowsArgumentException(string? name)
    {
        var act = () => Bookmark.Create(name!, "https://test.com");

        act.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithInvalidUrl_ThrowsArgumentException(string? url)
    {
        var act = () => Bookmark.Create("Test", url!);

        act.Should().Throw<ArgumentException>().WithParameterName("url");
    }

    #endregion

    #region Rename

    [Fact]
    public void Rename_WithValidName_UpdatesName()
    {
        var bookmark = Bookmark.Create("Old Name", "https://test.com");

        bookmark.Rename("New Name");

        bookmark.Name.Should().Be("New Name");
    }

    [Fact]
    public void Rename_TrimsName()
    {
        var bookmark = Bookmark.Create("Old", "https://test.com");

        bookmark.Rename("  New  ");

        bookmark.Name.Should().Be("New");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_WithInvalidName_ThrowsArgumentException(string? newName)
    {
        var bookmark = Bookmark.Create("Test", "https://test.com");

        var act = () => bookmark.Rename(newName!);

        act.Should().Throw<ArgumentException>().WithParameterName("newName");
    }

    #endregion

    #region UpdateUrl

    [Fact]
    public void UpdateUrl_WithValidUrl_UpdatesUrl()
    {
        var bookmark = Bookmark.Create("Test", "https://old.com");

        bookmark.UpdateUrl("https://new.com");

        bookmark.Url.Should().Be("https://new.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateUrl_WithInvalidUrl_ThrowsArgumentException(string? newUrl)
    {
        var bookmark = Bookmark.Create("Test", "https://test.com");

        var act = () => bookmark.UpdateUrl(newUrl!);

        act.Should().Throw<ArgumentException>().WithParameterName("newUrl");
    }

    #endregion

    #region SetSortOrder

    [Fact]
    public void SetSortOrder_UpdatesOrder()
    {
        var bookmark = Bookmark.Create("Test", "https://test.com");

        bookmark.SetSortOrder(42);

        bookmark.SortOrder.Should().Be(42);
    }

    #endregion

    #region Unique IDs

    [Fact]
    public void Create_GeneratesUniqueIds()
    {
        var b1 = Bookmark.Create("One", "https://one.com");
        var b2 = Bookmark.Create("Two", "https://two.com");

        b1.Id.Should().NotBe(b2.Id);
    }

    #endregion
}
