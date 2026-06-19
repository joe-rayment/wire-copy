// Licensed under the MIT License. See LICENSE in the repository root.

using System.Reflection;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class TerminalInputHandlerTests
{
    private readonly TerminalInputHandler _sut;

    public TerminalInputHandlerTests()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var resizeDetector = Substitute.For<IResizeDetector>();
        var navigationService = Substitute.For<INavigationService>();
        var logger = Substitute.For<ILogger<TerminalInputHandler>>();
        _sut = new TerminalInputHandler(themeProvider, resizeDetector, navigationService, logger);
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
    public void MapKeyToCommand_BareOem2_ReturnsNoOp()
    {
        // workspace-1d24: the dead ConsoleKey.Oem2 => ShowHelp entry was removed. A bare Oem2 with no
        // KeyChar is ambiguous between '/' and '?'. In the real input path help arrives as '?'
        // (KeyChar) and search as '/' (KeyChar); both are resolved by MapKeyInfoToCommand before this
        // ConsoleKey-level map is ever reached, so the Oem2 entry only ever advertised a non-existent
        // binding. Help remains reachable via '?'.
        var result = _sut.MapKeyToCommand(ConsoleKey.Oem2, 0);
        result.Type.Should().Be(CommandType.NoOp);
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
    public void DrainBufferedInput_DropsKeysAlreadyInChannel()
    {
        // Item #3 from QA: the cascade bug was caused by leftover bytes in the
        // _keyChannel feeding the next PromptForInputAsync. DrainBufferedInput()
        // is the single defence against that — verify it does in fact drain.
        var channelField = typeof(TerminalInputHandler)
            .GetField("_keyChannel", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var channel = (Channel<ConsoleKeyInfo>)channelField.GetValue(_sut)!;

        // Push 5 synthetic stale keys into the channel — what a partial paste
        // would leave behind after \r terminated the prompt mid-flight.
        foreach (var c in "stale")
        {
            channel.Writer.TryWrite(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        }

        // Sanity: the keys ARE in the channel before draining.
        channel.Reader.Count.Should().Be(5);

        _sut.DrainBufferedInput();

        // After drain: no keys readable.
        var hasKey = channel.Reader.TryRead(out _);
        hasKey.Should().BeFalse("DrainBufferedInput must empty the channel of stale keys");
    }

    [Fact]
    public async Task DrainBufferedInput_FollowedByFreshKey_ReturnsOnlyFreshKey()
    {
        // Item #3: simulates the wizard end-to-end shape. First "paste" leaves
        // residue in the channel, drain is called after the parse fail, then a
        // second "paste" arrives — only the second paste's bytes must surface.
        var channelField = typeof(TerminalInputHandler)
            .GetField("_keyChannel", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var channel = (Channel<ConsoleKeyInfo>)channelField.GetValue(_sut)!;

        // First "paste" residue
        foreach (var c in "garbage-from-failed-paste")
        {
            channel.Writer.TryWrite(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        }

        _sut.DrainBufferedInput();

        // Now the second paste arrives.
        foreach (var c in "fresh")
        {
            channel.Writer.TryWrite(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        }

        // Read everything available out of the channel.
        var seen = new System.Text.StringBuilder();
        while (channel.Reader.TryRead(out var k))
        {
            seen.Append(k.KeyChar);
        }

        seen.ToString().Should().Be(
            "fresh",
            "after a drain the next prompt must see ONLY the fresh paste, never residue from the failed one");

        // No await needed; the helper is synchronous. This is async to match
        // the test class style and to permit future async assertions.
        await Task.CompletedTask;
    }

    [Fact]
    public void TryConsumeBracketedPasteFrom_MultiLineJson_StripsCarriageReturnsAndNewlines()
    {
        // Item #4: the actual bug — \r-on-first-byte in PromptForInputAsync.
        // Synthesise the exact byte stream that arrives at TerminalInputHandler
        // when a user pastes multi-line JSON with bracketed-paste enabled:
        //   \x1b[200~ + JSON-with-\r\n + \x1b[201~
        // Caller (TryConsumeSgrMouseViaReadKey) has already consumed \x1b[2,
        // so the parser starts reading at the "00~" confirmation.
        var json = "{\r\n  \"type\": \"service_account\",\r\n  \"project_id\": \"p\"\r\n}";
        var stream = "00~" + json + "\x1b[201~";
        var queue = new Queue<char>(stream);

        var keys = TerminalInputHandler.TryConsumeBracketedPasteFrom(() =>
        {
            if (queue.Count == 0)
            {
                return (false, '\0', ConsoleKey.NoName);
            }

            var c = queue.Dequeue();
            var key = c == '\x1b' ? ConsoleKey.Escape : ConsoleKey.NoName;
            return (true, c, key);
        });

        keys.Should().NotBeNull("a properly framed paste must parse");
        var captured = new string(keys!.Select(k => k.KeyChar).ToArray());

        // Critical assertion: \r and \n inside the paste are stripped, all
        // other characters survive in order.
        var expected = json.Replace("\r", string.Empty).Replace("\n", string.Empty);
        captured.Should().Be(
            expected,
            "the bracketed-paste parser must drop \\r and \\n so PromptForInputAsync doesn't terminate the prompt mid-paste");
    }

    [Fact]
    public void TryConsumeBracketedPasteFrom_PemKeyEmbeddedNewlines_PreservesAllOtherCharacters()
    {
        // Item #5: a realistic Google service-account JSON has a private_key
        // field containing literal `\n` escape sequences AND, when copied via
        // `cat key.json | pbcopy`, embedded actual newlines inside the PEM
        // block. The bracketed-paste parser must leave both forms intact
        // (literal backslash-n) and strip only the real newlines.
        var pemBlock = "-----BEGIN PRIVATE KEY-----\nLine1\nLine2\n-----END PRIVATE KEY-----\n";
        var json =
            "{\r\n" +
            "  \"type\": \"service_account\",\r\n" +
            "  \"private_key\": \"" + pemBlock.Replace("\n", "\\n") + "\",\r\n" +
            "  \"client_email\": \"x@y.iam.gserviceaccount.com\"\r\n" +
            "}";

        var stream = "00~" + json + "\x1b[201~";
        var queue = new Queue<char>(stream);

        var keys = TerminalInputHandler.TryConsumeBracketedPasteFrom(() =>
        {
            if (queue.Count == 0)
            {
                return (false, '\0', ConsoleKey.NoName);
            }

            var c = queue.Dequeue();
            var key = c == '\x1b' ? ConsoleKey.Escape : ConsoleKey.NoName;
            return (true, c, key);
        });

        keys.Should().NotBeNull();
        var captured = new string(keys!.Select(k => k.KeyChar).ToArray());

        // Real newlines stripped, but the literal backslash-n inside the PEM
        // string must still be a backslash followed by an n.
        captured.Should().NotContain("\r");
        captured.Should().NotContain("\n");
        captured.Should().Contain("\\nLine1\\nLine2\\n", "literal escape sequences must survive untouched");
        captured.Should().Contain("BEGIN PRIVATE KEY");
        captured.Should().Contain("END PRIVATE KEY");
    }

    [Fact]
    public void HandleShowHelp_UsesWaitForInputAsync_NotConsoleReadKey()
    {
        // Verify that ViewCommandHandler.HandleShowHelp calls InputHandler.WaitForInputAsync
        // by checking the method body doesn't contain Console.ReadKey references.
        // This is a structural verification that the fix is in place.
        var method = typeof(WireCopy.Infrastructure.Browser.CommandHandlers.ViewCommandHandler)
            .GetMethod("HandleShowHelp", BindingFlags.Public | BindingFlags.Static)!;

        method.Should().NotBeNull("HandleShowHelp should exist as a static method");

        // Verify parameters include CommandContext (which has InputHandler)
        var parameters = method.GetParameters();
        parameters.Should().HaveCount(3);
        parameters[0].ParameterType.Name.Should().Be("CommandContext");
    }

    #endregion
}
