// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

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
        // -1 is valid (URL bar), but lower values clamp to -1
        _sut.LauncherSelectedIndex = -5;

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
