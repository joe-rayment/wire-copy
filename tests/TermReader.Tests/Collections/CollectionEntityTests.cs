// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Domain.Entities.Collections;
using Xunit;

namespace TermReader.Tests.Collections;

[Trait("Category", "Unit")]
public class CollectionEntityTests
{
    #region Collection.Create

    [Fact]
    public void Create_WithValidName_SetsNameAndGeneratesId()
    {
        // Act
        var collection = Collection.Create("My Bookmarks");

        // Assert
        collection.Id.Should().NotBe(Guid.Empty);
        collection.Name.Should().Be("My Bookmarks");
        collection.Items.Should().BeEmpty();
        collection.SortOrder.Should().Be(0);
        collection.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        collection.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Create_WithSortOrder_SetsSortOrder()
    {
        // Act
        var collection = Collection.Create("Favorites", sortOrder: 5);

        // Assert
        collection.SortOrder.Should().Be(5);
    }

    [Fact]
    public void Create_TrimsName()
    {
        // Act
        var collection = Collection.Create("  Padded Name  ");

        // Assert
        collection.Name.Should().Be("Padded Name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_ThrowsArgumentException(string? name)
    {
        // Act
        var act = () => Collection.Create(name!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("name");
    }

    #endregion

    #region Collection.Rename

    [Fact]
    public void Rename_ChangesNameAndUpdatesTimestamp()
    {
        // Arrange
        var collection = Collection.Create("Original");
        var originalUpdatedAt = collection.UpdatedAt;

        // Allow a small delay so timestamps differ
        Thread.Sleep(10);

        // Act
        collection.Rename("Renamed");

        // Assert
        collection.Name.Should().Be("Renamed");
        collection.UpdatedAt.Should().BeOnOrAfter(originalUpdatedAt);
    }

    [Fact]
    public void Rename_TrimsNewName()
    {
        // Arrange
        var collection = Collection.Create("Original");

        // Act
        collection.Rename("  New Name  ");

        // Assert
        collection.Name.Should().Be("New Name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_WithBlankName_ThrowsArgumentException(string? newName)
    {
        // Arrange
        var collection = Collection.Create("Original");

        // Act
        var act = () => collection.Rename(newName!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("newName");
    }

    #endregion

    #region Collection.AddItem

    [Fact]
    public void AddItem_AddsItemAndUpdatesTimestamp()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var originalUpdatedAt = collection.UpdatedAt;

        Thread.Sleep(10);

        // Act
        var item = collection.AddItem("https://example.com", "Example");

        // Assert
        collection.Items.Should().HaveCount(1);
        collection.Items[0].Should().BeSameAs(item);
        item.Url.Should().Be("https://example.com");
        item.Title.Should().Be("Example");
        item.CollectionId.Should().Be(collection.Id);
        collection.UpdatedAt.Should().BeOnOrAfter(originalUpdatedAt);
    }

    [Fact]
    public void AddItem_ReturnsCreatedItem()
    {
        // Arrange
        var collection = Collection.Create("Test");

        // Act
        var item = collection.AddItem("https://example.com", "Example");

        // Assert
        item.Should().NotBeNull();
        item.Id.Should().NotBe(Guid.Empty);
        item.Url.Should().Be("https://example.com");
        item.Title.Should().Be("Example");
    }

    [Fact]
    public void AddItem_TrimsUrlAndTitle()
    {
        // Arrange
        var collection = Collection.Create("Test");

        // Act
        var item = collection.AddItem("  https://example.com  ", "  Example  ");

        // Assert
        item.Url.Should().Be("https://example.com");
        item.Title.Should().Be("Example");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddItem_WithBlankUrl_ThrowsArgumentException(string? url)
    {
        // Arrange
        var collection = Collection.Create("Test");

        // Act
        var act = () => collection.AddItem(url!, "Title");

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("url");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddItem_WithBlankTitle_ThrowsArgumentException(string? title)
    {
        // Arrange
        var collection = Collection.Create("Test");

        // Act
        var act = () => collection.AddItem("https://example.com", title!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("title");
    }

    #endregion

    #region Collection.RemoveItem

    [Fact]
    public void RemoveItem_RemovesExistingItemById()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var item = collection.AddItem("https://example.com", "Example");

        // Act
        collection.RemoveItem(item.Id);

        // Assert
        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public void RemoveItem_WithNonExistentId_DoesNothing()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com", "Example");
        var originalCount = collection.Items.Count;

        // Act
        collection.RemoveItem(Guid.NewGuid());

        // Assert
        collection.Items.Should().HaveCount(originalCount);
    }

    #endregion

    #region Collection.MoveItemUp

    [Fact]
    public void MoveItemUp_SwapsWithPreviousItem()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var item1 = collection.AddItem("https://example.com/1", "First");
        var item2 = collection.AddItem("https://example.com/2", "Second");
        var item3 = collection.AddItem("https://example.com/3", "Third");

        // Act
        collection.MoveItemUp(item2.Id);

        // Assert
        collection.Items[0].Id.Should().Be(item2.Id);
        collection.Items[1].Id.Should().Be(item1.Id);
        collection.Items[2].Id.Should().Be(item3.Id);
    }

    [Fact]
    public void MoveItemUp_OnFirstItem_DoesNothing()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var item1 = collection.AddItem("https://example.com/1", "First");
        var item2 = collection.AddItem("https://example.com/2", "Second");
        var originalUpdatedAt = collection.UpdatedAt;

        Thread.Sleep(10);

        // Act
        collection.MoveItemUp(item1.Id);

        // Assert
        collection.Items[0].Id.Should().Be(item1.Id);
        collection.Items[1].Id.Should().Be(item2.Id);
    }

    #endregion

    #region Collection.MoveItemDown

    [Fact]
    public void MoveItemDown_SwapsWithNextItem()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var item1 = collection.AddItem("https://example.com/1", "First");
        var item2 = collection.AddItem("https://example.com/2", "Second");
        var item3 = collection.AddItem("https://example.com/3", "Third");

        // Act
        collection.MoveItemDown(item2.Id);

        // Assert
        collection.Items[0].Id.Should().Be(item1.Id);
        collection.Items[1].Id.Should().Be(item3.Id);
        collection.Items[2].Id.Should().Be(item2.Id);
    }

    [Fact]
    public void MoveItemDown_OnLastItem_DoesNothing()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var item1 = collection.AddItem("https://example.com/1", "First");
        var item2 = collection.AddItem("https://example.com/2", "Second");

        // Act
        collection.MoveItemDown(item2.Id);

        // Assert
        collection.Items[0].Id.Should().Be(item1.Id);
        collection.Items[1].Id.Should().Be(item2.Id);
    }

    #endregion

    #region Collection.Clear

    [Fact]
    public void Clear_RemovesAllItems()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com/1", "First");
        collection.AddItem("https://example.com/2", "Second");
        collection.AddItem("https://example.com/3", "Third");

        // Act
        collection.Clear();

        // Assert
        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public void Clear_UpdatesTimestamp()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com/1", "First");
        var originalUpdatedAt = collection.UpdatedAt;

        Thread.Sleep(10);

        // Act
        collection.Clear();

        // Assert
        collection.UpdatedAt.Should().BeOnOrAfter(originalUpdatedAt);
    }

    #endregion

    #region Collection.ContainsUrl

    [Fact]
    public void ContainsUrl_ReturnsTrueForMatchingUrl_CaseInsensitive()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://Example.COM/Page", "Example");

        // Act & Assert
        collection.ContainsUrl("https://example.com/page").Should().BeTrue();
        collection.ContainsUrl("HTTPS://EXAMPLE.COM/PAGE").Should().BeTrue();
        collection.ContainsUrl("https://Example.COM/Page").Should().BeTrue();
    }

    [Fact]
    public void ContainsUrl_ReturnsFalseForNonMatchingUrl()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com", "Example");

        // Act & Assert
        collection.ContainsUrl("https://other.com").Should().BeFalse();
    }

    [Fact]
    public void ContainsUrl_ReturnsFalseForEmptyCollection()
    {
        // Arrange
        var collection = Collection.Create("Test");

        // Act & Assert
        collection.ContainsUrl("https://example.com").Should().BeFalse();
    }

    #endregion
}

[Trait("Category", "Unit")]
public class CollectionItemEntityTests
{
    #region CollectionItem.Create

    [Fact]
    public void Create_SetsPropertiesCorrectly()
    {
        // Arrange
        var collectionId = Guid.NewGuid();

        // Act
        var item = CollectionItem.Create(collectionId, "https://example.com", "Example Page");

        // Assert
        item.Id.Should().NotBe(Guid.Empty);
        item.CollectionId.Should().Be(collectionId);
        item.Url.Should().Be("https://example.com");
        item.Title.Should().Be("Example Page");
        item.SavedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        item.IsRead.Should().BeFalse();
    }

    [Fact]
    public void Create_WithEmptyCollectionId_ThrowsArgumentException()
    {
        // Act
        var act = () => CollectionItem.Create(Guid.Empty, "https://example.com", "Title");

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("collectionId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankUrl_ThrowsArgumentException(string? url)
    {
        // Act
        var act = () => CollectionItem.Create(Guid.NewGuid(), url!, "Title");

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("url");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankTitle_ThrowsArgumentException(string? title)
    {
        // Act
        var act = () => CollectionItem.Create(Guid.NewGuid(), "https://example.com", title!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("title");
    }

    #endregion

    #region CollectionItem.MarkAsRead

    [Fact]
    public void MarkAsRead_SetsIsReadToTrue()
    {
        // Arrange
        var item = CollectionItem.Create(Guid.NewGuid(), "https://example.com", "Example");
        item.IsRead.Should().BeFalse();

        // Act
        item.MarkAsRead();

        // Assert
        item.IsRead.Should().BeTrue();
    }

    [Fact]
    public void MarkAsRead_CalledTwice_RemainsTrue()
    {
        // Arrange
        var item = CollectionItem.Create(Guid.NewGuid(), "https://example.com", "Example");

        // Act
        item.MarkAsRead();
        item.MarkAsRead();

        // Assert
        item.IsRead.Should().BeTrue();
    }

    #endregion
}
