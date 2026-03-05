// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Domain.Entities.Collections;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Direct tests for CollectionNavigationState, covering the return-point
/// save/restore logic and edge cases not exercised through NavigationService.
/// </summary>
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
    public void CollectionItemSelectedIndex_ClampsNegativeToZero()
    {
        _sut.CollectionItemSelectedIndex = -1;
        _sut.CollectionItemSelectedIndex.Should().Be(0);
    }

    #endregion
}
