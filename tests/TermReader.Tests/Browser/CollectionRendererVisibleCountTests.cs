// Educational and personal use only.

using FluentAssertions;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class CollectionRendererVisibleCountTests
{
    #region GetCollectionListVisibleCount

    [Fact]
    public void GetCollectionListVisibleCount_SmallTerminal_NoSeparators()
    {
        // height=20, no separators, linesPerItem=1
        // remainingHeight = Max(3, 20-3-2) = 15
        var result = CollectionRenderer.GetCollectionListVisibleCount(20);
        result.Should().Be(15);
    }

    [Fact]
    public void GetCollectionListVisibleCount_LargeTerminal_NoSeparators()
    {
        // height=40, no separators (removed by default), linesPerItem=1
        // remainingHeight = Max(3, 40-3-2) = 35
        var result = CollectionRenderer.GetCollectionListVisibleCount(40);
        result.Should().Be(35);
    }

    [Fact]
    public void GetCollectionListVisibleCount_MinimumHeight_ClampsTo3()
    {
        // height=5, remainingHeight = Max(3, 5-3-2) = Max(3, 0) = 3
        var result = CollectionRenderer.GetCollectionListVisibleCount(5);
        result.Should().Be(3);
    }

    #endregion

    #region GetCollectionItemsVisibleCount

    [Fact]
    public void GetCollectionItemsVisibleCount_SmallTerminal_InlineCta()
    {
        // height=20, width=80: CTA is inline (1 line, height <= 35), linesPerItem=2
        // remainingHeight = Max(3, 20-3-2-1) = 14
        // Max(1, (14+1)/2) = 7
        var result = CollectionRenderer.GetCollectionItemsVisibleCount(20);
        result.Should().Be(7);
    }

    [Fact]
    public void GetCollectionItemsVisibleCount_LargeTerminal_SlabCta()
    {
        // height=40, width=80: CTA is full slab (5 lines, height > 35), linesPerItem=2
        // remainingHeight = Max(3, 40-3-2-5) = 30
        // Max(1, (30+1)/2) = 15
        var result = CollectionRenderer.GetCollectionItemsVisibleCount(40);
        result.Should().Be(15);
    }

    [Fact]
    public void GetCollectionItemsVisibleCount_MinimumHeight_ClampsTo1()
    {
        // height=5, remainingHeight = Max(3, 5-3-2-1) = Max(3, -1) = 3
        // Max(1, (3+1)/2) = 2
        var result = CollectionRenderer.GetCollectionItemsVisibleCount(5);
        result.Should().BeGreaterOrEqualTo(1);
    }

    #endregion
}
