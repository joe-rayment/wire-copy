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

            if (keyInfo.KeyChar == '?')
            {
                return Task.FromResult(new NavigationCommand { Type = CommandType.ShowHelp });
            }

            var command = MapKeyToCommand(keyInfo.Key, keyInfo.Modifiers);

            // Log actionable commands (skip NoOp)
            if (command.Type != CommandType.NoOp)
            {
                _logger.LogDebug("Input: {Key} -> {CommandType}", keyInfo.Key, command.Type);
            }

            return Task.FromResult(command);
        }

        return Task.FromResult(new NavigationCommand { Type = CommandType.Quit });
    }

    public NavigationCommand MapKeyToCommand(ConsoleKey key, ConsoleModifiers modifiers)
    {
        // Shift+key combinations
        if ((modifiers & ConsoleModifiers.Shift) != 0)
        {
            return key switch
            {
                ConsoleKey.G => new NavigationCommand { Type = CommandType.GoToBottom }, // G (shift+g)
                ConsoleKey.J => new NavigationCommand { Type = CommandType.ReorderDown }, // J (shift+j)
                ConsoleKey.K => new NavigationCommand { Type = CommandType.ReorderUp }, // K (shift+k)
                _ => new NavigationCommand { Type = CommandType.NoOp }
            };
        }

        if ((modifiers & ConsoleModifiers.Control) != 0)
        {
            return key switch
            {
                ConsoleKey.D => new NavigationCommand { Type = CommandType.PageDown },
                ConsoleKey.U => new NavigationCommand { Type = CommandType.PageUp },
                ConsoleKey.P => new NavigationCommand { Type = CommandType.CycleTheme }, // Ctrl+P
                ConsoleKey.C => new NavigationCommand { Type = CommandType.Quit }, // Ctrl+C
                _ => new NavigationCommand { Type = CommandType.NoOp }
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

            // Help
            ConsoleKey.Oem2 => new NavigationCommand { Type = CommandType.ShowHelp }, // '?' key

            // Default: no-op for unrecognized keys
            _ => new NavigationCommand { Type = CommandType.NoOp }
        };
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

 Collections
   s               Save to default collection
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
        catch (IOException)
        {
            // Fallback if cursor positioning fails
        }
        catch (InvalidOperationException)
        {
            // Fallback if console operations fail
        }

        return Task.FromResult<string?>(null);
    }
}
