// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Tests for NavigationService collection-related state management:
/// EnterCollections/ExitCollections, SaveCollectionReturnPoint,
/// and collection index tracking.
/// </summary>
public class NavigationServiceCollectionTests
{
    private readonly NavigationService _sut;

    public NavigationServiceCollectionTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _sut = new NavigationService(logger);
    }

    private static Page CreateTestPage(string url, string title)
    {
        var metadata = new PageMetadata { Title = title };
        var html = $"<html><head><title>{title}</title></head><body></body></html>";
        return Page.Create(url, html, metadata);
    }

    #region EnterCollections / ExitCollections

    [Fact]
    public void EnterCollections_SetsViewModeToCollectionList()
    {
        // Arrange
        var page = CreateTestPage("https://example.com", "Test");
        _sut.NavigateTo(page);

        // Act
        _sut.EnterCollections();

        // Assert
        _sut.InCollectionsMode.Should().BeTrue();
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.CollectionList);
    }

    [Fact]
    public void EnterCollections_ResetsCollectionSelectedIndex()
    {
        // Arrange
        var page = CreateTestPage("https://example.com", "Test");
        _sut.NavigateTo(page);

        // Act
        _sut.EnterCollections();

        // Assert
        _sut.CollectionSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void ExitCollections_RestoresPreviousViewMode()
    {
        // Arrange
        var page = CreateTestPage("https://example.com", "Test");
        _sut.NavigateTo(page);
        _sut.SetViewMode(ViewMode.Readable);
        _sut.SetScrollOffset(42);

        // Act
        _sut.EnterCollections();
        _sut.ExitCollections();

        // Assert
        _sut.InCollectionsMode.Should().BeFalse();
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Readable);
        _sut.CurrentContext.ScrollOffset.Should().Be(42);
    }

    [Fact]
    public void ExitCollections_ClearsActiveCollection()
    {
        // Arrange
        var page = CreateTestPage("https://example.com", "Test");
        _sut.NavigateTo(page);
        _sut.EnterCollections();

        var collection = Collection.Create("Test Collection");
        _sut.EnterCollection(collection);

        // Act
        _sut.ExitCollections();

        // Assert
        _sut.ActiveCollection.Should().BeNull();
        _sut.InCollectionsMode.Should().BeFalse();
    }

    #endregion

    #region EnterCollection / ExitToCollectionList

    [Fact]
    public void EnterCollection_SetsActiveCollectionAndViewMode()
    {
        // Arrange
        _sut.EnterCollections();
        var collection = Collection.Create("My Bookmarks");

        // Act
        _sut.EnterCollection(collection);

        // Assert
        _sut.ActiveCollection.Should().BeSameAs(collection);
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.CollectionItems);
        _sut.CollectionItemSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void ExitToCollectionList_ClearsActiveCollectionAndGoesBackToList()
    {
        // Arrange
        _sut.EnterCollections();
        var collection = Collection.Create("My Bookmarks");
        _sut.EnterCollection(collection);

        // Act
        _sut.ExitToCollectionList();

        // Assert
        _sut.ActiveCollection.Should().BeNull();
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.CollectionList);
    }

    #endregion

    #region CollectionSelectedIndex / CollectionItemSelectedIndex

    [Fact]
    public void CollectionSelectedIndex_CanBeSetAndRetrieved()
    {
        // Act
        _sut.CollectionSelectedIndex = 5;

        // Assert
        _sut.CollectionSelectedIndex.Should().Be(5);
    }

    [Fact]
    public void CollectionSelectedIndex_ClampsToZero()
    {
        // Act
        _sut.CollectionSelectedIndex = -3;

        // Assert
        _sut.CollectionSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void CollectionItemSelectedIndex_CanBeSetAndRetrieved()
    {
        // Act
        _sut.CollectionItemSelectedIndex = 7;

        // Assert
        _sut.CollectionItemSelectedIndex.Should().Be(7);
    }

    [Fact]
    public void CollectionItemSelectedIndex_ClampsToZero()
    {
        // Act
        _sut.CollectionItemSelectedIndex = -1;

        // Assert
        _sut.CollectionItemSelectedIndex.Should().Be(0);
    }

    #endregion

    #region SaveCollectionReturnPoint / TryRestoreCollectionReturnPoint

    [Fact]
    public void SaveCollectionReturnPoint_WithActiveCollection_SavesReturnPoint()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        _sut.NavigateTo(page1);
        _sut.EnterCollections();

        var collection = Collection.Create("Test Collection");
        collection.AddItem("https://article.com/1", "Article 1");
        collection.AddItem("https://article.com/2", "Article 2");
        _sut.EnterCollection(collection);
        _sut.CollectionItemSelectedIndex = 1;

        // Act
        _sut.SaveCollectionReturnPoint();

        // Navigate to article (simulates activating a collection item link)
        var articlePage = CreateTestPage("https://article.com/2", "Article 2");
        _sut.NavigateTo(articlePage);

        // Assert - can restore back
        var restored = _sut.TryRestoreCollectionReturnPoint();
        restored.Should().BeTrue();
        _sut.InCollectionsMode.Should().BeTrue();
        _sut.ActiveCollection.Should().NotBeNull();
        _sut.ActiveCollection!.Name.Should().Be("Test Collection");
        _sut.CollectionItemSelectedIndex.Should().Be(1);
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.CollectionItems);
    }

    [Fact]
    public void TryRestoreCollectionReturnPoint_WithNoSavedPoint_ReturnsFalse()
    {
        // Act
        var result = _sut.TryRestoreCollectionReturnPoint();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void SaveCollectionReturnPoint_WithNoActiveCollection_DoesNotSave()
    {
        // Arrange - no active collection
        _sut.EnterCollections();

        // Act
        _sut.SaveCollectionReturnPoint();

        // Assert
        var result = _sut.TryRestoreCollectionReturnPoint();
        result.Should().BeFalse();
    }

    [Fact]
    public void TryRestoreCollectionReturnPoint_ClearsReturnPointAfterRestore()
    {
        // Arrange
        var page = CreateTestPage("https://example.com", "Test");
        _sut.NavigateTo(page);
        _sut.EnterCollections();

        var collection = Collection.Create("Test");
        _sut.EnterCollection(collection);
        _sut.SaveCollectionReturnPoint();

        var articlePage = CreateTestPage("https://article.com", "Article");
        _sut.NavigateTo(articlePage);

        // Act - first restore should succeed
        _sut.TryRestoreCollectionReturnPoint().Should().BeTrue();

        // Act - second restore should fail (return point cleared)
        var secondResult = _sut.TryRestoreCollectionReturnPoint();
        secondResult.Should().BeFalse();
    }

    [Fact]
    public void TryRestoreCollectionReturnPoint_PopsBackHistory()
    {
        // Arrange
        var page1 = CreateTestPage("https://example.com", "Home");
        _sut.NavigateTo(page1);
        _sut.EnterCollections();

        var collection = Collection.Create("Test");
        _sut.EnterCollection(collection);
        _sut.SaveCollectionReturnPoint();

        var page2 = CreateTestPage("https://article.com", "Article");
        _sut.NavigateTo(page2);

        // Act
        _sut.TryRestoreCollectionReturnPoint();

        // Assert - should pop back to page1
        _sut.CurrentPage!.Url.Should().Be("https://example.com");
    }

    #endregion

    #region Navigation state preservation across collection mode

    [Fact]
    public void NavigationHistory_PreservedDuringCollectionsMode()
    {
        // Arrange - navigate to two pages
        var page1 = CreateTestPage("https://example.com/1", "Page 1");
        var page2 = CreateTestPage("https://example.com/2", "Page 2");
        _sut.NavigateTo(page1);
        _sut.NavigateTo(page2);

        // Act - enter and exit collections
        _sut.EnterCollections();
        _sut.ExitCollections();

        // Assert - back history preserved
        _sut.CanGoBack.Should().BeTrue();
        _sut.CurrentPage!.Url.Should().Be("https://example.com/2");
    }

    [Fact]
    public void ToggleViewMode_SwitchesHierarchicalToReadable()
    {
        // Arrange
        var page = CreateTestPage("https://example.com", "Test");
        _sut.NavigateTo(page);
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Hierarchical);

        // Act
        _sut.ToggleViewMode();

        // Assert
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Readable);
    }

    [Fact]
    public void ToggleViewMode_SwitchesReadableToHierarchical()
    {
        // Arrange
        var page = CreateTestPage("https://example.com", "Test");
        _sut.NavigateTo(page);
        _sut.SetViewMode(ViewMode.Readable);

        // Act
        _sut.ToggleViewMode();

        // Assert
        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Hierarchical);
    }

    [Fact]
    public void SetViewMode_ResetsScrollOffset()
    {
        // Arrange
        var page = CreateTestPage("https://example.com", "Test");
        _sut.NavigateTo(page);
        _sut.SetScrollOffset(50);

        // Act
        _sut.SetViewMode(ViewMode.Readable);

        // Assert
        _sut.CurrentContext.ScrollOffset.Should().Be(0);
    }

    [Fact]
    public void SetSearchQuery_ResetsMatchIndex()
    {
        // Arrange
        _sut.SetSearchQuery("old query");
        _sut.SetSearchMatchIndex(5);

        // Act
        _sut.SetSearchQuery("new query");

        // Assert
        _sut.CurrentContext.SearchQuery.Should().Be("new query");
        _sut.CurrentContext.SearchMatchIndex.Should().Be(0);
    }

    #endregion

    #region HasCollectionReturnPoint

    [Fact]
    public void HasCollectionReturnPoint_Initially_ReturnsFalse()
    {
        _sut.HasCollectionReturnPoint.Should().BeFalse();
    }

    [Fact]
    public void HasCollectionReturnPoint_AfterSave_ReturnsTrue()
    {
        var page = CreateTestPage("https://example.com", "Test");
        _sut.NavigateTo(page);
        _sut.EnterCollections();

        var collection = Collection.Create("Test");
        collection.AddItem("https://article.com/1", "Article 1");
        _sut.EnterCollection(collection);
        _sut.SaveCollectionReturnPoint();

        _sut.HasCollectionReturnPoint.Should().BeTrue();
    }

    [Fact]
    public void HasCollectionReturnPoint_AfterRestore_ReturnsFalse()
    {
        var page = CreateTestPage("https://example.com", "Test");
        _sut.NavigateTo(page);
        _sut.EnterCollections();

        var collection = Collection.Create("Test");
        _sut.EnterCollection(collection);
        _sut.SaveCollectionReturnPoint();

        var articlePage = CreateTestPage("https://article.com", "Article");
        _sut.NavigateTo(articlePage);
        _sut.TryRestoreCollectionReturnPoint();

        _sut.HasCollectionReturnPoint.Should().BeFalse();
    }

    [Fact]
    public void HasCollectionReturnPoint_NoActiveCollection_ReturnsFalse()
    {
        _sut.EnterCollections();
        _sut.SaveCollectionReturnPoint();

        _sut.HasCollectionReturnPoint.Should().BeFalse();
    }

    #endregion
}
