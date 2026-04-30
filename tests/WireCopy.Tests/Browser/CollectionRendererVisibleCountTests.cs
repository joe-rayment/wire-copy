// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

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
    public void GetCollectionItemsVisibleCount_SmallTerminal_CompactCta()
    {
        // height=20, width=80: CTA is compact slab (3 lines, 18 <= height < 22), linesPerItem=2
        // remainingHeight = Max(3, 20-3-2-3) = 12
        // With separators: 2 lines/item + 1 separator between = 3 lines per slot
        // Max(1, (12+1)/3) = 4
        var result = CollectionRenderer.GetCollectionItemsVisibleCount(20);
        result.Should().Be(4);
    }

    [Fact]
    public void GetCollectionItemsVisibleCount_LargeTerminal_HeroCta()
    {
        // height=40, width=80: CTA is hero box (7 lines, height >= 22 && width-2 >= 50), linesPerItem=2
        // remainingHeight = Max(3, 40-3-2-7) = 28
        // With separators: 2 lines/item + 1 separator between = 3 lines per slot
        // Max(1, (28+1)/3) = 9
        var result = CollectionRenderer.GetCollectionItemsVisibleCount(40);
        result.Should().Be(9);
    }

    [Fact]
    public void GetCollectionItemsVisibleCount_StandardTerminal_HeroCta()
    {
        // height=24, width=80: hero CTA fires (height >= 22), linesPerItem=2
        // remainingHeight = Max(3, 24-3-2-7) = 12
        // Max(1, (12+1)/3) = 4
        var result = CollectionRenderer.GetCollectionItemsVisibleCount(24);
        result.Should().Be(4);
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
