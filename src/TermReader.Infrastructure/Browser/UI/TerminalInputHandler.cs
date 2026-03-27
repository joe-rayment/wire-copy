// Educational and personal use only.

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI;

/// <summary>
/// Handles keyboard input with vim-like bindings for the terminal browser.
/// Owns stdin via a persistent background thread that feeds a key channel.
/// Races keyboard input against resize events from IResizeDetector.
/// </summary>
public class TerminalInputHandler : IInputHandler
{
    private readonly IThemeProvider _themeProvider;
    private readonly IResizeDetector _resizeDetector;
    private readonly ILogger<TerminalInputHandler> _logger;
    private readonly Channel<ConsoleKeyInfo> _keyChannel = Channel.CreateUnbounded<ConsoleKeyInfo>();
    private bool _waitingForSecondKey; // For 'gg' command
    private bool _keyReaderStarted;
    private Task<ConsoleKeyInfo>? _pendingKeyTask;
    private Task<bool>? _pendingResizeTask;

    public TerminalInputHandler(IThemeProvider themeProvider, IResizeDetector resizeDetector, ILogger<TerminalInputHandler> logger)
    {
        _themeProvider = themeProvider;
        _resizeDetector = resizeDetector;
        _logger = logger;
        IsInteractive = !Console.IsInputRedirected;
    }

    /// <inheritdoc />
    public bool IsInteractive { get; }

    public async Task<NavigationCommand> WaitForInputAsync(CancellationToken cancellationToken = default)
    {
        EnsureKeyReaderStarted();

        try
        {
            return await WaitForInputCoreAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            // Key channel closed (stdin unavailable) — treat as clean exit
            _logger.LogInformation("Input channel closed — exiting input loop");
            throw new OperationCanceledException("stdin unavailable");
        }
    }

    public NavigationCommand MapKeyToCommand(ConsoleKey key, ConsoleModifiers modifiers)
    {
        return MapKeyToCommandStatic(key, modifiers);
    }

    public string GetHelpText()
    {
        return @"
 Shortcuts
 ══════════════════════════════════════════════════════════════

 Navigation (all views)
   j / ↓           Move down / scroll
   k / ↑           Move up / scroll
   Enter           Follow link / open item
   b / Backspace   Go back
   Shift+L         Go forward

 Link Tree View
   h / ←           Collapse group
   l / →           Expand group
   Space           Toggle expand/collapse

 Reader View
   h / ← / -       Narrow width
   l / → / +       Widen width
   0               Reset width

 Scrolling
   Ctrl+d          Page down
   Ctrl+u          Page up
   gg              Go to top
   G               Go to bottom

 Views
   v / Tab         Toggle Link View ↔ Reader View
   r               Reader View
   t               Link Tree View

 Search & Commands
   /               Search
   n / N           Next / previous match
   :               Command line

 Launcher
   h/j/k/l         Navigate grid
   a               Add bookmark
   d               Delete bookmark
   Shift+J / K     Reorder bookmarks
   :rename <name>  Rename selected bookmark
   c               Reading list
   :home           Return to launcher

 Reading List
   Space           Select / deselect item or section
   s               Save to Reading List (or all selected)
   Shift+A         Save all links to Reading List
   Shift+S         Save to specific collection
   d               Delete / remove item
   Shift+J / K     Reorder items
   Shift+X         Clear all items from collection
   p               Generate podcast from collection
   :new <name>     Create new collection
   :rename <name>  Rename current collection
   :clear          Clear current collection
   :export [fmt]   Export collection (urls, opml)

 Cache
   Shift+R / F5    Refresh (bypass cache)
   Shift+I         Interactive refresh (headed browser for captcha/login)

 General
   Ctrl+p          Cycle theme
   Esc             Go back          q               Quit

 Press any key to close...
";
    }

    public async Task<string?> PromptForUrlAsync(string prompt = "Enter URL: ", CancellationToken cancellationToken = default)
    {
        // Called before main loop, before key reader starts - uses Console.ReadLine directly
        Console.WriteLine();
        var palette = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        Console.Write(palette.PromptLabelFg.AnsiFg);
        Console.WriteLine("TermReader - Browser Mode");
        Console.WriteLine("================================");
        Console.Write("\x1b[0m");
        Console.WriteLine();
        Console.Write(prompt);

        var url = await Task.Run(() => Console.ReadLine(), cancellationToken);

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        // Add https:// if missing
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url;
    }

    public async Task<string?> PromptForInputAsync(string prompt, CancellationToken cancellationToken = default, bool isSecret = false, int? row = null, int? col = null, string? initialInput = null)
    {
        EnsureKeyReaderStarted();

        // Drain any pending key/resize tasks so they don't compete with
        // PromptForInputAsync's direct channel reads.
        DrainPendingTasks();

        try
        {
            // Position at specified row or default to bottom of terminal
            var targetRow = row ?? Console.WindowHeight - 1;
            var targetCol = col ?? 0;
            Console.SetCursorPosition(targetCol, targetRow);

            // Clear the line and show prompt
            var clearWidth = Math.Max(1, Console.WindowWidth - 1 - targetCol);
            Console.Write(new string(' ', clearWidth));
            Console.SetCursorPosition(targetCol, targetRow);
            var palette = BuiltInThemes.Get(_themeProvider.CurrentTheme);
            Console.Write(palette.PromptFg.AnsiFg);
            Console.Write(prompt);
            Console.Write("\x1b[0m");

            var promptStart = targetCol + prompt.Length;

            // Read input from the key channel
            var input = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(initialInput))
            {
                input.Append(initialInput);
                Console.Write(initialInput);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var keyInfo = await _keyChannel.Reader.ReadAsync(cancellationToken);

                if (keyInfo.Key == ConsoleKey.Escape)
                {
                    return null;
                }

                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    var result = input.ToString();
                    return string.IsNullOrWhiteSpace(result) ? null : result;
                }

                if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (input.Length > 0)
                    {
                        input.Remove(input.Length - 1, 1);

                        // Redraw the input line
                        Console.SetCursorPosition(promptStart, targetRow);
                        var display = isSecret ? new string('*', input.Length) : input.ToString();
                        Console.Write(display + " ");
                        Console.SetCursorPosition(promptStart + input.Length, targetRow);
                    }

                    continue;
                }

                // Printable character
                if (keyInfo.KeyChar >= 32)
                {
                    input.Append(keyInfo.KeyChar);
                    Console.Write(isSecret ? '*' : keyInfo.KeyChar);
                }
            }
        }
        catch (IOException)
        {
            // Fallback if cursor positioning fails
        }
        catch (InvalidOperationException)
        {
            // Fallback if console operations fail
        }

        return null;
    }

    private async Task<NavigationCommand> WaitForInputCoreAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_pendingKeyTask is { IsFaulted: true } or { IsCanceled: true })
            {
                _pendingKeyTask = null;
            }

            if (_pendingResizeTask is { IsFaulted: true } or { IsCanceled: true })
            {
                _pendingResizeTask = null;
            }

            _pendingKeyTask ??= _keyChannel.Reader.ReadAsync(cancellationToken).AsTask();
            _pendingResizeTask ??= _resizeDetector.Resizes.ReadAsync(cancellationToken).AsTask();

            var completed = await Task.WhenAny(_pendingKeyTask, _pendingResizeTask).ConfigureAwait(false);

            if (completed == _pendingResizeTask)
            {
                _pendingResizeTask = null;

                while (_resizeDetector.Resizes.TryRead(out _))
                {
                    // Drain queued resize signals to coalesce into one event
                }

                return new NavigationCommand { Type = CommandType.TerminalResized };
            }

            if (_pendingKeyTask == null)
            {
                continue;
            }

            var keyInfo = await _pendingKeyTask.ConfigureAwait(false);
            _pendingKeyTask = null;

            if (_waitingForSecondKey && keyInfo.Key == ConsoleKey.G)
            {
                _waitingForSecondKey = false;
                return new NavigationCommand { Type = CommandType.GoToTop };
            }

            if (_waitingForSecondKey)
            {
                _waitingForSecondKey = false;
            }

            if (keyInfo.Key == ConsoleKey.G && (keyInfo.Modifiers & ConsoleModifiers.Shift) == 0)
            {
                _waitingForSecondKey = true;
                continue;
            }

            var command = MapKeyInfoToCommand(keyInfo);
            if (keyInfo.KeyChar >= 32)
            {
                command = command with { RawKeyChar = keyInfo.KeyChar };
            }

            if (command.Type != CommandType.NoOp)
            {
                _logger.LogDebug("Input: {Key} -> {CommandType}", keyInfo.Key, command.Type);
            }

            return command;
        }

        return new NavigationCommand { Type = CommandType.Quit };
    }

#pragma warning disable SA1204 // Static members grouped with related non-static key mapping methods
    private static NavigationCommand MapKeyInfoToCommand(ConsoleKeyInfo keyInfo)
    {
        return keyInfo.KeyChar switch
        {
            ':' => new NavigationCommand { Type = CommandType.OpenCommandLine },
            '/' => new NavigationCommand { Type = CommandType.Search },
            'n' => new NavigationCommand { Type = CommandType.SearchNext },
            'N' => new NavigationCommand { Type = CommandType.SearchPrevious },
            's' => new NavigationCommand { Type = CommandType.SaveToCollection },
            'S' => new NavigationCommand { Type = CommandType.SaveToSpecific },
            'A' => new NavigationCommand { Type = CommandType.SaveAllToReadingList },
            'd' => new NavigationCommand { Type = CommandType.DeleteItem },
            'p' => new NavigationCommand { Type = CommandType.GeneratePodcast },
            'a' => new NavigationCommand { Type = CommandType.AddBookmark },
            'c' => new NavigationCommand { Type = CommandType.OpenCollections },
            '+' or '=' or ']' => new NavigationCommand { Type = CommandType.IncreaseWidth },
            '-' or '_' or '[' => new NavigationCommand { Type = CommandType.DecreaseWidth },
            '0' => new NavigationCommand { Type = CommandType.ResetWidth },
            '?' => new NavigationCommand { Type = CommandType.ShowHelp },
            'D' => new NavigationCommand { Type = CommandType.DumpHtml },
            'o' => new NavigationCommand { Type = CommandType.OpenInBrowser },
            'z' => new NavigationCommand { Type = CommandType.Undo },
            _ => MapKeyToCommandStatic(keyInfo.Key, keyInfo.Modifiers)
        };
    }

    private static NavigationCommand MapKeyToCommandStatic(ConsoleKey key, ConsoleModifiers modifiers)
    {
        if ((modifiers & ConsoleModifiers.Shift) != 0)
        {
            return key switch
            {
                ConsoleKey.G => new NavigationCommand { Type = CommandType.GoToBottom },
                ConsoleKey.J => new NavigationCommand { Type = CommandType.ReorderDown },
                ConsoleKey.K => new NavigationCommand { Type = CommandType.ReorderUp },
                ConsoleKey.R => new NavigationCommand { Type = CommandType.ForceRefresh },
                ConsoleKey.I => new NavigationCommand { Type = CommandType.InteractiveRefresh },
                ConsoleKey.X => new NavigationCommand { Type = CommandType.ClearCollection },
                ConsoleKey.L => new NavigationCommand { Type = CommandType.GoForward },
                _ => new NavigationCommand { Type = CommandType.NoOp }
            };
        }

        if ((modifiers & ConsoleModifiers.Control) != 0)
        {
            return key switch
            {
                ConsoleKey.D => new NavigationCommand { Type = CommandType.PageDown },
                ConsoleKey.U => new NavigationCommand { Type = CommandType.PageUp },
                ConsoleKey.P => new NavigationCommand { Type = CommandType.CycleTheme },
                ConsoleKey.C => new NavigationCommand { Type = CommandType.Quit },
                _ => new NavigationCommand { Type = CommandType.NoOp }
            };
        }

        return key switch
        {
            ConsoleKey.J => new NavigationCommand { Type = CommandType.MoveDown },
            ConsoleKey.K => new NavigationCommand { Type = CommandType.MoveUp },
            ConsoleKey.DownArrow => new NavigationCommand { Type = CommandType.MoveDown },
            ConsoleKey.UpArrow => new NavigationCommand { Type = CommandType.MoveUp },
            ConsoleKey.H => new NavigationCommand { Type = CommandType.CollapseNode },
            ConsoleKey.L => new NavigationCommand { Type = CommandType.ExpandNode },
            ConsoleKey.LeftArrow => new NavigationCommand { Type = CommandType.CollapseNode },
            ConsoleKey.RightArrow => new NavigationCommand { Type = CommandType.ExpandNode },
            ConsoleKey.Spacebar => new NavigationCommand { Type = CommandType.ToggleSelection },
            ConsoleKey.Enter => new NavigationCommand { Type = CommandType.ActivateLink },
            ConsoleKey.B => new NavigationCommand { Type = CommandType.GoBack },
            ConsoleKey.Backspace => new NavigationCommand { Type = CommandType.GoBack },
            ConsoleKey.V => new NavigationCommand { Type = CommandType.SwitchView },
            ConsoleKey.Tab => new NavigationCommand { Type = CommandType.SwitchView },
            ConsoleKey.R => new NavigationCommand { Type = CommandType.SwitchToReadable },
            ConsoleKey.T => new NavigationCommand { Type = CommandType.SwitchToHierarchical },
            ConsoleKey.Q => new NavigationCommand { Type = CommandType.Quit },
            ConsoleKey.Escape => new NavigationCommand { Type = CommandType.GoBack },
            ConsoleKey.F5 => new NavigationCommand { Type = CommandType.Refresh },
            ConsoleKey.Oem2 => new NavigationCommand { Type = CommandType.ShowHelp },
            _ => new NavigationCommand { Type = CommandType.NoOp }
        };
    }

    /// <summary>
    /// Reads an escape sequence from stdin after the initial \x1b byte.
    /// Returns ConsoleKeyInfo array for recognized sequences (arrows, mouse scroll),
    /// or null if it's just a bare Escape keypress.
    /// </summary>
    private static List<ConsoleKeyInfo>? ReadEscapeSequence(Stream stdin)
    {
        // Peek for CSI: \x1b[
        if (!TryReadByteWithTimeout(stdin, out var next))
        {
            return null; // Bare Escape (no follow-up within timeout)
        }

        if (next != '[')
        {
            return null; // Alt+key or unknown — treat as Escape (byte is lost, acceptable)
        }

        // Read CSI parameter bytes until a final byte (0x40-0x7E)
        if (!TryReadByteWithTimeout(stdin, out var third))
        {
            return null;
        }

        // SGR mouse: \x1b[<button;x;y[Mm]
        if (third == '<')
        {
            return ReadSgrMouse(stdin);
        }

        // Single-letter final bytes: \x1b[A/B/C/D (arrows), H/F (Home/End)
        if (third >= 0x40 && third <= 0x7E)
        {
            return third switch
            {
                (byte)'A' => [new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false)],
                (byte)'B' => [new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false)],
                (byte)'C' => [new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false)],
                (byte)'D' => [new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false)],
                (byte)'H' => [new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false)],
                (byte)'F' => [new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false)],
                _ => [], // Unknown single-byte CSI — discard
            };
        }

        // Parameterized CSI: \x1b[N~ (F-keys, Insert, Delete, PageUp/Down)
        return ReadParameterizedCsi(stdin, third);
    }

    /// <summary>
    /// Reads a parameterized CSI sequence like \x1b[15~ (F5) or \x1b[5~ (PageUp).
    /// The third byte (first digit) has already been read.
    /// </summary>
    private static List<ConsoleKeyInfo>? ReadParameterizedCsi(Stream stdin, int firstParamByte)
    {
        var buf = new List<byte>(8) { (byte)firstParamByte };
        while (true)
        {
            if (!TryReadByteWithTimeout(stdin, out var b))
            {
                return [];
            }

            if (b >= 0x40 && b <= 0x7E)
            {
                // Final byte — sequence complete
                if (b == '~')
                {
                    var param = System.Text.Encoding.ASCII.GetString(buf.ToArray());
                    return param switch
                    {
                        "1" or "7" => [new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false)],
                        "2" => [new ConsoleKeyInfo('\0', ConsoleKey.Insert, false, false, false)],
                        "3" => [new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false)],
                        "4" or "8" => [new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false)],
                        "5" => [new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false)],
                        "6" => [new ConsoleKeyInfo('\0', ConsoleKey.PageDown, false, false, false)],
                        "15" => [new ConsoleKeyInfo('\0', ConsoleKey.F5, false, false, false)],
                        _ => [],
                    };
                }

                return []; // Non-tilde final byte — unknown, discard
            }

            buf.Add((byte)b);
            if (buf.Count > 16)
            {
                return []; // Runaway
            }
        }
    }

    /// <summary>
    /// Reads an SGR mouse sequence: button;x;y terminated by 'M' (press) or 'm' (release).
    /// Scroll wheel up = button 64, down = button 65.
    /// Returns synthetic j/k keys for scroll, null for other mouse events.
    /// </summary>
    private static List<ConsoleKeyInfo>? ReadSgrMouse(Stream stdin)
    {
        var buf = new List<byte>(16);
        while (true)
        {
            var b = stdin.ReadByte();
            if (b < 0)
            {
                return null;
            }

            if (b == 'M' || b == 'm')
            {
                break;
            }

            buf.Add((byte)b);

            if (buf.Count > 32)
            {
                return null; // Runaway sequence
            }
        }

        // Parse "button;x;y"
        var parts = System.Text.Encoding.ASCII.GetString(buf.ToArray()).Split(';');
        if (parts.Length < 1 || !int.TryParse(parts[0], out var button))
        {
            return null;
        }

        // Scroll wheel: button 64 = up, 65 = down (3 lines per tick)
        const int scrollLines = 3;
        if (button == 64)
        {
            var keys = new List<ConsoleKeyInfo>(scrollLines);
            for (var i = 0; i < scrollLines; i++)
            {
                keys.Add(new ConsoleKeyInfo('k', ConsoleKey.K, false, false, false));
            }

            return keys;
        }

        if (button == 65)
        {
            var keys = new List<ConsoleKeyInfo>(scrollLines);
            for (var i = 0; i < scrollLines; i++)
            {
                keys.Add(new ConsoleKeyInfo('j', ConsoleKey.J, false, false, false));
            }

            return keys;
        }

        // Other mouse events (click, drag) — discard
        return [];
    }

    private static bool TryReadByteWithTimeout(Stream stdin, out int result)
    {
        // Escape sequences arrive as an atomic burst from the terminal pty —
        // all bytes are buffered at once, so ReadByte returns immediately.
        // For a bare Escape keypress, only \x1b arrives, so the next read blocks.
        // This blocking behavior (until next keypress) matches standard terminal
        // apps (vim ttimeoutlen). The Escape key fires when the NEXT key arrives.
        // This is the correct and safe approach — no orphaned tasks, no data loss.
        result = stdin.ReadByte();
        return result >= 0;
    }

    /// <summary>
    /// Converts a raw stdin byte to a ConsoleKeyInfo.
    /// Handles ASCII printable chars, Enter, Tab, Backspace, and Ctrl+key.
    /// </summary>
    private static ConsoleKeyInfo ByteToKeyInfo(int b)
    {
        // Ctrl+key: bytes 1-26 map to Ctrl+A through Ctrl+Z
        if (b >= 1 && b <= 26)
        {
            var key = (ConsoleKey)((int)ConsoleKey.A + b - 1);
            return new ConsoleKeyInfo((char)b, key, false, false, true);
        }

        return b switch
        {
            0 => new ConsoleKeyInfo('\0', ConsoleKey.NoName, false, false, true), // Ctrl+@/Space
            13 or 10 => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            9 => new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false),
            127 => new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
            32 => new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false),
            >= 33 and <= 126 => MapPrintableChar((char)b),
            _ => new ConsoleKeyInfo((char)b, ConsoleKey.NoName, false, false, false),
        };
    }

    private static ConsoleKeyInfo MapPrintableChar(char c)
    {
        var shift = char.IsUpper(c) || "~!@#$%^&*()_+{}|:\"<>?".Contains(c);
        var key = char.ToUpper(c) switch
        {
            >= 'A' and <= 'Z' => (ConsoleKey)((int)ConsoleKey.A + char.ToUpper(c) - 'A'),
            >= '0' and <= '9' => (ConsoleKey)((int)ConsoleKey.D0 + c - '0'),
            '/' => ConsoleKey.Oem2,
            '?' => ConsoleKey.Oem2,
            '[' or '{' => ConsoleKey.Oem4,
            ']' or '}' => ConsoleKey.Oem6,
            '\\' or '|' => ConsoleKey.Oem5,
            ';' or ':' => ConsoleKey.Oem1,
            '\'' or '"' => ConsoleKey.Oem7,
            ',' or '<' => ConsoleKey.OemComma,
            '.' or '>' => ConsoleKey.OemPeriod,
            '-' or '_' => ConsoleKey.OemMinus,
            '=' or '+' => ConsoleKey.OemPlus,
            '`' or '~' => ConsoleKey.Oem3,
            _ => ConsoleKey.NoName,
        };
        return new ConsoleKeyInfo(c, key, shift, false, false);
    }

    private void EnsureKeyReaderStarted()
    {
        if (_keyReaderStarted)
        {
            return;
        }

        _keyReaderStarted = true;

        if (!IsInteractive)
        {
            _logger.LogWarning("stdin is not a terminal — keyboard input unavailable");
            _keyChannel.Writer.TryComplete();
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                using var stdin = Console.OpenStandardInput();
                while (true)
                {
                    var b = stdin.ReadByte();
                    if (b < 0)
                    {
                        break;
                    }

                    if (b == 0x1b)
                    {
                        var result = ReadEscapeSequence(stdin);
                        if (result != null)
                        {
                            foreach (var k in result)
                            {
                                _keyChannel.Writer.TryWrite(k);
                            }

                            continue;
                        }

                        _keyChannel.Writer.TryWrite(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));
                        continue;
                    }

                    _keyChannel.Writer.TryWrite(ByteToKeyInfo(b));
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Key reader: stdin unavailable");
                _keyChannel.Writer.TryComplete();
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Key reader: stdin closed");
                _keyChannel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Key reader thread crashed, will restart on next input request");

                // Allow restart by resetting the flag and recreating the channel
                _keyReaderStarted = false;
            }
        })
        {
            IsBackground = true,
            Name = "TermReader-KeyReader"
        };
        thread.Start();
    }

    /// <summary>
    /// Clears any pending key/resize tasks so that other methods
    /// (e.g. PromptForInputAsync) can safely read from the channel directly.
    /// If the pending key task already completed, pushes the key back into the channel.
    /// If it's still awaiting, attaches a continuation to push the key back when it arrives.
    /// </summary>
    private void DrainPendingTasks()
    {
        if (_pendingKeyTask != null)
        {
            if (_pendingKeyTask.IsCompletedSuccessfully)
            {
                // Push the already-read key back into the channel
                _keyChannel.Writer.TryWrite(_pendingKeyTask.Result);
            }
            else if (!_pendingKeyTask.IsCompleted)
            {
                // Task is still awaiting a key from the channel reader.
                // Attach a continuation so the key isn't lost when it eventually arrives.
                var channel = _keyChannel;
                _pendingKeyTask.ContinueWith(
                    t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            channel.Writer.TryWrite(t.Result);
                        }
                    },
                    TaskContinuationOptions.ExecuteSynchronously);
            }

            _pendingKeyTask = null;
        }

        // Resize events are coalesced; safe to discard any pending resize read
        _pendingResizeTask = null;
    }
}
