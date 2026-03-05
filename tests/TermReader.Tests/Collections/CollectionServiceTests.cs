// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Domain.Entities.Collections;
using TermReader.Infrastructure.Collections;
using TermReader.Persistence;
using TermReader.Persistence.Repositories;
using Xunit;

namespace TermReader.Tests.Collections;

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
        collections[0].Name.Should().Be("Read Later");
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
        collection.Name.Should().Be("Read Later");
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
