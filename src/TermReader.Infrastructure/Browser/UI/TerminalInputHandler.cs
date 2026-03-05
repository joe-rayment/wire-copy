// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI;

/// <summary>
/// Handles keyboard input with vim-like bindings for the terminal browser.
/// </summary>
public class TerminalInputHandler : IInputHandler
{
    private readonly IThemeProvider _themeProvider;
    private readonly ILogger<TerminalInputHandler> _logger;
    private bool _waitingForSecondKey; // For 'gg' command

    public TerminalInputHandler(IThemeProvider themeProvider, ILogger<TerminalInputHandler> logger)
    {
        _themeProvider = themeProvider;
        _logger = logger;
    }

    public Task<NavigationCommand> WaitForInputAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var keyInfo = Console.ReadKey(intercept: true);

            // Handle 'gg' for go to top
            if (_waitingForSecondKey && keyInfo.Key == ConsoleKey.G)
            {
                _waitingForSecondKey = false;
                return Task.FromResult(new NavigationCommand { Type = CommandType.GoToTop });
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
                continue; // Wait for second keypress
            }

            // Character-based commands (handle before MapKeyToCommand for reliable detection)
            if (keyInfo.KeyChar == ':')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.OpenCommandLine });
            }

            if (keyInfo.KeyChar == '/')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.Search });
            }

            if (keyInfo.KeyChar == 'n')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.SearchNext });
            }

            if (keyInfo.KeyChar == 'N')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.SearchPrevious });
            }

            // Collections commands (character-based for reliable detection)
            if (keyInfo.KeyChar == 's')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.SaveToCollection });
            }

            if (keyInfo.KeyChar == 'S')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.SaveToSpecific });
            }

            if (keyInfo.KeyChar == 'd')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.DeleteItem });
            }

            if (keyInfo.KeyChar == 'a')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.AddBookmark });
            }

            if (keyInfo.KeyChar == 'c')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.OpenCollections });
            }

            // Reader view width adjustment
            if (keyInfo.KeyChar == '+' || keyInfo.KeyChar == '=')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.IncreaseWidth });
            }

            if (keyInfo.KeyChar == '-' || keyInfo.KeyChar == '_')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.DecreaseWidth });
            }

            if (keyInfo.KeyChar == '0')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.ResetWidth });
            }

            var command = MapKeyToCommand(keyInfo.Key, keyInfo.Modifiers);

            // Log non-default commands (MoveUp is the default/fallback)
            if (command.Type != CommandType.MoveUp)
            {
                _logger.LogDebug("Input: {Key} -> {CommandType}", keyInfo.Key, command.Type);
            }

            return Task.FromResult(command);
        }

        return Task.FromResult(new NavigationCommand { Type = CommandType.Quit });
    }

    public NavigationCommand MapKeyToCommand(ConsoleKey key, ConsoleModifiers modifiers)
    {
        // Ctrl+key combinations
        if ((modifiers & ConsoleModifiers.Shift) != 0)
        {
            return key switch
            {
                ConsoleKey.G => new NavigationCommand { Type = CommandType.GoToBottom }, // G (shift+g)
                ConsoleKey.J => new NavigationCommand { Type = CommandType.ReorderDown }, // J (shift+j)
                ConsoleKey.K => new NavigationCommand { Type = CommandType.ReorderUp }, // K (shift+k)
                _ => new NavigationCommand { Type = CommandType.MoveDown }
            };
        }

        if ((modifiers & ConsoleModifiers.Control) != 0)
        {
            return key switch
            {
                ConsoleKey.D => new NavigationCommand { Type = CommandType.PageDown },
                ConsoleKey.U => new NavigationCommand { Type = CommandType.PageUp },
                ConsoleKey.C => new NavigationCommand { Type = CommandType.Quit }, // Ctrl+C
                _ => new NavigationCommand { Type = CommandType.MoveDown }
            };
        }

        // Regular keys (vim-like bindings)
        return key switch
        {
            // Movement
            ConsoleKey.J => new NavigationCommand { Type = CommandType.MoveDown },
            ConsoleKey.K => new NavigationCommand { Type = CommandType.MoveUp },
            ConsoleKey.DownArrow => new NavigationCommand { Type = CommandType.MoveDown },
            ConsoleKey.UpArrow => new NavigationCommand { Type = CommandType.MoveUp },

            // Collapse/Expand
            ConsoleKey.H => new NavigationCommand { Type = CommandType.CollapseNode },
            ConsoleKey.L => new NavigationCommand { Type = CommandType.ExpandNode },
            ConsoleKey.LeftArrow => new NavigationCommand { Type = CommandType.CollapseNode },
            ConsoleKey.RightArrow => new NavigationCommand { Type = CommandType.ExpandNode },
            ConsoleKey.Spacebar => new NavigationCommand { Type = CommandType.ToggleNode },

            // Navigation
            ConsoleKey.Enter => new NavigationCommand { Type = CommandType.ActivateLink },
            ConsoleKey.B => new NavigationCommand { Type = CommandType.GoBack },
            ConsoleKey.Backspace => new NavigationCommand { Type = CommandType.GoBack },

            // View switching
            ConsoleKey.V => new NavigationCommand { Type = CommandType.SwitchView },
            ConsoleKey.Tab => new NavigationCommand { Type = CommandType.SwitchView },
            ConsoleKey.R => new NavigationCommand { Type = CommandType.SwitchToReadable },
            ConsoleKey.T => new NavigationCommand { Type = CommandType.SwitchToHierarchical },

            // Application
            ConsoleKey.Q => new NavigationCommand { Type = CommandType.Quit },
            ConsoleKey.Escape => new NavigationCommand { Type = CommandType.GoBack },
            ConsoleKey.F5 => new NavigationCommand { Type = CommandType.Refresh },
            ConsoleKey.F6 => new NavigationCommand { Type = CommandType.CycleTheme },

            // Help
            ConsoleKey.Oem2 => new NavigationCommand { Type = CommandType.ShowHelp }, // '?' key

            // Default: just return a no-op
            _ => new NavigationCommand { Type = CommandType.MoveDown }
        };
    }

    public string GetHelpText()
    {
        return @"
╔════════════════════════════════════════════════════════════════╗
║                    Terminal Browser Help                        ║
╚════════════════════════════════════════════════════════════════╝

  Navigation (Vim-style)
  ─────────────────────────────────────────────────────────────────
  j / ↓         Move down (next link / scroll)
  k / ↑         Move up (previous link / scroll)
  h / ←         Collapse node / Go back
  l / →         Expand node / Go forward
  Enter         Follow selected link
  Space         Toggle expand/collapse
  b / Backspace Go back in history

  Scrolling
  ─────────────────────────────────────────────────────────────────
  Ctrl+d        Page down
  Ctrl+u        Page up
  gg            Go to top
  G (Shift+g)   Go to bottom

  View Modes
  ─────────────────────────────────────────────────────────────────
  v / Tab       Toggle between Link View and Reader View
  r             Switch to Reader View
  t             Switch to Link Tree View

  Command Line
  ─────────────────────────────────────────────────────────────────
  :             Open command line (enter URL or command)
                Commands: open URL, go URL, back, forward, quit, help

  Search
  ─────────────────────────────────────────────────────────────────
  /             Search in current view
  n             Next search match
  N             Previous search match

  Launcher (Home Screen)
  ─────────────────────────────────────────────────────────────────
  h/j/k/l       Navigate bookmark grid (left/down/up/right)
  Enter         Open selected bookmark
  a             Add new bookmark
  d             Delete selected bookmark
  c             Open collections
  :home         Return to launcher from any view

  Reader View
  ─────────────────────────────────────────────────────────────────
  + / =         Increase content width
  - / _         Decrease content width
  0             Reset content width to default

  Collections
  ─────────────────────────────────────────────────────────────────
  s             Save link to default collection
  S             Save link to specific collection
  d             Delete item (in collection views)
  J (Shift+j)   Move item down (in collection items)
  K (Shift+k)   Move item up (in collection items)
  :collections  Open collections view
  :readlater    Open read later collection

  Application
  ─────────────────────────────────────────────────────────────────
  F5            Refresh current page
  F6            Cycle color theme
  Esc           Go back (quit from launcher)
  q             Quit browser
  ?             Show this help

  Press any key to continue...
";
    }

    public async Task<string?> PromptForUrlAsync(string prompt = "Enter URL: ", CancellationToken cancellationToken = default)
    {
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

    public Task<string?> PromptForInputAsync(string prompt, CancellationToken cancellationToken = default)
    {
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

            // Read input character by character
            var input = new System.Text.StringBuilder();
            while (!cancellationToken.IsCancellationRequested)
            {
                var keyInfo = Console.ReadKey(intercept: true);

                if (keyInfo.Key == ConsoleKey.Escape)
                {
                    return Task.FromResult<string?>(null);
                }

                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    var result = input.ToString();
                    return Task.FromResult<string?>(string.IsNullOrWhiteSpace(result) ? null : result);
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
        catch
        {
            // Fallback if cursor positioning fails
        }

        return Task.FromResult<string?>(null);
    }
}
