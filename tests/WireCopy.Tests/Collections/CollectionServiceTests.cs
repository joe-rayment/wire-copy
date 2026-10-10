// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Infrastructure.Collections;
using WireCopy.Persistence;
using WireCopy.Persistence.Repositories;
using Xunit;

namespace WireCopy.Tests.Collections;

[Trait("Category", "Unit")]
public class CollectionServiceTests : TestDatabaseFixture
{
    private readonly CollectionRepository _repository;
    private readonly CollectionService _sut;
    private readonly ILogger<CollectionService> _logger;

    public CollectionServiceTests()
    {
        var preferences = new InMemoryCollectionPreferences();
        _repository = new CollectionRepository(DbContext, preferences);
        var unitOfWork = new UnitOfWork(DbContext, Substitute.For<ILogger<UnitOfWork>>());
        _logger = Substitute.For<ILogger<CollectionService>>();
        _sut = new CollectionService(_repository, unitOfWork, _logger);
    }

    #region SaveToDefaultCollectionAsync

    [Fact]
    public async Task SaveToDefaultCollectionAsync_CreatesDefaultCollectionAndAddsItem()
    {
        // Act
        var item = await _sut.SaveToDefaultCollectionAsync("https://example.com", "Example");

        // Assert
        item.Should().NotBeNull();
        item!.Url.Should().Be("https://example.com");
        item.Title.Should().Be("Example");

        var collections = await _sut.GetAllCollectionsAsync();
        collections.Should().HaveCount(1);
        collections[0].Name.Should().Be("Reading List");
        collections[0].Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveToDefaultCollectionAsync_ReuseExistingDefaultCollection()
    {
        // Arrange
        await _sut.SaveToDefaultCollectionAsync("https://example.com/1", "First");

        // Act
        await _sut.SaveToDefaultCollectionAsync("https://example.com/2", "Second");

        // Assert
        var collections = await _sut.GetAllCollectionsAsync();
        collections.Should().HaveCount(1);
        collections[0].Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveToDefaultCollectionAsync_SkipsDuplicateUrlSilently()
    {
        // Arrange
        await _sut.SaveToDefaultCollectionAsync("https://example.com", "Example");

        // Act
        var result = await _sut.SaveToDefaultCollectionAsync("https://example.com", "Example Again");

        // Assert
        result.Should().BeNull();

        var collections = await _sut.GetAllCollectionsAsync();
        collections[0].Items.Should().HaveCount(1);
    }

    #endregion

    #region SaveToCollectionByNameAsync

    [Fact]
    public async Task SaveToCollectionByNameAsync_CreatesCollectionIfNotExists()
    {
        // Act
        var item = await _sut.SaveToCollectionByNameAsync("Favorites", "https://example.com", "Example");

        // Assert
        item.Should().NotBeNull();
        item!.Url.Should().Be("https://example.com");

        var collections = await _sut.GetAllCollectionsAsync();
        collections.Should().ContainSingle(c => c.Name == "Favorites");
    }

    [Fact]
    public async Task SaveToCollectionByNameAsync_ReusesExistingCollection()
    {
        // Arrange
        await _sut.SaveToCollectionByNameAsync("Favorites", "https://example.com/1", "First");

        // Act
        await _sut.SaveToCollectionByNameAsync("Favorites", "https://example.com/2", "Second");

        // Assert
        var collections = await _sut.GetAllCollectionsAsync();
        var favorites = collections.Should().ContainSingle(c => c.Name == "Favorites").Subject;
        favorites.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveToCollectionByNameAsync_NameWithTrailingSpace_ReusesExistingCollection()
    {
        // Guards:   typing "Favs " (stray space) at the save-to-collection prompt files the
        //           link into the existing "Favs" instead of creating a second "Favs".
        // Fails if: the service looks up the collection by the untrimmed name.
        // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~SaveToCollectionByNameAsync_NameWithTrailingSpace"
        await _sut.SaveToCollectionByNameAsync("Favs", "https://example.com/1", "First");

        await _sut.SaveToCollectionByNameAsync("Favs ", "https://example.com/2", "Second");

        var collections = await _sut.GetAllCollectionsAsync();
        var favs = collections.Should().ContainSingle(c => c.Name == "Favs").Subject;
        favs.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveToCollectionByNameAsync_SkipsDuplicateUrlSilently()
    {
        // Arrange
        await _sut.SaveToCollectionByNameAsync("Favorites", "https://example.com", "Example");

        // Act
        var result = await _sut.SaveToCollectionByNameAsync("Favorites", "https://example.com", "Example Again");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetAllCollectionsAsync

    [Fact]
    public async Task GetAllCollectionsAsync_ReturnsAllCollections()
    {
        // Arrange
        await _sut.CreateCollectionAsync("Alpha");
        await _sut.CreateCollectionAsync("Beta");
        await _sut.CreateCollectionAsync("Gamma");

        // Act
        var collections = await _sut.GetAllCollectionsAsync();

        // Assert
        collections.Should().HaveCount(3);
        collections.Select(c => c.Name).Should().Contain(new[] { "Alpha", "Beta", "Gamma" });
    }

    [Fact]
    public async Task GetAllCollectionsAsync_ReturnsEmptyWhenNoCollections()
    {
        // Act
        var collections = await _sut.GetAllCollectionsAsync();

        // Assert
        collections.Should().BeEmpty();
    }

    #endregion

    #region CreateCollectionAsync

    [Fact]
    public async Task CreateCollectionAsync_CreatesNewCollection()
    {
        // Act
        var collection = await _sut.CreateCollectionAsync("My Collection");

        // Assert
        collection.Should().NotBeNull();
        collection.Id.Should().NotBe(Guid.Empty);
        collection.Name.Should().Be("My Collection");
        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateCollectionAsync_ThrowsWhenDuplicateName()
    {
        // Arrange
        await _sut.CreateCollectionAsync("My Collection");

        // Act
        var act = () => _sut.CreateCollectionAsync("My Collection");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task CreateCollectionAsync_NameWithTrailingSpace_ThrowsWhenDuplicate()
    {
        // Guards:   creating "Favs " when "Favs" exists is refused as a duplicate.
        // Fails if: the duplicate check uses the untrimmed name and lets a second "Favs" in.
        // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~CreateCollectionAsync_NameWithTrailingSpace"
        await _sut.CreateCollectionAsync("Favs");

        var act = () => _sut.CreateCollectionAsync("Favs ");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    #endregion

    #region DeleteCollectionAsync

    [Fact]
    public async Task DeleteCollectionAsync_RemovesCollectionAndItems()
    {
        // Arrange
        var collection = await _sut.CreateCollectionAsync("ToDelete");
        await _sut.SaveToCollectionByNameAsync("ToDelete", "https://example.com", "Example");

        // Act
        await _sut.DeleteCollectionAsync(collection.Id);

        // Assert
        var collections = await _sut.GetAllCollectionsAsync();
        collections.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteCollectionAsync_ThrowsWhenCollectionNotFound()
    {
        // Act
        var act = () => _sut.DeleteCollectionAsync(Guid.NewGuid());

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    #endregion

    #region RemoveItemAsync

    [Fact]
    public async Task RemoveItemAsync_RemovesItemFromCollection()
    {
        // Arrange
        var item = await _sut.SaveToDefaultCollectionAsync("https://example.com", "Example");
        var defaultCollection = await _sut.GetDefaultCollectionAsync();

        // Act
        await _sut.RemoveItemAsync(defaultCollection.Id, item!.Id);

        // Assert
        var updated = await _sut.GetDefaultCollectionAsync();
        updated.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveItemAsync_ThrowsWhenCollectionNotFound()
    {
        // Act
        var act = () => _sut.RemoveItemAsync(Guid.NewGuid(), Guid.NewGuid());

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    #endregion

    #region MoveItemUpAsync / MoveItemDownAsync

    [Fact]
    public async Task MoveItemUpAsync_ReordersItems()
    {
        // Arrange
        var item1 = await _sut.SaveToDefaultCollectionAsync("https://example.com/1", "First");
        var item2 = await _sut.SaveToDefaultCollectionAsync("https://example.com/2", "Second");
        var defaultCollection = await _sut.GetDefaultCollectionAsync();

        // Act
        await _sut.MoveItemUpAsync(defaultCollection.Id, item2!.Id);

        // Assert
        var updated = await _sut.GetDefaultCollectionAsync();
        updated.Items[0].Url.Should().Be("https://example.com/2");
        updated.Items[1].Url.Should().Be("https://example.com/1");
    }

    [Fact]
    public async Task MoveItemDownAsync_ReordersItems()
    {
        // Arrange
        var item1 = await _sut.SaveToDefaultCollectionAsync("https://example.com/1", "First");
        var item2 = await _sut.SaveToDefaultCollectionAsync("https://example.com/2", "Second");
        var defaultCollection = await _sut.GetDefaultCollectionAsync();

        // Act
        await _sut.MoveItemDownAsync(defaultCollection.Id, item1!.Id);

        // Assert
        var updated = await _sut.GetDefaultCollectionAsync();
        updated.Items[0].Url.Should().Be("https://example.com/2");
        updated.Items[1].Url.Should().Be("https://example.com/1");
    }

    [Fact]
    public async Task MoveItemUpAsync_OnFirstItem_DoesNotChangeOrder()
    {
        // Arrange
        var item1 = await _sut.SaveToDefaultCollectionAsync("https://example.com/1", "First");
        var item2 = await _sut.SaveToDefaultCollectionAsync("https://example.com/2", "Second");
        var defaultCollection = await _sut.GetDefaultCollectionAsync();

        // Act
        await _sut.MoveItemUpAsync(defaultCollection.Id, item1!.Id);

        // Assert
        var updated = await _sut.GetDefaultCollectionAsync();
        updated.Items[0].Url.Should().Be("https://example.com/1");
        updated.Items[1].Url.Should().Be("https://example.com/2");
    }

    [Fact]
    public async Task MoveItemDownAsync_OnLastItem_DoesNotChangeOrder()
    {
        // Arrange
        var item1 = await _sut.SaveToDefaultCollectionAsync("https://example.com/1", "First");
        var item2 = await _sut.SaveToDefaultCollectionAsync("https://example.com/2", "Second");
        var defaultCollection = await _sut.GetDefaultCollectionAsync();

        // Act
        await _sut.MoveItemDownAsync(defaultCollection.Id, item2!.Id);

        // Assert
        var updated = await _sut.GetDefaultCollectionAsync();
        updated.Items[0].Url.Should().Be("https://example.com/1");
        updated.Items[1].Url.Should().Be("https://example.com/2");
    }

    #endregion

    #region GetDefaultCollectionAsync

    [Fact]
    public async Task GetDefaultCollectionAsync_CreatesReadLaterCollectionWhenNoneExists()
    {
        // Act
        var collection = await _sut.GetDefaultCollectionAsync();

        // Assert
        collection.Should().NotBeNull();
        collection.Name.Should().Be("Reading List");
    }

    [Fact]
    public async Task GetDefaultCollectionAsync_ReturnsExistingDefaultCollection()
    {
        // Arrange
        var firstCall = await _sut.GetDefaultCollectionAsync();

        // Act
        var secondCall = await _sut.GetDefaultCollectionAsync();

        // Assert
        secondCall.Id.Should().Be(firstCall.Id);
    }

    [Fact]
    public async Task GetDefaultCollectionAsync_ReturnsLastUsedCollectionWhenSet()
    {
        // Arrange
        var custom = await _sut.CreateCollectionAsync("Custom Default");
        await _sut.SetDefaultCollectionAsync(custom.Id);

        // Act
        var defaultCollection = await _sut.GetDefaultCollectionAsync();

        // Assert
        defaultCollection.Id.Should().Be(custom.Id);
        defaultCollection.Name.Should().Be("Custom Default");
    }

    #endregion

    #region Save-then-view Reading List (integration)

    [Fact]
    public async Task SaveToReadingList_ThenGetReadingList_ReturnsCollectionWithItems()
    {
        await _sut.SaveToReadingListAsync("https://example.com/article", "My Article");

        var readingList = await _sut.GetReadingListAsync();

        readingList.Should().NotBeNull();
        readingList.Name.Should().Be("Reading List");
        readingList.Items.Should().HaveCount(1);
        readingList.Items[0].Url.Should().Be("https://example.com/article");
    }

    [Fact]
    public async Task SaveAllToReadingList_ThenGetReadingList_ContainsAllItems()
    {
        var items = new[]
        {
            ("https://example.com/1", "First"),
            ("https://example.com/2", "Second"),
            ("https://example.com/3", "Third")
        };
        await _sut.SaveAllToReadingListAsync(items);

        var readingList = await _sut.GetReadingListAsync();

        readingList.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task SaveAllToReadingListAsync_SavedTwice_DoesNotDuplicateAndReturnsAddedCount()
    {
        // Guards:   pressing "save all" twice leaves one copy of each link, and the service
        //           returns how many links were actually added each time (3, then 0, then 1).
        // Fails if: SaveAllToReadingListAsync appends again or returns the batch size.
        // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~SaveAllToReadingListAsync_SavedTwice"
        var items = new[]
        {
            ("https://example.com/1", "First"),
            ("https://example.com/2", "Second"),
            ("https://example.com/3", "Third")
        };

        var first = await _sut.SaveAllToReadingListAsync(items);
        var second = await _sut.SaveAllToReadingListAsync(items);
        var third = await _sut.SaveAllToReadingListAsync(items.Append(("https://example.com/4", "Fourth")));

        first.Should().Be(3);
        second.Should().Be(0);
        third.Should().Be(1);
        var readingList = await _sut.GetReadingListAsync();
        readingList.Items.Select(i => i.Url).Should().Equal(
            "https://example.com/1", "https://example.com/2",
            "https://example.com/3", "https://example.com/4");
    }

    [Fact]
    public async Task GetReadingListAsync_SameCollectionAsGetDefaultCollectionAsync()
    {
        await _sut.SaveToReadingListAsync("https://example.com", "Test");

        var readingList = await _sut.GetReadingListAsync();
        var defaultCollection = await _sut.GetDefaultCollectionAsync();

        readingList.Id.Should().Be(defaultCollection.Id);
    }

    #endregion

    #region RenameCollectionAsync

    [Fact]
    public async Task RenameCollectionAsync_ChangesCollectionName()
    {
        // Arrange
        var collection = await _sut.CreateCollectionAsync("Old Name");

        // Act
        await _sut.RenameCollectionAsync(collection.Id, "New Name");

        // Assert
        var collections = await _sut.GetAllCollectionsAsync();
        collections.Should().ContainSingle(c => c.Name == "New Name");
    }

    [Fact]
    public async Task RenameCollectionAsync_ThrowsWhenCollectionNotFound()
    {
        // Act
        var act = () => _sut.RenameCollectionAsync(Guid.NewGuid(), "New Name");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    #endregion

    #region ClearCollectionAsync

    [Fact]
    public async Task ClearCollectionAsync_RemovesAllItemsFromCollection()
    {
        // Arrange
        await _sut.SaveToDefaultCollectionAsync("https://example.com/1", "First");
        await _sut.SaveToDefaultCollectionAsync("https://example.com/2", "Second");
        var defaultCollection = await _sut.GetDefaultCollectionAsync();

        // Act
        await _sut.ClearCollectionAsync(defaultCollection.Id);

        // Assert
        var updated = await _sut.GetDefaultCollectionAsync();
        updated.Items.Should().BeEmpty();
    }

    #endregion

    #region MarkItemAsReadAsync

    [Fact]
    public async Task MarkItemAsReadAsync_SetsItemIsReadToTrue()
    {
        // Arrange
        var item = await _sut.SaveToDefaultCollectionAsync("https://example.com", "Example");
        var defaultCollection = await _sut.GetDefaultCollectionAsync();

        // Act
        await _sut.MarkItemAsReadAsync(defaultCollection.Id, item!.Id);

        // Assert
        var updated = await _sut.GetDefaultCollectionAsync();
        updated.Items[0].IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task MarkItemAsReadAsync_ThrowsWhenItemNotFound()
    {
        // Arrange
        var defaultCollection = await _sut.GetDefaultCollectionAsync();

        // Act
        var act = () => _sut.MarkItemAsReadAsync(defaultCollection.Id, Guid.NewGuid());

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    #endregion
}
