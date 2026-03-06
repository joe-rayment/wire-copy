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

    public TerminalInputHandler(IThemeProvider themeProvider, IResizeDetector resizeDetector, ILogger<TerminalInputHandler> logger)
    {
        _themeProvider = themeProvider;
        _resizeDetector = resizeDetector;
        _logger = logger;
    }

    public async Task<NavigationCommand> WaitForInputAsync(CancellationToken cancellationToken = default)
    {
        EnsureKeyReaderStarted();

        while (!cancellationToken.IsCancellationRequested)
        {
            // Race keyboard channel against resize channel
            var keyTask = _keyChannel.Reader.ReadAsync(cancellationToken).AsTask();
            var resizeTask = _resizeDetector.Resizes.ReadAsync(cancellationToken).AsTask();

            var completed = await Task.WhenAny(keyTask, resizeTask).ConfigureAwait(false);

            if (completed == resizeTask)
            {
                // Drain any additional resize events (coalesce rapid resizes)
                while (_resizeDetector.Resizes.TryRead(out _))
                {
                    // Intentionally empty: discard queued resize signals to coalesce into one
                }

                return new NavigationCommand { Type = CommandType.TerminalResized };
            }

            var keyInfo = await keyTask.ConfigureAwait(false);

            // Handle 'gg' for go to top
            if (_waitingForSecondKey && keyInfo.Key == ConsoleKey.G)
            {
                _waitingForSecondKey = false;
                return new NavigationCommand { Type = CommandType.GoToTop };
            }

            // If we were waiting for 'g' but got something else, reset
            if (_waitingForSecondKey)
            {
                _waitingForSecondKey = false;
            }

            // Start waiting for second 'g' for 'gg' command
            if (keyInfo.Key == ConsoleKey.G && (keyInfo.Modifiers & ConsoleModifiers.Shift) == 0)
            {
                _waitingForSecondKey = true;
                continue;
            }

            var command = MapKeyInfoToCommand(keyInfo);
            if (command.Type != CommandType.NoOp)
            {
                _logger.LogDebug("Input: {Key} -> {CommandType}", keyInfo.Key, command.Type);
            }

            return command;
        }

        return new NavigationCommand { Type = CommandType.Quit };
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

 Navigation
   j / ↓           Move down / scroll
   k / ↑           Move up / scroll
   h / ←           Collapse node
   l / →           Expand node
   Enter           Follow link / open item
   Space           Toggle expand/collapse
   b / Backspace   Go back

 Scrolling
   Ctrl+d          Page down
   Ctrl+u          Page up
   gg              Go to top
   G               Go to bottom

 Views
   v / Tab         Toggle Link View ↔ Reader View
   r               Reader View
   t               Link Tree View

 Reader Width
   + / =           Widen       - / _           Narrow
   0               Reset

 Search & Commands
   /               Search
   n / N           Next / previous match
   :               Command line

 Launcher
   h/j/k/l         Navigate grid
   a               Add bookmark
   d               Delete bookmark
   c               Reading list
   :home           Return to launcher

 Reading List
   s               Save to Reading List
   A               Save all links to Reading List
   S               Save to specific collection
   d               Delete / remove item
   J / K           Reorder items

 General
   F5              Refresh          Ctrl+p          Cycle theme
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

    public async Task<string?> PromptForInputAsync(string prompt, CancellationToken cancellationToken = default)
    {
        EnsureKeyReaderStarted();

        try
        {
            // Position at the bottom of the terminal
            var row = Console.WindowHeight - 1;
            Console.SetCursorPosition(0, row);

            // Clear the line and show prompt
            Console.Write(new string(' ', Console.WindowWidth - 1));
            Console.SetCursorPosition(0, row);
            var palette = BuiltInThemes.Get(_themeProvider.CurrentTheme);
            Console.Write(palette.PromptFg.AnsiFg);
            Console.Write(prompt);
            Console.Write("\x1b[0m");

            // Read input from the key channel
            var input = new System.Text.StringBuilder();
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
                        Console.SetCursorPosition(prompt.Length, row);
                        Console.Write(input.ToString() + " ");
                        Console.SetCursorPosition(prompt.Length + input.Length, row);
                    }

                    continue;
                }

                // Printable character
                if (keyInfo.KeyChar >= 32)
                {
                    input.Append(keyInfo.KeyChar);
                    Console.Write(keyInfo.KeyChar);
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
            'a' => new NavigationCommand { Type = CommandType.AddBookmark },
            'c' => new NavigationCommand { Type = CommandType.OpenCollections },
            '+' or '=' => new NavigationCommand { Type = CommandType.IncreaseWidth },
            '-' or '_' => new NavigationCommand { Type = CommandType.DecreaseWidth },
            '0' => new NavigationCommand { Type = CommandType.ResetWidth },
            '?' => new NavigationCommand { Type = CommandType.ShowHelp },
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
            ConsoleKey.Spacebar => new NavigationCommand { Type = CommandType.ToggleNode },
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

    private void EnsureKeyReaderStarted()
    {
        if (_keyReaderStarted)
        {
            return;
        }

        _keyReaderStarted = true;

        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    _keyChannel.Writer.TryWrite(key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Key reader thread terminated");
                _keyChannel.Writer.TryComplete();
            }
        })
        {
            IsBackground = true,
            Name = "TermReader-KeyReader"
        };
        thread.Start();
    }
}
