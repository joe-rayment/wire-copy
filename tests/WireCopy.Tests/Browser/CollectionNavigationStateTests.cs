// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Direct tests for CollectionNavigationState, covering the return-point
/// save/restore logic and edge cases not exercised through NavigationService.
/// </summary>
[Trait("Category", "Unit")]
public class CollectionNavigationStateTests
{
    private readonly CollectionNavigationState _sut;

    public CollectionNavigationStateTests()
    {
        var logger = Substitute.For<ILogger>();
        _sut = new CollectionNavigationState(logger);
    }

    #region TryRestoreReturnPoint tuple return

    [Fact]
    public void TryRestoreReturnPoint_ReturnsCorrectTupleValues()
    {
        var collection = Collection.Create("Test");
        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        _sut.EnterCollection(collection);
        _sut.CollectionItemSelectedIndex = 3;
        _sut.SaveCollectionReturnPoint();

        // Simulate leaving collection mode
        _sut.ExitCollections();

        var result = _sut.TryRestoreReturnPoint();

        result.Should().NotBeNull();
        result!.Value.Collection.Should().BeSameAs(collection);
        result.Value.ItemSelectedIndex.Should().Be(3);
    }

    [Fact]
    public void TryRestoreReturnPoint_RestoresInCollectionsMode()
    {
        var collection = Collection.Create("Test");
        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        _sut.EnterCollection(collection);
        _sut.SaveCollectionReturnPoint();
        _sut.ExitCollections();

        _sut.InCollectionsMode.Should().BeFalse();

        _sut.TryRestoreReturnPoint();

        _sut.InCollectionsMode.Should().BeTrue();
        _sut.ActiveCollection.Should().BeSameAs(collection);
    }

    [Fact]
    public void TryRestoreReturnPoint_ClearsAfterSingleUse()
    {
        var collection = Collection.Create("Test");
        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        _sut.EnterCollection(collection);
        _sut.SaveCollectionReturnPoint();
        _sut.ExitCollections();

        _sut.TryRestoreReturnPoint().Should().NotBeNull();
        _sut.TryRestoreReturnPoint().Should().BeNull();
    }

    #endregion

    #region SaveCollectionReturnPoint edge cases

    [Fact]
    public void SaveCollectionReturnPoint_OverwritesPreviousSave()
    {
        var collection1 = Collection.Create("First");
        var collection2 = Collection.Create("Second");

        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);

        // First save
        _sut.EnterCollection(collection1);
        _sut.CollectionItemSelectedIndex = 1;
        _sut.SaveCollectionReturnPoint();

        // Overwrite with second save
        _sut.ExitToCollectionList();
        _sut.EnterCollection(collection2);
        _sut.CollectionItemSelectedIndex = 5;
        _sut.SaveCollectionReturnPoint();

        _sut.ExitCollections();

        var result = _sut.TryRestoreReturnPoint();
        result.Should().NotBeNull();
        result!.Value.Collection.Name.Should().Be("Second");
        result.Value.ItemSelectedIndex.Should().Be(5);
    }

    [Fact]
    public void SaveCollectionReturnPoint_WithNoActiveCollection_DoesNotSave()
    {
        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        // No EnterCollection call

        _sut.SaveCollectionReturnPoint();

        _sut.TryRestoreReturnPoint().Should().BeNull();
    }

    #endregion

    #region EnterCollections / ExitCollections state

    [Fact]
    public void EnterCollections_SavesPreCollectionsState()
    {
        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Readable, 42);

        _sut.PreCollectionsViewMode.Should().Be(Domain.Enums.Browser.ViewMode.Readable);
        _sut.PreCollectionsScrollOffset.Should().Be(42);
        _sut.InCollectionsMode.Should().BeTrue();
        _sut.CollectionSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void ExitCollections_ClearsActiveCollectionAndMode()
    {
        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        _sut.EnterCollection(Collection.Create("Test"));

        _sut.ExitCollections();

        _sut.InCollectionsMode.Should().BeFalse();
        _sut.ActiveCollection.Should().BeNull();
    }

    [Fact]
    public void ExitToCollectionList_ClearsActiveCollectionOnly()
    {
        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        _sut.EnterCollection(Collection.Create("Test"));

        _sut.ExitToCollectionList();

        _sut.ActiveCollection.Should().BeNull();
        _sut.InCollectionsMode.Should().BeTrue();
    }

    #endregion

    #region Index clamping

    [Fact]
    public void CollectionSelectedIndex_ClampsNegativeToZero()
    {
        _sut.CollectionSelectedIndex = -5;
        _sut.CollectionSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void CollectionItemSelectedIndex_AllowsNegativeOneForCtaFocus()
    {
        _sut.CollectionItemSelectedIndex = -1;
        _sut.CollectionItemSelectedIndex.Should().Be(-1);
    }

    [Fact]
    public void CollectionItemSelectedIndex_ClampsBelowNegativeOneToNegativeOne()
    {
        _sut.CollectionItemSelectedIndex = -5;
        _sut.CollectionItemSelectedIndex.Should().Be(-1);
    }

    #endregion

    #region Scroll offset properties

    [Fact]
    public void CollectionListScrollOffset_DefaultsToZero()
    {
        _sut.CollectionListScrollOffset.Should().Be(0);
    }

    [Fact]
    public void CollectionListScrollOffset_CanBeSetAndRead()
    {
        _sut.CollectionListScrollOffset = 5;
        _sut.CollectionListScrollOffset.Should().Be(5);
    }

    [Fact]
    public void CollectionListScrollOffset_ClampsNegativeToZero()
    {
        _sut.CollectionListScrollOffset = -3;
        _sut.CollectionListScrollOffset.Should().Be(0);
    }

    [Fact]
    public void CollectionItemScrollOffset_CanBeSetAndRead()
    {
        _sut.CollectionItemScrollOffset = 10;
        _sut.CollectionItemScrollOffset.Should().Be(10);
    }

    [Fact]
    public void CollectionItemScrollOffset_ClampsNegativeToZero()
    {
        _sut.CollectionItemScrollOffset = -1;
        _sut.CollectionItemScrollOffset.Should().Be(0);
    }

    [Fact]
    public void EnterCollections_ResetsCollectionListScrollOffset()
    {
        _sut.CollectionListScrollOffset = 7;

        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);

        _sut.CollectionListScrollOffset.Should().Be(0);
    }

    [Fact]
    public void EnterCollection_ResetsCollectionItemScrollOffset()
    {
        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        _sut.CollectionItemScrollOffset = 5;

        _sut.EnterCollection(Collection.Create("Test"));

        _sut.CollectionItemScrollOffset.Should().Be(0);
    }

    #endregion

    #region UpdateActiveCollection preserves index

    [Fact]
    public void UpdateActiveCollection_PreservesSelectedIndex()
    {
        var collection = Collection.Create("Test");
        collection.AddItem("https://a.com", "A");
        collection.AddItem("https://b.com", "B");
        collection.AddItem("https://c.com", "C");

        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        _sut.EnterCollection(collection);
        _sut.CollectionItemSelectedIndex = 2; // select last item

        // Simulate refresh with same items
        var refreshed = Collection.Create("Test");
        refreshed.AddItem("https://a.com", "A");
        refreshed.AddItem("https://b.com", "B");
        refreshed.AddItem("https://c.com", "C");

        _sut.UpdateActiveCollection(refreshed);

        _sut.CollectionItemSelectedIndex.Should().Be(2, "UpdateActiveCollection should not reset selected index");
        _sut.ActiveCollection.Should().BeSameAs(refreshed);
    }

    [Fact]
    public void UpdateActiveCollection_PreservesCtaButtonFocus()
    {
        var collection = Collection.Create("Test");
        collection.AddItem("https://a.com", "A");

        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        _sut.EnterCollection(collection);
        // EnterCollection sets index to -1 (CTA) when items exist
        _sut.CollectionItemSelectedIndex.Should().Be(-1);

        var refreshed = Collection.Create("Test");
        refreshed.AddItem("https://a.com", "A");
        refreshed.AddItem("https://b.com", "B");

        _sut.UpdateActiveCollection(refreshed);

        _sut.CollectionItemSelectedIndex.Should().Be(-1, "CTA button focus should be preserved after refresh");
    }

    [Fact]
    public void EnterCollection_SetsCtaForNonEmptyCollection()
    {
        var collection = Collection.Create("Test");
        collection.AddItem("https://a.com", "A");

        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        _sut.EnterCollection(collection);

        _sut.CollectionItemSelectedIndex.Should().Be(-1, "CTA button index for non-empty collection");
    }

    [Fact]
    public void EnterCollection_SetsZeroForEmptyCollection()
    {
        var collection = Collection.Create("Test");

        _sut.EnterCollections(Domain.Enums.Browser.ViewMode.Hierarchical, 0);
        _sut.EnterCollection(collection);

        _sut.CollectionItemSelectedIndex.Should().Be(0, "empty collection starts at index 0");
    }

    #endregion
}
