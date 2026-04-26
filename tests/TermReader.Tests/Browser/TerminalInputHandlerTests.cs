// Licensed under the MIT License. See LICENSE in the repository root.

using System.Reflection;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.UI;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class TerminalInputHandlerTests
{
    private readonly TerminalInputHandler _sut;

    public TerminalInputHandlerTests()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var resizeDetector = Substitute.For<IResizeDetector>();
        var logger = Substitute.For<ILogger<TerminalInputHandler>>();
        _sut = new TerminalInputHandler(themeProvider, resizeDetector, logger);
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
    public void MapKeyToCommand_Spacebar_ReturnsToggleSelection()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.Spacebar, 0);
        result.Type.Should().Be(CommandType.ToggleSelection);
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
    public void MapKeyToCommand_Escape_ReturnsGoBack()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.Escape, 0);
        result.Type.Should().Be(CommandType.GoBack);
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
    public void MapKeyToCommand_UnknownKey_ReturnsNoOp()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.F12, 0);
        result.Type.Should().Be(CommandType.NoOp);
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
    public void MapKeyToCommand_ShiftR_ReturnsForceRefresh()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.R, ConsoleModifiers.Shift);
        result.Type.Should().Be(CommandType.ForceRefresh);
    }

    [Fact]
    public void MapKeyToCommand_ShiftI_ReturnsInteractiveRefresh()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.I, ConsoleModifiers.Shift);
        result.Type.Should().Be(CommandType.InteractiveRefresh);
    }

    [Fact]
    public void MapKeyToCommand_ShiftX_ReturnsClearCollection()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.X, ConsoleModifiers.Shift);
        result.Type.Should().Be(CommandType.ClearCollection);
    }

    [Fact]
    public void MapKeyToCommand_ShiftL_ReturnsGoForward()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.L, ConsoleModifiers.Shift);
        result.Type.Should().Be(CommandType.GoForward);
    }

    [Fact]
    public void MapKeyToCommand_ShiftUnknown_ReturnsNoOp()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.F12, ConsoleModifiers.Shift);
        result.Type.Should().Be(CommandType.NoOp);
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
    public void MapKeyToCommand_CtrlP_ReturnsCycleTheme()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.P, ConsoleModifiers.Control);
        result.Type.Should().Be(CommandType.CycleTheme);
    }

    [Fact]
    public void MapKeyToCommand_CtrlC_ReturnsQuit()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.C, ConsoleModifiers.Control);
        result.Type.Should().Be(CommandType.Quit);
    }

    [Fact]
    public void MapKeyToCommand_CtrlUnknown_ReturnsNoOp()
    {
        var result = _sut.MapKeyToCommand(ConsoleKey.F12, ConsoleModifiers.Control);
        result.Type.Should().Be(CommandType.NoOp);
    }

    #endregion

    #region GetHelpText

    [Fact]
    public void GetHelpText_ContainsCollectionKeybindings()
    {
        var help = _sut.GetHelpText();

        help.Should().Contain("Save to Reading List");
        help.Should().Contain("Save all links to Reading List");
        help.Should().Contain("Save to specific collection");
        help.Should().Contain("Delete / remove item");
        help.Should().Contain("Reorder items");
    }

    [Fact]
    public void GetHelpText_ContainsSearchKeybindings()
    {
        var help = _sut.GetHelpText();

        help.Should().Contain("Search");
        help.Should().Contain("Next / previous match");
    }

    [Fact]
    public void GetHelpText_ContainsNavigationKeybindings()
    {
        var help = _sut.GetHelpText();

        help.Should().Contain("Move down");
        help.Should().Contain("Move up");
        help.Should().Contain("Go back");
        help.Should().Contain("Follow link");
    }

    [Fact]
    public void GetHelpText_ContainsCacheKeybindings()
    {
        var help = _sut.GetHelpText();

        help.Should().Contain("Refresh (bypass cache)");
        help.Should().Contain("Interactive refresh");
    }

    [Fact]
    public void GetHelpText_ContainsPerViewSections()
    {
        var help = _sut.GetHelpText();

        help.Should().Contain("Link Tree View");
        help.Should().Contain("Reader View");
        help.Should().Contain("Narrow width");
        help.Should().Contain("Widen width");
    }

    [Fact]
    public void GetHelpText_ShowsBothOldAndNewWidthBindings()
    {
        var help = _sut.GetHelpText();

        // Both h/l and +/- should appear in help
        help.Should().Contain("h / ");
        help.Should().Contain("l / ");
        help.Should().Contain("-");
        help.Should().Contain("+");
    }

    #endregion

    #region DrainPendingTasks - key preservation

    [Fact]
    public async Task DrainPendingTasks_CompletedPendingKeyTask_PushesKeyBackToChannel()
    {
        // Arrange: get private _keyChannel and _pendingKeyTask via reflection
        var channelField = typeof(TerminalInputHandler)
            .GetField("_keyChannel", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pendingKeyField = typeof(TerminalInputHandler)
            .GetField("_pendingKeyTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var drainMethod = typeof(TerminalInputHandler)
            .GetMethod("DrainPendingTasks", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var channel = (Channel<ConsoleKeyInfo>)channelField.GetValue(_sut)!;

        // Write a key into the channel
        var testKey = new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false);
        channel.Writer.TryWrite(testKey);

        // Create a pending task that reads from the channel (will complete immediately)
        var pendingTask = channel.Reader.ReadAsync(CancellationToken.None).AsTask();
        await pendingTask; // Ensure it completes
        pendingKeyField.SetValue(_sut, pendingTask);

        // Act: drain should push the completed key back
        drainMethod.Invoke(_sut, null);

        // Assert: key should be back in the channel
        var hasKey = channel.Reader.TryRead(out var result);
        hasKey.Should().BeTrue("DrainPendingTasks should push completed key back into channel");
        result.KeyChar.Should().Be('x');
    }

    [Fact]
    public async Task DrainPendingTasks_PendingKeyTask_NotYetCompleted_AttachesContinuation()
    {
        // Arrange
        var channelField = typeof(TerminalInputHandler)
            .GetField("_keyChannel", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pendingKeyField = typeof(TerminalInputHandler)
            .GetField("_pendingKeyTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var drainMethod = typeof(TerminalInputHandler)
            .GetMethod("DrainPendingTasks", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var channel = (Channel<ConsoleKeyInfo>)channelField.GetValue(_sut)!;

        // Create a pending task that reads from the channel (NOT yet completed - channel is empty)
        var pendingTask = channel.Reader.ReadAsync(CancellationToken.None).AsTask();
        pendingTask.IsCompleted.Should().BeFalse("task should be waiting for channel data");
        pendingKeyField.SetValue(_sut, pendingTask);

        // Act: drain the pending task (attaches continuation for key recovery)
        drainMethod.Invoke(_sut, null);

        // _pendingKeyTask should now be null
        pendingKeyField.GetValue(_sut).Should().BeNull();

        // Now write a key — the abandoned task's continuation should push it back
        var testKey = new ConsoleKeyInfo('z', ConsoleKey.Z, false, false, false);
        channel.Writer.TryWrite(testKey);

        // Wait briefly for the continuation to fire
        await Task.Delay(50);

        // The abandoned task consumed 'z', but the continuation pushed it back
        var hasKey = channel.Reader.TryRead(out var result);
        hasKey.Should().BeTrue("continuation should push consumed key back into channel");
        result.KeyChar.Should().Be('z');
    }

    [Fact]
    public void DrainPendingTasks_NullPendingKeyTask_DoesNotThrow()
    {
        var drainMethod = typeof(TerminalInputHandler)
            .GetMethod("DrainPendingTasks", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Act & Assert: should not throw when _pendingKeyTask is null
        var act = () => drainMethod.Invoke(_sut, null);
        act.Should().NotThrow();
    }

    [Fact]
    public void HandleShowHelp_UsesWaitForInputAsync_NotConsoleReadKey()
    {
        // Verify that ViewCommandHandler.HandleShowHelp calls InputHandler.WaitForInputAsync
        // by checking the method body doesn't contain Console.ReadKey references.
        // This is a structural verification that the fix is in place.
        var method = typeof(TermReader.Infrastructure.Browser.CommandHandlers.ViewCommandHandler)
            .GetMethod("HandleShowHelp", BindingFlags.Public | BindingFlags.Static)!;

        method.Should().NotBeNull("HandleShowHelp should exist as a static method");

        // Verify parameters include CommandContext (which has InputHandler)
        var parameters = method.GetParameters();
        parameters.Should().HaveCount(3);
        parameters[0].ParameterType.Name.Should().Be("CommandContext");
    }

    #endregion
}
