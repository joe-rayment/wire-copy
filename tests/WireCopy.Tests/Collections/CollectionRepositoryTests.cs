// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Persistence.Repositories;
using Xunit;

namespace WireCopy.Tests.Collections;

[Trait("Category", "Unit")]
public class CollectionRepositoryTests : TestDatabaseFixture
{
    private readonly CollectionRepository _sut;
    private readonly ICollectionPreferences _preferences;

    public CollectionRepositoryTests()
    {
        _preferences = Substitute.For<ICollectionPreferences>();
        _sut = new CollectionRepository(DbContext, _preferences);
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
        var c1 = Collection.Create("Zebra", sortOrder: 1);
        var c2 = Collection.Create("Alpha", sortOrder: 0);
        var c3 = Collection.Create("Beta", sortOrder: 0);
        await _sut.AddAsync(c1);
        await _sut.AddAsync(c2);
        await _sut.AddAsync(c3);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetAllAsync();

        result.Should().HaveCount(3);
        result[0].Name.Should().Be("Alpha");
        result[1].Name.Should().Be("Beta");
        result[2].Name.Should().Be("Zebra");
    }

    [Fact]
    public async Task GetAllAsync_IncludesItemsOrderedBySortOrder()
    {
        var collection = Collection.Create("Test");
        collection.AddItem("https://b.com", "B Item");
        collection.AddItem("https://a.com", "A Item");
        await _sut.AddAsync(collection);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetAllAsync();

        result.Should().HaveCount(1);
        result[0].Items.Should().HaveCount(2);
        result[0].Items[0].SortOrder.Should().BeLessThanOrEqualTo(result[0].Items[1].SortOrder);
    }

    #endregion

    #region GetByIdAsync

    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsCollection()
    {
        var collection = Collection.Create("Test");
        await _sut.AddAsync(collection);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetByIdAsync(collection.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test");
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_IncludesItems()
    {
        var collection = Collection.Create("WithItems");
        collection.AddItem("https://example.com", "Example");
        await _sut.AddAsync(collection);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetByIdAsync(collection.Id);

        result.Should().NotBeNull();
        result!.Items.Should().HaveCount(1);
        result.Items[0].Url.Should().Be("https://example.com");
    }

    #endregion

    #region GetByNameAsync

    [Fact]
    public async Task GetByNameAsync_ExistingName_ReturnsCollection()
    {
        var collection = Collection.Create("My Collection");
        await _sut.AddAsync(collection);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetByNameAsync("My Collection");

        result.Should().NotBeNull();
        result!.Name.Should().Be("My Collection");
    }

    [Fact]
    public async Task GetByNameAsync_CaseInsensitive()
    {
        var collection = Collection.Create("My Collection");
        await _sut.AddAsync(collection);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetByNameAsync("my collection");

        result.Should().NotBeNull();
        result!.Name.Should().Be("My Collection");
    }

    [Fact]
    public async Task GetByNameAsync_NonExistentName_ReturnsNull()
    {
        var result = await _sut.GetByNameAsync("NoSuchCollection");
        result.Should().BeNull();
    }

    #endregion

    #region GetOrCreateDefaultAsync

    [Fact]
    public async Task GetOrCreateDefaultAsync_NoDefault_CreatesOne()
    {
        var result = await _sut.GetOrCreateDefaultAsync();
        await DbContext.SaveChangesAsync();

        result.Should().NotBeNull();
        result.Name.Should().Be("Reading List");
    }

    [Fact]
    public async Task GetOrCreateDefaultAsync_DefaultExists_ReturnsExisting()
    {
        var existing = Collection.Create("Reading List", sortOrder: 0);
        await _sut.AddAsync(existing);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetOrCreateDefaultAsync();

        result.Should().NotBeNull();
        result.Id.Should().Be(existing.Id);
    }

    #endregion

    #region CountReadingListItemsAsync

    [Fact]
    public async Task CountReadingListItemsAsync_CountsRecentItemsInReadingList()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://a.com", "A");
        collection.AddItem("https://b.com", "B");
        await _sut.AddAsync(collection);
        await DbContext.SaveChangesAsync();

        var count = await _sut.CountReadingListItemsAsync(TimeSpan.FromHours(16));

        count.Should().Be(2);
    }

    [Fact]
    public async Task CountReadingListItemsAsync_ExcludesItemsOlderThanMaxAge()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://a.com", "A");
        await _sut.AddAsync(collection);
        await DbContext.SaveChangesAsync();

        // maxAge of zero => cutoff is "now"; items saved a moment ago are older
        // than the cutoff and are therefore excluded. This is the core fix for
        // workspace-hlzy: the card no longer counts items past their TTL.
        var count = await _sut.CountReadingListItemsAsync(TimeSpan.Zero);

        count.Should().Be(0);
    }

    [Fact]
    public async Task CountReadingListItemsAsync_IgnoresItemsInOtherCollections()
    {
        var readingList = Collection.Create("Reading List");
        readingList.AddItem("https://a.com", "A");
        var other = Collection.Create("Work");
        other.AddItem("https://b.com", "B");
        other.AddItem("https://c.com", "C");
        await _sut.AddAsync(readingList);
        await _sut.AddAsync(other);
        await DbContext.SaveChangesAsync();

        var count = await _sut.CountReadingListItemsAsync(TimeSpan.FromHours(16));

        count.Should().Be(1);
    }

    [Fact]
    public async Task CountReadingListItemsAsync_IncludesLegacyReadLater()
    {
        var legacy = Collection.Create("Read Later");
        legacy.AddItem("https://a.com", "A");
        await _sut.AddAsync(legacy);
        await DbContext.SaveChangesAsync();

        var count = await _sut.CountReadingListItemsAsync(TimeSpan.FromHours(16));

        count.Should().Be(1);
    }

    [Fact]
    public async Task CountReadingListItemsAsync_NoReadingList_ReturnsZero()
    {
        var count = await _sut.CountReadingListItemsAsync(TimeSpan.FromHours(16));

        count.Should().Be(0);
    }

    #endregion

    #region AddAsync and DeleteAsync

    [Fact]
    public async Task AddAsync_PersistsCollection()
    {
        var collection = Collection.Create("New");
        await _sut.AddAsync(collection);
        await DbContext.SaveChangesAsync();

        using var verifyContext = CreateDbContext();
        var repo = new CollectionRepository(verifyContext, _preferences);
        var result = await repo.GetByIdAsync(collection.Id);
        result.Should().NotBeNull();
        result!.Name.Should().Be("New");
    }

    [Fact]
    public async Task DeleteAsync_RemovesCollection()
    {
        var collection = Collection.Create("ToDelete");
        await _sut.AddAsync(collection);
        await DbContext.SaveChangesAsync();

        await _sut.DeleteAsync(collection);
        await DbContext.SaveChangesAsync();

        var result = await _sut.GetByIdAsync(collection.Id);
        result.Should().BeNull();
    }

    #endregion

    #region LastUsedCollectionId

    [Fact]
    public async Task GetLastUsedCollectionIdAsync_DelegatesToPreferences()
    {
        var expectedId = Guid.NewGuid();
        _preferences.LastUsedCollectionId.Returns(expectedId);

        var result = await _sut.GetLastUsedCollectionIdAsync();

        result.Should().Be(expectedId);
    }

    [Fact]
    public async Task SetLastUsedCollectionIdAsync_SetsOnPreferences()
    {
        var id = Guid.NewGuid();

        await _sut.SetLastUsedCollectionIdAsync(id);

        _preferences.LastUsedCollectionId.Should().Be(id);
    }

    #endregion
}
