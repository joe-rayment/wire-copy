// Educational and personal use only.

using FluentAssertions;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

public class CollectionRendererVisibleCountTests
{
    #region GetCollectionListVisibleCount

    [Fact]
    public void GetCollectionListVisibleCount_SmallTerminal_NoSeparators()
    {
        // height=20 < 30, so no separators, linesPerItem=1
        // remainingHeight = Max(3, 20-5-3) = 12
        var result = CollectionRenderer.GetCollectionListVisibleCount(20);
        result.Should().Be(12);
    }

    [Fact]
    public void GetCollectionListVisibleCount_LargeTerminal_WithSeparators()
    {
        // height=40 >= 30, so separators, linesPerItem=2
        // remainingHeight = Max(3, 40-5-3) = 32
        // (32+1)/2 = 16
        var result = CollectionRenderer.GetCollectionListVisibleCount(40);
        result.Should().Be(16);
    }

    [Fact]
    public void GetCollectionListVisibleCount_MinimumHeight_ClampsTo3()
    {
        // height=5, remainingHeight = Max(3, 5-5-3) = Max(3, -3) = 3
        var result = CollectionRenderer.GetCollectionListVisibleCount(5);
        result.Should().Be(3);
    }

    #endregion

    #region GetCollectionItemsVisibleCount

    [Fact]
    public void GetCollectionItemsVisibleCount_SmallTerminal_NoSeparators()
    {
        // height=20, podcastButtonLines=4 (20 is not < 20), linesPerItem=2
        // remainingHeight = Max(3, 20-5-3-4) = 8
        // Max(1, (8+1)/2) = 4
        var result = CollectionRenderer.GetCollectionItemsVisibleCount(20);
        result.Should().Be(4);
    }

    [Fact]
    public void GetCollectionItemsVisibleCount_LargeTerminal_WithSeparators()
    {
        // height=40, podcastButtonLines=4, linesPerItem=3
        // remainingHeight = Max(3, 40-5-3-4) = 28
        // Max(1, (28+1)/3) = 9
        var result = CollectionRenderer.GetCollectionItemsVisibleCount(40);
        result.Should().Be(9);
    }

    [Fact]
    public void GetCollectionItemsVisibleCount_MinimumHeight_ClampsTo1()
    {
        // height=5, remainingHeight = Max(3, -3) = 3
        // Max(1, (3+1)/2) = 2
        var result = CollectionRenderer.GetCollectionItemsVisibleCount(5);
        result.Should().BeGreaterOrEqualTo(1);
    }

    #endregion
}
