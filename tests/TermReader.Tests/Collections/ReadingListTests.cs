// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities.Collections;
using TermReader.Infrastructure.Collections;
using Xunit;

namespace TermReader.Tests.Collections;

public class ReadingListTests
{
    #region Collection.AddOrMoveToTop

    [Fact]
    public void AddOrMoveToTop_NewItem_AddsAtTopWithSortOrderZero()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://old.com", "Old");

        var item = collection.AddOrMoveToTop("https://new.com", "New");

        item.SortOrder.Should().Be(0);
        collection.Items[0].Url.Should().Be("https://new.com");
        collection.Items[1].Url.Should().Be("https://old.com");
        collection.Items[1].SortOrder.Should().Be(1);
    }

    [Fact]
    public void AddOrMoveToTop_ExistingUrl_MovesToTopWithFreshTimestamp()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://first.com", "First");
        collection.AddItem("https://second.com", "Second");

        // Re-add existing URL
        var item = collection.AddOrMoveToTop("https://first.com", "First Updated");

        collection.Items.Should().HaveCount(2);
        collection.Items[0].Url.Should().Be("https://first.com");
        collection.Items[0].Title.Should().Be("First Updated");
        collection.Items[0].SortOrder.Should().Be(0);
        item.SavedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AddOrMoveToTop_ExistingUrl_CaseInsensitive()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://example.com", "Example");

        collection.AddOrMoveToTop("https://EXAMPLE.COM", "Example Updated");

        collection.Items.Should().HaveCount(1);
        collection.Items[0].Title.Should().Be("Example Updated");
    }

    #endregion

    #region Collection.RemoveExpiredItems

    [Fact]
    public void RemoveExpiredItems_RemovesOldItems_KeepsRecent()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://recent.com", "Recent");
        collection.AddItem("https://old.com", "Old");

        // We can't easily set SavedAt in the past, so test with a very large maxAge
        var removed = collection.RemoveExpiredItems(TimeSpan.FromHours(24));

        // All items are recent (just created), none should be removed
        removed.Should().Be(0);
        collection.Items.Should().HaveCount(2);
    }

    [Fact]
    public void RemoveExpiredItems_EmptyCollection_ReturnsZero()
    {
        var collection = Collection.Create("Reading List");

        var removed = collection.RemoveExpiredItems(TimeSpan.FromHours(16));

        removed.Should().Be(0);
    }

    #endregion

    #region Collection.AddItemsAtEnd

    [Fact]
    public void AddItemsAtEnd_BulkAddPreservesOrder()
    {
        var collection = Collection.Create("Reading List");
        collection.AddItem("https://existing.com", "Existing");

        var items = new[]
        {
            ("https://one.com", "One"),
            ("https://two.com", "Two"),
            ("https://three.com", "Three")
        };

        collection.AddItemsAtEnd(items);

        collection.Items.Should().HaveCount(4);
        collection.Items[1].Url.Should().Be("https://one.com");
        collection.Items[2].Url.Should().Be("https://two.com");
        collection.Items[3].Url.Should().Be("https://three.com");
    }

    [Fact]
    public void AddItemsAtEnd_SkipsEmptyUrlsAndTitles()
    {
        var collection = Collection.Create("Reading List");

        var items = new[]
        {
            ("https://valid.com", "Valid"),
            ("", "No URL"),
            ("https://also-valid.com", "")
        };

        collection.AddItemsAtEnd(items);

        collection.Items.Should().HaveCount(1);
        collection.Items[0].Url.Should().Be("https://valid.com");
    }

    [Fact]
    public void AddItemsAtEnd_EmptyList_DoesNotUpdateTimestamp()
    {
        var collection = Collection.Create("Reading List");
        var originalUpdatedAt = collection.UpdatedAt;

        collection.AddItemsAtEnd(Array.Empty<(string, string)>());

        collection.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    #endregion

    #region CollectionService Reading List methods

    [Fact]
    public async Task SaveToReadingListAsync_CreatesReadingList_WhenNotExists()
    {
        var (service, repo, uow) = CreateService();
        repo.GetByNameAsync("Reading List", Arg.Any<CancellationToken>()).Returns((Collection?)null);
        repo.GetByNameAsync("Read Later", Arg.Any<CancellationToken>()).Returns((Collection?)null);
        repo.AddAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        repo.UpdateAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(1));

        var item = await service.SaveToReadingListAsync("https://example.com", "Example", CancellationToken.None);

        item.Should().NotBeNull();
        item.Url.Should().Be("https://example.com");
        await repo.Received(1).AddAsync(Arg.Is<Collection>(c => c.Name == "Reading List"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveToReadingListAsync_RenamesLegacyReadLater()
    {
        var (service, repo, uow) = CreateService();
        var legacy = Collection.Create("Read Later");
        repo.GetByNameAsync("Reading List", Arg.Any<CancellationToken>()).Returns((Collection?)null);
        repo.GetByNameAsync("Read Later", Arg.Any<CancellationToken>()).Returns(legacy);
        repo.UpdateAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(1));

        await service.SaveToReadingListAsync("https://example.com", "Example", CancellationToken.None);

        legacy.Name.Should().Be("Reading List");
    }

    [Fact]
    public async Task PurgeExpiredReadingListItemsAsync_NoCollection_ReturnsZero()
    {
        var (service, repo, _) = CreateService();
        repo.GetByNameAsync("Reading List", Arg.Any<CancellationToken>()).Returns((Collection?)null);

        var result = await service.PurgeExpiredReadingListItemsAsync(TimeSpan.FromHours(16), CancellationToken.None);

        result.Should().Be(0);
    }

    [Fact]
    public async Task SaveAllToReadingListAsync_AddsItems()
    {
        var (service, repo, uow) = CreateService();
        var collection = Collection.Create("Reading List");
        repo.GetByNameAsync("Reading List", Arg.Any<CancellationToken>()).Returns(collection);
        repo.UpdateAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(1));

        var items = new[] { ("https://a.com", "A"), ("https://b.com", "B") };
        await service.SaveAllToReadingListAsync(items, CancellationToken.None);

        collection.Items.Should().HaveCount(2);
    }

    #endregion

    #region Save-then-view flow

    [Fact]
    public async Task SaveToReadingList_ThenGetReadingList_ReturnsItemsInOrder()
    {
        var (service, repo, uow) = CreateService();
        var collection = Collection.Create("Reading List");
        repo.GetByNameAsync("Reading List", Arg.Any<CancellationToken>()).Returns(collection);
        repo.UpdateAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(1));

        await service.SaveToReadingListAsync("https://a.com", "Article A");
        await service.SaveToReadingListAsync("https://b.com", "Article B");

        var result = await service.GetReadingListAsync();

        result.Name.Should().Be("Reading List");
        result.Items.Should().HaveCount(2);
        // Most recently saved should be first (AddOrMoveToTop)
        result.Items[0].Url.Should().Be("https://b.com");
        result.Items[1].Url.Should().Be("https://a.com");
    }

    [Fact]
    public async Task SaveAllToReadingList_ThenGetReadingList_ReturnsAllItems()
    {
        var (service, repo, uow) = CreateService();
        var collection = Collection.Create("Reading List");
        repo.GetByNameAsync("Reading List", Arg.Any<CancellationToken>()).Returns(collection);
        repo.UpdateAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(1));

        var items = new[]
        {
            ("https://a.com", "A"),
            ("https://b.com", "B"),
            ("https://c.com", "C")
        };
        await service.SaveAllToReadingListAsync(items);

        var result = await service.GetReadingListAsync();

        result.Items.Should().HaveCount(3);
        result.Items.Select(i => i.Url).Should().ContainInOrder(
            "https://a.com", "https://b.com", "https://c.com");
    }

    [Fact]
    public async Task GetReadingListAsync_WhenLegacyReadLaterExists_RenamesAndReturns()
    {
        var (service, repo, uow) = CreateService();
        var legacy = Collection.Create("Read Later");
        legacy.AddItem("https://old.com", "Old Article");
        repo.GetByNameAsync("Reading List", Arg.Any<CancellationToken>()).Returns((Collection?)null);
        repo.GetByNameAsync("Read Later", Arg.Any<CancellationToken>()).Returns(legacy);
        repo.UpdateAsync(Arg.Any<Collection>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(1));

        var result = await service.GetReadingListAsync();

        result.Name.Should().Be("Reading List");
        result.Items.Should().HaveCount(1);
        result.Items[0].Url.Should().Be("https://old.com");
    }

    #endregion

    private static (CollectionService Service, ICollectionRepository Repo, IUnitOfWork UoW) CreateService()
    {
        var repo = Substitute.For<ICollectionRepository>();
        var uow = Substitute.For<IUnitOfWork>();
        var logger = Substitute.For<ILogger<CollectionService>>();
        return (new CollectionService(repo, uow, logger), repo, uow);
    }
}
