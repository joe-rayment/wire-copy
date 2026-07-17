// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class NavigationServiceLauncherTests
{
    private readonly NavigationService _sut;

    public NavigationServiceLauncherTests()
    {
        var logger = Substitute.For<ILogger<NavigationService>>();
        _sut = new NavigationService(logger);
    }

    #region EnterLauncher

    [Fact]
    public void EnterLauncher_SetsViewModeToLauncher()
    {
        _sut.EnterLauncher();

        _sut.CurrentContext.ViewMode.Should().Be(ViewMode.Launcher);
    }

    [Fact]
    public void EnterLauncher_ResetsSelectedIndex()
    {
        _sut.LauncherSelectedIndex = 5;

        _sut.EnterLauncher();

        _sut.LauncherSelectedIndex.Should().Be(0);
    }

    [Fact]
    public void EnterLauncher_ResetsScrollOffset()
    {
        _sut.LauncherScrollOffset = 3;

        _sut.EnterLauncher();

        _sut.LauncherScrollOffset.Should().Be(0);
    }

    [Fact]
    public void InLauncherMode_WhenInLauncher_ReturnsTrue()
    {
        _sut.EnterLauncher();

        _sut.InLauncherMode.Should().BeTrue();
    }

    [Fact]
    public void InLauncherMode_WhenNotInLauncher_ReturnsFalse()
    {
        _sut.InLauncherMode.Should().BeFalse();
    }

    #endregion

    #region LauncherSelectedIndex

    [Fact]
    public void LauncherSelectedIndex_ClampedToMinusOne()
    {
        // Sentinels: -1 = URL bar, 0+ = grid item.
        // Values below -1 clamp to -1 (workspace-ayt8 — setup hint is chrome,
        // not a focusable element, so there is no -2 sentinel).
        _sut.LauncherSelectedIndex = -5;

        _sut.LauncherSelectedIndex.Should().Be(-1);
    }

    [Fact]
    public void LauncherSelectedIndex_AcceptsUrlBarSentinel()
    {
        _sut.LauncherSelectedIndex = -1;

        _sut.LauncherSelectedIndex.Should().Be(-1);
    }

    [Fact]
    public void LauncherSelectedIndex_SetsValidValue()
    {
        _sut.LauncherSelectedIndex = 7;

        _sut.LauncherSelectedIndex.Should().Be(7);
    }

    #endregion

    #region LauncherScrollOffset

    [Fact]
    public void LauncherScrollOffset_ClampedToNonNegative()
    {
        _sut.LauncherScrollOffset = -3;

        _sut.LauncherScrollOffset.Should().Be(0);
    }

    [Fact]
    public void LauncherScrollOffset_SetsValidValue()
    {
        _sut.LauncherScrollOffset = 2;

        _sut.LauncherScrollOffset.Should().Be(2);
    }

    #endregion

    #region MoveInGrid

    [Fact]
    public void MoveInGrid_Down_MovesToNextRow()
    {
        // Grid with 6 items, 2 columns: [0,1], [2,3], [4,5]
        // From index 0, move down → index 2
        var result = NavigationService.MoveInGrid(0, 6, direction: 1, columns: 2);

        result.Should().Be(2);
    }

    [Fact]
    public void MoveInGrid_Up_MovesToPreviousRow()
    {
        // From index 2, move up → index 0
        var result = NavigationService.MoveInGrid(2, 6, direction: 0, columns: 2);

        result.Should().Be(0);
    }

    [Fact]
    public void MoveInGrid_Right_MovesToNextColumn()
    {
        // From index 0, move right → index 1
        var result = NavigationService.MoveInGrid(0, 6, direction: 3, columns: 2);

        result.Should().Be(1);
    }

    [Fact]
    public void MoveInGrid_Left_MovesToPreviousColumn()
    {
        // From index 1, move left → index 0
        var result = NavigationService.MoveInGrid(1, 6, direction: 2, columns: 2);

        result.Should().Be(0);
    }

    [Fact]
    public void MoveInGrid_Up_AtTopRow_StaysAtTop()
    {
        // From index 0, move up → stays at 0
        var result = NavigationService.MoveInGrid(0, 6, direction: 0, columns: 2);

        result.Should().Be(0);
    }

    [Fact]
    public void MoveInGrid_Left_AtLeftEdge_StaysInPlace()
    {
        // From index 0, move left → stays at 0
        var result = NavigationService.MoveInGrid(0, 6, direction: 2, columns: 2);

        result.Should().Be(0);
    }

    [Fact]
    public void MoveInGrid_Right_AtRightEdge_StaysInPlace()
    {
        // From index 1, move right → stays at 1 (already at column 1 in 2-column grid)
        var result = NavigationService.MoveInGrid(1, 6, direction: 3, columns: 2);

        result.Should().Be(1);
    }

    // Responsive columns (workspace-ehon): the launcher grid now reaches 3-4
    // columns on a wide desktop-shell window; nav must follow the live count.
    // 9 items, 3 columns: [0,1,2] [3,4,5] [6,7,8].
    [Fact]
    public void MoveInGrid_ThreeColumns_Right_StepsAcrossAllColumns()
    {
        NavigationService.MoveInGrid(0, 9, direction: 3, columns: 3).Should().Be(1);
        NavigationService.MoveInGrid(1, 9, direction: 3, columns: 3).Should().Be(2);
    }

    [Fact]
    public void MoveInGrid_ThreeColumns_Right_AtLastColumn_StaysInPlace()
    {
        NavigationService.MoveInGrid(2, 9, direction: 3, columns: 3).Should().Be(2);
    }

    [Fact]
    public void MoveInGrid_ThreeColumns_Down_PreservesColumn()
    {
        NavigationService.MoveInGrid(2, 9, direction: 1, columns: 3).Should().Be(5);
        NavigationService.MoveInGrid(1, 9, direction: 1, columns: 3).Should().Be(4);
    }

    [Fact]
    public void MoveInGrid_ThreeColumns_Down_OntoShortLastRow_ClampsToLastItem()
    {
        // 7 items, 3 cols: [0,1,2] [3,4,5] [6]. Down from index 5 (col 2) would be
        // index 8 but clamps to the last existing item, 6.
        NavigationService.MoveInGrid(5, 7, direction: 1, columns: 3).Should().Be(6);
    }

    [Fact]
    public void MoveInGrid_Down_ClampedToLastItem()
    {
        // From index 4 (row 2), move down → clamped to 5 (last item)
        var result = NavigationService.MoveInGrid(4, 6, direction: 1, columns: 2);

        result.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public void MoveInGrid_WithSingleItem_AlwaysReturnsZero()
    {
        var result = NavigationService.MoveInGrid(0, 1, direction: 1, columns: 2);

        result.Should().Be(0);
    }

    [Fact]
    public void MoveInGrid_WithEmptyGrid_ReturnsZero()
    {
        var result = NavigationService.MoveInGrid(0, 0, direction: 1, columns: 2);

        result.Should().Be(0);
    }

    [Fact]
    public void MoveInGrid_Down_PastEnd_ClampedToLastItem()
    {
        // 3 items in 2 columns: [0,1], [2]
        // From index 2, move down → would be 4, clamped to 2
        var result = NavigationService.MoveInGrid(2, 3, direction: 1, columns: 2);

        result.Should().Be(2);
    }

    [Fact]
    public void MoveInGrid_Right_WithOddItems_DoesNotGoOutOfBounds()
    {
        // 3 items in 2 columns: [0,1], [2]
        // From index 2, move right → would be 3, clamped to 2
        var result = NavigationService.MoveInGrid(2, 3, direction: 3, columns: 2);

        result.Should().Be(2);
    }

    #endregion
}
