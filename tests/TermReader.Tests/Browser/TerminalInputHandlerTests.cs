// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Infrastructure.Browser.UI;
using Xunit;

namespace TermReader.Tests.Browser;

public class TerminalInputHandlerTests
{
    private readonly TerminalInputHandler _sut;

    public TerminalInputHandlerTests()
    {
        var logger = Substitute.For<ILogger<TerminalInputHandler>>();
        _sut = new TerminalInputHandler(logger);
    }

    #region Regular key mappings

    [Fact]
    public void MapKeyToCommand_J_ReturnsMoveDown()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.J, 0);
        result.Type.Should().Be(CommandType.MoveDown);
    }

    [Fact]
    public void MapKeyToCommand_K_ReturnsMoveUp()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.K, 0);
        result.Type.Should().Be(CommandType.MoveUp);
    }

    [Fact]
    public void MapKeyToCommand_DownArrow_ReturnsMoveDown()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.DownArrow, 0);
        result.Type.Should().Be(CommandType.MoveDown);
    }

    [Fact]
    public void MapKeyToCommand_UpArrow_ReturnsMoveUp()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.UpArrow, 0);
        result.Type.Should().Be(CommandType.MoveUp);
    }

    [Fact]
    public void MapKeyToCommand_H_ReturnsCollapseNode()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.H, 0);
        result.Type.Should().Be(CommandType.CollapseNode);
    }

    [Fact]
    public void MapKeyToCommand_L_ReturnsExpandNode()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.L, 0);
        result.Type.Should().Be(CommandType.ExpandNode);
    }

    [Fact]
    public void MapKeyToCommand_LeftArrow_ReturnsCollapseNode()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.LeftArrow, 0);
        result.Type.Should().Be(CommandType.CollapseNode);
    }

    [Fact]
    public void MapKeyToCommand_RightArrow_ReturnsExpandNode()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.RightArrow, 0);
        result.Type.Should().Be(CommandType.ExpandNode);
    }

    [Fact]
    public void MapKeyToCommand_Spacebar_ReturnsToggleNode()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.Spacebar, 0);
        result.Type.Should().Be(CommandType.ToggleNode);
    }

    [Fact]
    public void MapKeyToCommand_Enter_ReturnsActivateLink()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.Enter, 0);
        result.Type.Should().Be(CommandType.ActivateLink);
    }

    [Fact]
    public void MapKeyToCommand_B_ReturnsGoBack()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.B, 0);
        result.Type.Should().Be(CommandType.GoBack);
    }

    [Fact]
    public void MapKeyToCommand_Backspace_ReturnsGoBack()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.Backspace, 0);
        result.Type.Should().Be(CommandType.GoBack);
    }

    [Fact]
    public void MapKeyToCommand_V_ReturnsSwitchView()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.V, 0);
        result.Type.Should().Be(CommandType.SwitchView);
    }

    [Fact]
    public void MapKeyToCommand_Tab_ReturnsSwitchView()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.Tab, 0);
        result.Type.Should().Be(CommandType.SwitchView);
    }

    [Fact]
    public void MapKeyToCommand_R_ReturnsSwitchToReadable()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.R, 0);
        result.Type.Should().Be(CommandType.SwitchToReadable);
    }

    [Fact]
    public void MapKeyToCommand_T_ReturnsSwitchToHierarchical()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.T, 0);
        result.Type.Should().Be(CommandType.SwitchToHierarchical);
    }

    [Fact]
    public void MapKeyToCommand_Q_ReturnsQuit()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.Q, 0);
        result.Type.Should().Be(CommandType.Quit);
    }

    [Fact]
    public void MapKeyToCommand_Escape_ReturnsQuit()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.Escape, 0);
        result.Type.Should().Be(CommandType.Quit);
    }

    [Fact]
    public void MapKeyToCommand_F5_ReturnsRefresh()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.F5, 0);
        result.Type.Should().Be(CommandType.Refresh);
    }

    [Fact]
    public void MapKeyToCommand_QuestionMark_ReturnsShowHelp()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.Oem2, 0);
        result.Type.Should().Be(CommandType.ShowHelp);
    }

    [Fact]
    public void MapKeyToCommand_UnknownKey_ReturnsMoveDown()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.F12, 0);
        result.Type.Should().Be(CommandType.MoveDown);
    }

    #endregion

    #region Shift key combinations

    [Fact]
    public void MapKeyToCommand_ShiftG_ReturnsGoToBottom()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.G, ConsoleModifiers.Shift);
        result.Type.Should().Be(CommandType.GoToBottom);
    }

    [Fact]
    public void MapKeyToCommand_ShiftJ_ReturnsReorderDown()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.J, ConsoleModifiers.Shift);
        result.Type.Should().Be(CommandType.ReorderDown);
    }

    [Fact]
    public void MapKeyToCommand_ShiftK_ReturnsReorderUp()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.K, ConsoleModifiers.Shift);
        result.Type.Should().Be(CommandType.ReorderUp);
    }

    [Fact]
    public void MapKeyToCommand_ShiftUnknown_ReturnsMoveDown()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.F12, ConsoleModifiers.Shift);
        result.Type.Should().Be(CommandType.MoveDown);
    }

    #endregion

    #region Ctrl key combinations

    [Fact]
    public void MapKeyToCommand_CtrlD_ReturnsPageDown()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.D, ConsoleModifiers.Control);
        result.Type.Should().Be(CommandType.PageDown);
    }

    [Fact]
    public void MapKeyToCommand_CtrlU_ReturnsPageUp()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.U, ConsoleModifiers.Control);
        result.Type.Should().Be(CommandType.PageUp);
    }

    [Fact]
    public void MapKeyToCommand_CtrlC_ReturnsQuit()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.C, ConsoleModifiers.Control);
        result.Type.Should().Be(CommandType.Quit);
    }

    [Fact]
    public void MapKeyToCommand_CtrlUnknown_ReturnsMoveDown()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.F12, ConsoleModifiers.Control);
        result.Type.Should().Be(CommandType.MoveDown);
    }

    #endregion

    #region GetHelpText

    [Fact]
    public void GetHelpText_ContainsCollectionKeybindings()
    {
        var help = _sut.GetHelpText();

        help.Should().Contain("Save link to default collection");
        help.Should().Contain("Save link to specific collection");
        help.Should().Contain("Delete item");
        help.Should().Contain("Move item down");
        help.Should().Contain("Move item up");
    }

    [Fact]
    public void GetHelpText_ContainsSearchKeybindings()
    {
        var help = _sut.GetHelpText();

        help.Should().Contain("Search");
        help.Should().Contain("Next search match");
        help.Should().Contain("Previous search match");
    }

    [Fact]
    public void GetHelpText_ContainsNavigationKeybindings()
    {
        var help = _sut.GetHelpText();

        help.Should().Contain("Move down");
        help.Should().Contain("Move up");
        help.Should().Contain("Go back");
        help.Should().Contain("Follow selected link");
    }

    #endregion
}
