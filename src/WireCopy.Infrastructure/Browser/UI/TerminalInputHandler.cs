// Licensed under the MIT License. See LICENSE in the repository root.

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI;

/// <summary>
/// Handles keyboard input with vim-like bindings for the terminal browser.
/// Owns stdin via a persistent background thread that feeds a key channel.
/// Races keyboard input against resize events from IResizeDetector.
/// </summary>
public class TerminalInputHandler : IInputHandler
{
    private readonly IThemeProvider _themeProvider;
    private readonly IResizeDetector _resizeDetector;
    private readonly INavigationService _navigationService;
    private readonly ILogger<TerminalInputHandler> _logger;
    private readonly Channel<ConsoleKeyInfo> _keyChannel = Channel.CreateUnbounded<ConsoleKeyInfo>();
    private readonly TerminalAnimationController _animationController;
    private bool _waitingForSecondKey; // For 'gg' command
    private int _numericPrefix; // For count-prefixed motions (e.g., 10j)
    private bool _keyReaderStarted;
    private Task<ConsoleKeyInfo>? _pendingKeyTask;
    private Task<bool>? _pendingResizeTask;
    private Task? _pendingAnimationTick;

    public TerminalInputHandler(IThemeProvider themeProvider, IResizeDetector resizeDetector, INavigationService navigationService, ILogger<TerminalInputHandler> logger)
    {
        _themeProvider = themeProvider;
        _resizeDetector = resizeDetector;
        _navigationService = navigationService;
        _logger = logger;
        _animationController = new TerminalAnimationController();
        IsInteractive = !Console.IsInputRedirected;
    }

    /// <inheritdoc />
    public bool IsInteractive { get; }

    /// <inheritdoc />
    public IAnimationController AnimationController => _animationController;

    public async Task<NavigationCommand> WaitForInputAsync(CancellationToken cancellationToken = default)
    {
        EnsureKeyReaderStarted();

        try
        {
            return await WaitForInputCoreAsync(cancellationToken).ConfigureAwait(false);
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
   j / ↓           Move down (prefix: 10j = 10 lines)
   k / ↑           Move up (prefix: 5k = 5 lines)
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
   Shift+E         Regenerate article layout (AI re-extract)

 Speed Reading (Reader View)
   f               Toggle speed reading on/off
   < / >           Slower / faster WPM

 Scrolling
   Ctrl+d          Page down
   Ctrl+u          Page up
   gg              Go to top (5gg = go to item 5)
   G               Go to bottom (5G = go to item 5)

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
   \               Prefetch detail (what's cached / loading now)

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
        Console.WriteLine("WireCopy - Browser Mode");
        Console.WriteLine("================================");
        Console.Write("\x1b[0m");
        Console.WriteLine();
        Console.Write(prompt);

        var url = await Task.Run(() => Console.ReadLine(), cancellationToken).ConfigureAwait(false);

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

    public Task<string?> PromptForInputAsync(string prompt, CancellationToken cancellationToken = default, bool isSecret = false, int? row = null, int? col = null, string? initialInput = null, Func<char, bool>? interceptKey = null)
    {
        // Public 7-arg signature unchanged (workspace-cgnt: do NOT extend
        // this signature — the GCS verify branch tried that and broke
        // qi5p's NSubstitute mocks). Delegates to PromptForInputCoreAsync
        // with clearWidth=null, preserving the historical "clear to right
        // edge" behaviour for command-line / search-bar callers.
        return PromptForInputCoreAsync(prompt, cancellationToken, isSecret, row, col, initialInput, interceptKey, clearWidth: null);
    }

    /// <summary>
    /// Specialised entry point for FormField callers (workspace-cgnt).
    /// Narrows the row-clear to <paramref name="clearWidth"/> cells so the
    /// surrounding box border characters survive typing. Lives on the
    /// concrete class (not <see cref="IInputHandler"/>) so existing
    /// NSubstitute mocks of the interface remain unaffected.
    /// </summary>
    public Task<string?> PromptForFieldInputAsync(string prompt, CancellationToken cancellationToken, bool isSecret, int row, int col, string? initialInput, Func<char, bool>? interceptKey, int clearWidth)
    {
        return PromptForInputCoreAsync(prompt, cancellationToken, isSecret, row, col, initialInput, interceptKey, clearWidth);
    }

    /// <summary>
    /// workspace-syj1.3 — specialised entry point for the ':' command line: renders a
    /// faint ghost <paramref name="hint"/> (e.g. " (:help)") right after the prompt.
    /// The hint disappears on the first keystroke (the input redraw pads the full
    /// width). Lives on the concrete class, like <see cref="PromptForFieldInputAsync"/>,
    /// so the 7-arg <see cref="IInputHandler"/> signature — and its NSubstitute mocks —
    /// stay untouched.
    /// </summary>
    public Task<string?> PromptForInputWithHintAsync(string prompt, string hint, CancellationToken cancellationToken, int? col = null)
    {
        return PromptForInputCoreAsync(prompt, cancellationToken, isSecret: false, row: null, col: col, initialInput: null, interceptKey: null, clearWidth: null, ghostHint: hint);
    }

#pragma warning disable SA1202 // private core kept adjacent to the two public wrappers that delegate to it
    private async Task<string?> PromptForInputCoreAsync(string prompt, CancellationToken cancellationToken, bool isSecret, int? row, int? col, string? initialInput, Func<char, bool>? interceptKey, int? clearWidth, string? ghostHint = null)
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

            // Show cursor during input — it's hidden globally by BrowserOrchestrator
            Console.CursorVisible = true;

            // Clear the line and render prompt. workspace-cgnt: when caller
            // passes clearWidth (e.g. FormField), narrow the wipe to the
            // box's inner-content width so the surrounding │ … │ border
            // chars survive typing. Default behaviour (clear to the right
            // edge) is preserved for non-FormField callers.
            Console.SetCursorPosition(targetCol, targetRow);
            var clearCells = clearWidth.HasValue
                ? Math.Max(1, clearWidth.Value)
                : Math.Max(1, Console.WindowWidth - 1 - targetCol);
            Console.Write(new string(' ', clearCells));
            Console.SetCursorPosition(targetCol, targetRow);
            var palette = BuiltInThemes.Get(_themeProvider.CurrentTheme);
            Console.Write(palette.PromptFg.AnsiFg);
            Console.Write(prompt);
            Console.Write("\x1b[0m");

            var promptStart = targetCol + prompt.Length;

            // Read input with cursor position tracking
            var input = new System.Text.StringBuilder();
            var cursorPos = 0;

            // Cap the visible input width so we never write past the right
            // border of a FormField box. Falls back to "WindowWidth - prompt"
            // when no explicit clearWidth was supplied.
            var maxInputCells = clearWidth.HasValue
                ? Math.Max(1, clearWidth.Value - prompt.Length)
                : Math.Max(1, Console.WindowWidth - 1 - promptStart);

            // workspace-syj1.3: faint ghost hint after the prompt (e.g. ':' → ' (:help)').
            // The first keystroke's RedrawInput pads the full input width, wiping it.
            if (!string.IsNullOrEmpty(ghostHint) && string.IsNullOrEmpty(initialInput))
            {
                var visibleHint = ghostHint.Length <= maxInputCells ? ghostHint : ghostHint[..maxInputCells];
                Console.Write("\x1b[2m");
                Console.Write(palette.SecondaryText.AnsiFg);
                Console.Write(visibleHint);
                Console.Write("\x1b[0m");
                Console.SetCursorPosition(promptStart, targetRow);
            }

            if (!string.IsNullOrEmpty(initialInput))
            {
                input.Append(initialInput);
                cursorPos = initialInput.Length;
                RedrawInput(input, promptStart, targetRow, cursorPos, isSecret, maxInputCells);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var keyInfo = await _keyChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

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
                    if (cursorPos > 0)
                    {
                        input.Remove(cursorPos - 1, 1);
                        cursorPos--;
                        RedrawInput(input, promptStart, targetRow, cursorPos, isSecret, maxInputCells);
                    }

                    continue;
                }

                if (keyInfo.Key == ConsoleKey.Delete)
                {
                    if (cursorPos < input.Length)
                    {
                        input.Remove(cursorPos, 1);
                        RedrawInput(input, promptStart, targetRow, cursorPos, isSecret, maxInputCells);
                    }

                    continue;
                }

                // workspace-romy.6: cursor movement redraws via RedrawInput —
                // the visible window may need to slide, and a bare
                // SetCursorPosition could land past the box edge on long input.
                if (keyInfo.Key == ConsoleKey.LeftArrow)
                {
                    if (cursorPos > 0)
                    {
                        cursorPos--;
                        RedrawInput(input, promptStart, targetRow, cursorPos, isSecret, maxInputCells);
                    }

                    continue;
                }

                if (keyInfo.Key == ConsoleKey.RightArrow)
                {
                    if (cursorPos < input.Length)
                    {
                        cursorPos++;
                        RedrawInput(input, promptStart, targetRow, cursorPos, isSecret, maxInputCells);
                    }

                    continue;
                }

                if (keyInfo.Key == ConsoleKey.Home)
                {
                    cursorPos = 0;
                    RedrawInput(input, promptStart, targetRow, cursorPos, isSecret, maxInputCells);
                    continue;
                }

                if (keyInfo.Key == ConsoleKey.End)
                {
                    cursorPos = input.Length;
                    RedrawInput(input, promptStart, targetRow, cursorPos, isSecret, maxInputCells);
                    continue;
                }

                // Printable character — give the caller a chance to intercept
                // (e.g. ? for a help overlay) before inserting into the buffer.
                if (keyInfo.KeyChar >= 32)
                {
                    if (interceptKey != null && interceptKey(keyInfo.KeyChar))
                    {
                        // Caller consumed the key. The overlay may have moved the
                        // cursor or written into the prompt row, so redraw the
                        // prompt label, current input, and restore cursor before
                        // continuing the input loop. Honour the same narrow
                        // clearWidth as the initial draw so border chrome
                        // around the field is preserved (workspace-cgnt).
                        Console.SetCursorPosition(targetCol, targetRow);
                        var clearCells2 = clearWidth.HasValue
                            ? Math.Max(1, clearWidth.Value)
                            : Math.Max(1, Console.WindowWidth - 1 - targetCol);
                        Console.Write(new string(' ', clearCells2));
                        Console.SetCursorPosition(targetCol, targetRow);
                        Console.Write(palette.PromptFg.AnsiFg);
                        Console.Write(prompt);
                        Console.Write("\x1b[0m");
                        RedrawInput(input, promptStart, targetRow, cursorPos, isSecret, maxInputCells);
                        continue;
                    }

                    input.Insert(cursorPos, keyInfo.KeyChar);
                    cursorPos++;
                    RedrawInput(input, promptStart, targetRow, cursorPos, isSecret, maxInputCells);
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
        finally
        {
            // Hide cursor again when leaving input mode
            try
            {
                Console.CursorVisible = false;
            }
            catch
            {
                // Ignore if console is unavailable
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void DrainBufferedInput()
    {
        // Drop any in-flight pending reads first so we don't fight them.
        DrainPendingTasks();

        // Drain queued keys from the channel, then wait briefly for any in-flight
        // paste bytes the background reader hasn't enqueued yet, then drain again.
        // Two passes catch the common case where a partial paste is mid-flight.
        DrainChannel();
        Thread.Sleep(50);
        DrainChannel();
    }
#pragma warning restore SA1202

    /// <summary>
    /// Redraws the input text at the prompt position and sets cursor. The
    /// optional <paramref name="maxInputCells"/> cap is honoured so FormField
    /// callers (with a narrow box) don't paint over the right-hand border —
    /// see workspace-cgnt.
    /// workspace-romy.6: input longer than the visible box scrolls horizontally
    /// instead of freezing — the old code truncated the DISPLAY at the box
    /// width and pinned the cursor there, so typing past ~60 cells in a docked
    /// terminal looked dead even though the buffer kept growing. The visible
    /// window slides to keep the cursor in view, with '…' markers at a
    /// scrolled-off edge.
    /// </summary>
    private static void RedrawInput(System.Text.StringBuilder input, int promptStart, int targetRow, int cursorPos, bool isSecret, int? maxInputCells = null)
    {
        Console.SetCursorPosition(promptStart, targetRow);
        var display = isSecret ? new string('*', input.Length) : input.ToString();
        var maxWidth = maxInputCells.HasValue
            ? Math.Max(1, maxInputCells.Value)
            : Math.Max(1, Console.WindowWidth - 1 - promptStart);

        var (window, windowStart) = ScrollWindow(display, cursorPos, maxWidth);
        Console.Write(window.PadRight(maxWidth)); // full-width pad clears stale cells as the window slides
        Console.SetCursorPosition(promptStart + Math.Min(cursorPos - windowStart, maxWidth - 1), targetRow);
    }

    /// <summary>
    /// workspace-romy.6: computes the visible slice of <paramref name="display"/>
    /// for a box of <paramref name="maxWidth"/> cells so the cursor is always
    /// inside the window. Stateless: the window start derives from the cursor
    /// alone (cursor pinned to the right edge once it passes the width).
    /// Scrolled-off edges are marked with '…'. Returns the slice and its start
    /// index in the full string.
    /// </summary>
#pragma warning disable SA1202 // kept adjacent to RedrawInput, its only production caller
    internal static (string Window, int Start) ScrollWindow(string display, int cursorPos, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(display);
        maxWidth = Math.Max(1, maxWidth);
        cursorPos = Math.Clamp(cursorPos, 0, display.Length);

        // Reserve one cell at the right edge for the cursor to sit in when at
        // end-of-input, so it never overwrites the box border.
        var start = Math.Max(0, cursorPos - maxWidth + 1);
        var visible = Math.Min(maxWidth, display.Length - start);
        var window = display.Substring(start, visible);

        if (start > 0 && window.Length > 0)
        {
            window = '…' + window[1..];
        }

        if (start + visible < display.Length && window.Length > 1)
        {
            window = window[..^1] + '…';
        }

        return (window, start);
    }
#pragma warning restore SA1202

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

            if (_pendingAnimationTick is { IsFaulted: true } or { IsCanceled: true })
            {
                _pendingAnimationTick = null;
            }

            _pendingKeyTask ??= _keyChannel.Reader.ReadAsync(cancellationToken).AsTask();
            _pendingResizeTask ??= _resizeDetector.Resizes.ReadAsync(cancellationToken).AsTask();

            // Build the task list: always key + resize, optionally animation tick
            var raceTasks = new List<Task>(3) { _pendingKeyTask, _pendingResizeTask };

            if (_animationController.AnimationState.HasActiveAnimation)
            {
                _pendingAnimationTick ??= Task.Delay(_animationController.AnimationIntervalMs, cancellationToken);
                raceTasks.Add(_pendingAnimationTick);
            }
            else
            {
                _pendingAnimationTick = null;
            }

            var completed = await Task.WhenAny(raceTasks).ConfigureAwait(false);

            // Animation tick fired — return AnimationTick command without consuming key input
            if (_pendingAnimationTick != null && completed == _pendingAnimationTick)
            {
                _pendingAnimationTick = null;
                _animationController.AnimationState.AdvanceFrame();
                return new NavigationCommand { Type = CommandType.AnimationTick };
            }

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
                var count = _numericPrefix;
                _numericPrefix = 0;
                return new NavigationCommand { Type = CommandType.GoToTop, Count = count };
            }

            if (_waitingForSecondKey)
            {
                _waitingForSecondKey = false;
            }

            // In launcher view, digits 1-9 jump-select the corresponding bookmark
            // (workspace-wxht). The badges advertise this binding; honour it
            // without entering the count-prefix accumulator. The numeric-prefix
            // path below remains active for Hierarchical/Reader views, where
            // count-prefixed motions like 5j / 10G are useful.
            if (keyInfo.KeyChar >= '1' && keyInfo.KeyChar <= '9' && !_waitingForSecondKey
                && _navigationService.InLauncherMode && _numericPrefix == 0)
            {
                return new NavigationCommand
                {
                    Type = CommandType.JumpToIndex,
                    Count = keyInfo.KeyChar - '0',
                    RawKeyChar = keyInfo.KeyChar,
                };
            }

            // Accumulate numeric prefix for count-prefixed motions (e.g., 10j, 5G)
            if (keyInfo.KeyChar >= '1' && keyInfo.KeyChar <= '9' && !_waitingForSecondKey)
            {
                _numericPrefix = (_numericPrefix * 10) + (keyInfo.KeyChar - '0');
                continue;
            }

            if (keyInfo.KeyChar == '0' && _numericPrefix > 0)
            {
                _numericPrefix = _numericPrefix * 10;
                continue;
            }

            if (keyInfo.Key == ConsoleKey.G && (keyInfo.Modifiers & ConsoleModifiers.Shift) == 0)
            {
                _waitingForSecondKey = true;
                continue;
            }

            var count2 = _numericPrefix;
            _numericPrefix = 0;

            var command = MapKeyInfoToCommand(keyInfo);
            if (keyInfo.KeyChar >= 32)
            {
                command = command with { RawKeyChar = keyInfo.KeyChar };
            }

            if (count2 > 0)
            {
                command = command with { Count = count2 };
            }

            if (command.Type != CommandType.NoOp)
            {
                _logger.LogDebug("Input: {Key} -> {CommandType} (count: {Count})", keyInfo.Key, command.Type, count2);
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
            'E' => new NavigationCommand { Type = CommandType.RegenerateArticleLayout },
            'd' => new NavigationCommand { Type = CommandType.DeleteItem },
            'p' => new NavigationCommand { Type = CommandType.GeneratePodcast },
            'a' => new NavigationCommand { Type = CommandType.AddBookmark },
            'c' => new NavigationCommand { Type = CommandType.OpenCollections },
            '+' or '=' or ']' => new NavigationCommand { Type = CommandType.IncreaseWidth },
            '-' or '_' or '[' => new NavigationCommand { Type = CommandType.DecreaseWidth },
            '0' => new NavigationCommand { Type = CommandType.ResetWidth },
            '{' => new NavigationCommand { Type = CommandType.ParagraphUp },
            '}' => new NavigationCommand { Type = CommandType.ParagraphDown },
            '?' => new NavigationCommand { Type = CommandType.ShowHelp },
            'D' => new NavigationCommand { Type = CommandType.DumpHtml },
            'o' => new NavigationCommand { Type = CommandType.OpenInBrowser },

            // 'O' (not a vim motion; pairs with 'o' = open-in-browser). Avoids the
            // vim word-motion key 'w' so the dock switcher doesn't shadow navigation.
            'O' => new NavigationCommand { Type = CommandType.ToggleBrowserDock },
            'y' => new NavigationCommand { Type = CommandType.AdoptLensPage },
            'L' => new NavigationCommand { Type = CommandType.TuneArticleLayout },
            'z' => new NavigationCommand { Type = CommandType.Undo },
            'x' => new NavigationCommand { Type = CommandType.CancelRun },
            'f' => new NavigationCommand { Type = CommandType.ToggleSpeedRead },
            '<' => new NavigationCommand { Type = CommandType.SpeedReadSlower },
            '>' => new NavigationCommand { Type = CommandType.SpeedReadFaster },
            '\\' => new NavigationCommand { Type = CommandType.TogglePreloadDetail },
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

                // workspace-vkhr: Shift+P restores the detached podcast-generation
                // modal. No-op when no active background job exists.
                ConsoleKey.P => new NavigationCommand { Type = CommandType.RestorePodcastModal },
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
                ConsoleKey.L => new NavigationCommand { Type = CommandType.ChooseLayout },
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
    /// Attempts to consume an SGR mouse sequence after Console.ReadKey returned Escape
    /// and KeyAvailable is true. SGR format: \x1b[&lt;button;x;yM (or m for release).
    /// .NET's ReadKey may fragment the sequence — read characters until we find 'M' or 'm'.
    /// Returns scroll keys for wheel events, empty list for other mouse events, or null
    /// if this wasn't a mouse sequence (the consumed keys are lost — acceptable trade-off).
    /// </summary>
    private static (List<ConsoleKeyInfo>? MouseKeys, ConsoleKeyInfo? LostKey) TryConsumeSgrMouseViaReadKey()
    {
        // After Escape, check if '[' follows (CSI start). Wait briefly for the next
        // byte — terminals deliver \x1b[... in pieces and a 0ms poll races the wire.
        if (!WaitForKeyAvailable(maxMs: 5))
        {
            return (null, null);
        }

        var k1 = Console.ReadKey(intercept: true);
        if (k1.KeyChar != '[' && k1.Key != ConsoleKey.Oem4)
        {
            return (null, k1); // Not a CSI — return the consumed key so it isn't lost
        }

        if (!WaitForKeyAvailable(maxMs: 5))
        {
            return (null, null); // Incomplete CSI — lost '[' is acceptable
        }

        var k2 = Console.ReadKey(intercept: true);

        // Bracketed paste mode: \x1b[200~ starts paste, \x1b[201~ ends it.
        // Pass through all characters between brackets as normal key presses.
        if (k2.KeyChar == '2')
        {
            var pasteKeys = TryConsumeBracketedPaste();
            if (pasteKeys != null)
            {
                return (pasteKeys, null);
            }

            // Not a paste sequence — the '2' and any consumed chars are lost
            return (null, null);
        }

        if (k2.KeyChar != '<')
        {
            return (null, null); // Not SGR mouse — some other CSI sequence (consumed)
        }

        // Read the SGR mouse payload: "button;x;y" terminated by 'M' or 'm'
        var payload = new System.Text.StringBuilder(16);
        var limit = 32;
        while (limit-- > 0)
        {
            if (!Console.KeyAvailable)
            {
                // Give the terminal a moment to deliver the rest of the sequence
                Thread.Sleep(5);
                if (!Console.KeyAvailable)
                {
                    break;
                }
            }

            var kn = Console.ReadKey(intercept: true);
            if (kn.KeyChar == 'M' || kn.KeyChar == 'm')
            {
                // Parse button number
                var parts = payload.ToString().Split(';');
                if (parts.Length >= 1 && int.TryParse(parts[0], out var button))
                {
                    const int scrollLines = 3;

                    // Scroll wheel up
                    if (button == 64)
                    {
                        var keys = new List<ConsoleKeyInfo>(scrollLines);
                        for (var i = 0; i < scrollLines; i++)
                        {
                            keys.Add(new ConsoleKeyInfo('k', ConsoleKey.K, false, false, false));
                        }

                        return (keys, null);
                    }

                    // Scroll wheel down
                    if (button == 65)
                    {
                        var keys = new List<ConsoleKeyInfo>(scrollLines);
                        for (var i = 0; i < scrollLines; i++)
                        {
                            keys.Add(new ConsoleKeyInfo('j', ConsoleKey.J, false, false, false));
                        }

                        return (keys, null);
                    }
                }

                return ([], null); // Other mouse event — consumed and discarded
            }

            payload.Append(kn.KeyChar);
        }

        return ([], null); // Runaway or incomplete — consumed and discarded
    }

    /// <summary>
    /// Handles bracketed paste mode sequences. Called after \x1b[2 has been consumed.
    /// Reads through 00~ to start paste, then collects all characters until \x1b[201~.
    /// Returns the paste content as a list of ConsoleKeyInfo, or null if not a paste sequence.
    /// </summary>
    private static List<ConsoleKeyInfo>? TryConsumeBracketedPaste()
    {
        return TryConsumeBracketedPasteFrom(
            tryRead: () =>
            {
                if (!WaitForKeyAvailable(maxMs: 20))
                {
                    return (false, '\0', ConsoleKey.NoName);
                }

                var k = Console.ReadKey(intercept: true);
                return (true, k.KeyChar, k.Key);
            },
            confirmTimeoutMs: 5,
            endTimeoutMs: 5);
    }

    /// <summary>
    /// Pure parser for bracketed-paste byte sequences. Takes a delegate that
    /// pulls one (KeyChar, Key) tuple at a time and returns success/timeout.
    /// Exposed internally so unit tests can synthesise the byte stream
    /// \x1b[200~&lt;content&gt;\x1b[201~ without involving Console.ReadKey.
    /// Returns null if the leading 00~ sequence does not arrive (i.e. we
    /// were called for some other CSI starting with '2'). Otherwise returns
    /// the accumulated paste content with \r and \n stripped — see
    /// PromptForInputAsync notes for why those bytes must not survive.
    /// </summary>
    /// <remarks>
    /// The two timeouts are passed through unchanged to the production path
    /// but are unused by tests, which always have a key ready.
    /// </remarks>
#pragma warning disable SA1202 // grouped with TryConsumeBracketedPaste, the only call site
    internal static List<ConsoleKeyInfo>? TryConsumeBracketedPasteFrom(
        Func<(bool Ok, char KeyChar, ConsoleKey Key)> tryRead,
        int confirmTimeoutMs = 5,
        int endTimeoutMs = 5)
    {
        _ = confirmTimeoutMs; // accepted for API parity; the delegate already encodes its own timeout
        _ = endTimeoutMs;

        // We've already consumed \x1b[2. Check for 00~ to confirm paste start.
        var confirmBuf = new char[3]; // expecting '0', '0', '~'
        for (int i = 0; i < 3; i++)
        {
            var (ok, ch, _) = tryRead();
            if (!ok)
            {
                return null;
            }

            confirmBuf[i] = ch;
        }

        if (confirmBuf[0] != '0' || confirmBuf[1] != '0' || confirmBuf[2] != '~')
        {
            return null; // Not a paste sequence
        }

        // Read paste content until we see \x1b[201~ (end bracket).
        // Pasted newlines (\r, \n) are dropped: they would otherwise terminate
        // PromptForInputAsync mid-paste and cascade through follow-up prompts.
        // The remaining content is concatenated, which is fine for JSON
        // (whitespace is not significant) and harmless for file paths.
        var pasteKeys = new List<ConsoleKeyInfo>(256);
        var limit = 16384; // safety limit for paste size

        while (limit-- > 0)
        {
            var (ok, ch, key) = tryRead();
            if (!ok)
            {
                break; // Paste ended without closing bracket — return what we have
            }

            // Check for end-of-paste: Escape starts \x1b[201~
            if (key == ConsoleKey.Escape)
            {
                // Try to consume [201~
                var endBuf = new char[5];
                var endRead = 0;
                for (int i = 0; i < 5; i++)
                {
                    var (ok2, ech, _) = tryRead();
                    if (!ok2)
                    {
                        break;
                    }

                    endBuf[i] = ech;
                    endRead++;
                }

                if (endRead == 5 && endBuf[0] == '[' && endBuf[1] == '2' &&
                    endBuf[2] == '0' && endBuf[3] == '1' && endBuf[4] == '~')
                {
                    break; // End of paste
                }

                // Not the end sequence — put the Escape back as regular char
                // (this is lossy for the consumed chars, but paste content is more important)
                continue;
            }

            // Drop newlines / carriage returns inside paste — see comment above.
            if (ch == '\r' || ch == '\n')
            {
                continue;
            }

            // Regular paste character — add to output
            if (ch >= 32 || ch == '\t')
            {
                var consoleKey = CharToConsoleKey(ch);
                pasteKeys.Add(new ConsoleKeyInfo(ch, consoleKey, false, false, false));
            }
        }

        return pasteKeys;
    }
#pragma warning restore SA1202

    /// <summary>
    /// Polls Console.KeyAvailable for up to <paramref name="maxMs"/> milliseconds.
    /// Returns true as soon as a key is ready, false if the timeout elapsed.
    /// Used to bridge the gap between bytes of a multi-byte escape sequence
    /// (mouse / bracketed-paste) so we don't decide it was a bare Escape too early.
    /// </summary>
    private static bool WaitForKeyAvailable(int maxMs)
    {
        if (Console.KeyAvailable)
        {
            return true;
        }

        var deadline = Environment.TickCount + maxMs;
        while (Environment.TickCount < deadline)
        {
            Thread.Sleep(1);
            if (Console.KeyAvailable)
            {
                return true;
            }
        }

        return Console.KeyAvailable;
    }

    /// <summary>
    /// Best-effort mapping of a char to ConsoleKey for synthetic key events.
    /// </summary>
    private static ConsoleKey CharToConsoleKey(char c)
    {
        return c switch
        {
            >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
            >= 'A' and <= 'Z' => ConsoleKey.A + (c - 'A'),
            >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
            _ => ConsoleKey.NoName,
        };
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
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);

                    // Console.ReadKey handles terminal raw mode and escape sequence parsing.
                    // Mouse SGR sequences (\x1b[<N;X;YM) and bracketed-paste markers
                    // (\x1b[200~ ... \x1b[201~) are not recognized by .NET — detect and
                    // consume them ourselves. After Escape, give the terminal up to ~10ms
                    // to deliver the rest of the sequence before deciding it was a bare Esc.
                    if (key.Key == ConsoleKey.Escape && WaitForKeyAvailable(maxMs: 10))
                    {
                        var (mouseKeys, lostKey) = TryConsumeSgrMouseViaReadKey();
                        if (mouseKeys != null)
                        {
                            foreach (var mk in mouseKeys)
                            {
                                _keyChannel.Writer.TryWrite(mk);
                            }

                            continue;
                        }

                        // Not a mouse sequence — write Escape + any consumed key back
                        _keyChannel.Writer.TryWrite(key);
                        if (lostKey.HasValue)
                        {
                            _keyChannel.Writer.TryWrite(lostKey.Value);
                        }

                        continue;
                    }

                    _keyChannel.Writer.TryWrite(key);
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
            Name = "WireCopy-KeyReader"
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

        // Animation ticks are fire-and-forget; safe to discard
        _pendingAnimationTick = null;
    }

    private void DrainChannel()
    {
        while (_keyChannel.Reader.TryRead(out _))
        {
            // Discard
        }
    }
}
