// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Domain.Entities.Collections;
using Xunit;

namespace TermReader.Tests.Collections;

/// <summary>
/// Edge case tests for Collection and CollectionItem domain entities.
/// Covers scenarios not handled by CollectionEntityTests:
/// duplicate detection, sort order after operations, non-existent IDs, etc.
/// </summary>
[Trait("Category", "Unit")]
public class CollectionEdgeCaseTests
{
    #region AddItem sort order / position

    [Fact]
    public void AddItem_MultipleItems_AssignsIncrementalPositions()
    {
        // Arrange
        var collection = Collection.Create("Test");

        // Act
        var item1 = collection.AddItem("https://example.com/1", "First");
        var item2 = collection.AddItem("https://example.com/2", "Second");
        var item3 = collection.AddItem("https://example.com/3", "Third");

        // Assert - items should be in insertion order
        collection.Items[0].Id.Should().Be(item1.Id);
        collection.Items[1].Id.Should().Be(item2.Id);
        collection.Items[2].Id.Should().Be(item3.Id);
    }

    #endregion

    #region ContainsUrl edge cases

    [Fact]
    public void ContainsUrl_WithTrailingSlash_DoesNotMatchWithout()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com/path/", "With Slash");

        // Act & Assert - URLs are compared as-is (except case)
        collection.ContainsUrl("https://example.com/path/").Should().BeTrue();
        collection.ContainsUrl("https://example.com/path").Should().BeFalse();
    }

    [Fact]
    public void ContainsUrl_WithQueryString_MatchesCaseInsensitively()
    {
        // Arrange
        var collection = Collection.Create("Test");
        collection.AddItem("https://example.com/page?id=123", "With Query");

        // Act & Assert
        collection.ContainsUrl("https://Example.COM/page?id=123").Should().BeTrue();
    }

    #endregion

    #region MoveItemUp / MoveItemDown with non-existent IDs

    [Fact]
    public void MoveItemUp_WithNonExistentId_DoesNothing()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var item1 = collection.AddItem("https://example.com/1", "First");
        var item2 = collection.AddItem("https://example.com/2", "Second");

        // Act
        collection.MoveItemUp(Guid.NewGuid());

        // Assert - order unchanged
        collection.Items[0].Id.Should().Be(item1.Id);
        collection.Items[1].Id.Should().Be(item2.Id);
    }

    [Fact]
    public void MoveItemDown_WithNonExistentId_DoesNothing()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var item1 = collection.AddItem("https://example.com/1", "First");
        var item2 = collection.AddItem("https://example.com/2", "Second");

        // Act
        collection.MoveItemDown(Guid.NewGuid());

        // Assert - order unchanged
        collection.Items[0].Id.Should().Be(item1.Id);
        collection.Items[1].Id.Should().Be(item2.Id);
    }

    [Fact]
    public void MoveItemUp_WithSingleItem_DoesNothing()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var item = collection.AddItem("https://example.com", "Only");

        // Act
        collection.MoveItemUp(item.Id);

        // Assert
        collection.Items.Should().HaveCount(1);
        collection.Items[0].Id.Should().Be(item.Id);
    }

    [Fact]
    public void MoveItemDown_WithSingleItem_DoesNothing()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var item = collection.AddItem("https://example.com", "Only");

        // Act
        collection.MoveItemDown(item.Id);

        // Assert
        collection.Items.Should().HaveCount(1);
        collection.Items[0].Id.Should().Be(item.Id);
    }

    #endregion

    #region RemoveItem edge cases

    [Fact]
    public void RemoveItem_FromEmptyCollection_DoesNothing()
    {
        // Arrange
        var collection = Collection.Create("Test");

        // Act
        collection.RemoveItem(Guid.NewGuid());

        // Assert
        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public void RemoveItem_TwiceWithSameId_OnlyRemovesOnce()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var item = collection.AddItem("https://example.com", "Example");

        // Act
        collection.RemoveItem(item.Id);
        collection.RemoveItem(item.Id);

        // Assert
        collection.Items.Should().BeEmpty();
    }

    #endregion

    #region Clear edge cases

    [Fact]
    public void Clear_OnEmptyCollection_DoesNotThrow()
    {
        // Arrange
        var collection = Collection.Create("Test");

        // Act
        var act = () => collection.Clear();

        // Assert
        act.Should().NotThrow();
        collection.Items.Should().BeEmpty();
    }

    #endregion

    #region Collection.Create edge cases

    [Fact]
    public void Create_WithVeryLongName_Succeeds()
    {
        // Arrange
        var longName = new string('a', 1000);

        // Act
        var collection = Collection.Create(longName);

        // Assert
        collection.Name.Should().Be(longName);
    }

    [Fact]
    public void Create_WithUnicodeCharacters_Succeeds()
    {
        // Act
        var collection = Collection.Create("Favoris");

        // Assert
        collection.Name.Should().Be("Favoris");
    }

    #endregion

    #region AddItem edge cases

    [Fact]
    public void AddItem_WithVeryLongUrl_Succeeds()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var longUrl = "https://example.com/" + new string('a', 2000);

        // Act
        var item = collection.AddItem(longUrl, "Long URL");

        // Assert
        item.Url.Should().Be(longUrl);
    }

    [Fact]
    public void AddItem_WithVeryLongTitle_Succeeds()
    {
        // Arrange
        var collection = Collection.Create("Test");
        var longTitle = new string('t', 2000);

        // Act
        var item = collection.AddItem("https://example.com", longTitle);

        // Assert
        item.Title.Should().Be(longTitle);
    }

    [Fact]
    public void AddItem_DuplicateUrls_AllowedByEntity()
    {
        // Note: Duplicate detection is done at the service level, not entity level.
        // The entity itself allows duplicate URLs.
        var collection = Collection.Create("Test");

        // Act
        collection.AddItem("https://example.com", "First");
        collection.AddItem("https://example.com", "Second");

        // Assert
        collection.Items.Should().HaveCount(2);
    }

    #endregion

    #region SortOrder

    [Fact]
    public void Create_DefaultSortOrder_IsZero()
    {
        var collection = Collection.Create("Test");
        collection.SortOrder.Should().Be(0);
    }

    [Fact]
    public void Create_NegativeSortOrder_IsAllowed()
    {
        var collection = Collection.Create("Test", sortOrder: -5);
        collection.SortOrder.Should().Be(-5);
    }

    #endregion

    #region CollectionItem.Create edge cases

    [Fact]
    public void CollectionItem_Create_GeneratesUniqueIds()
    {
        var collectionId = Guid.NewGuid();
        var item1 = CollectionItem.Create(collectionId, "https://example.com/1", "First");
        var item2 = CollectionItem.Create(collectionId, "https://example.com/2", "Second");

        item1.Id.Should().NotBe(item2.Id);
    }

    [Fact]
    public void CollectionItem_Create_DefaultIsReadFalse()
    {
        var item = CollectionItem.Create(Guid.NewGuid(), "https://example.com", "Test");
        item.IsRead.Should().BeFalse();
    }

    [Fact]
    public void CollectionItem_Create_SavedAtIsRecentUtc()
    {
        var item = CollectionItem.Create(Guid.NewGuid(), "https://example.com", "Test");
        item.SavedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    #endregion
}
