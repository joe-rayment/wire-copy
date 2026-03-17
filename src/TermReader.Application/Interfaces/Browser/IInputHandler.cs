// Educational and personal use only.

using TermReader.Application.DTOs.Browser;

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Service for handling keyboard input with vim-like bindings.
/// </summary>
public interface IInputHandler
{
    /// <summary>
    /// Waits for user input and returns the corresponding command.
    /// Blocks until input is received.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Navigation command based on the key pressed.</returns>
    Task<NavigationCommand> WaitForInputAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps a key to a command.
    /// </summary>
    /// <param name="key">Console key pressed.</param>
    /// <param name="modifiers">Key modifiers (Ctrl, Shift, Alt).</param>
    /// <returns>Corresponding navigation command.</returns>
    NavigationCommand MapKeyToCommand(ConsoleKey key, ConsoleModifiers modifiers);

    /// <summary>
    /// Gets help text showing all key bindings.
    /// </summary>
    string GetHelpText();

    /// <summary>
    /// Prompts user for URL input.
    /// </summary>
    /// <param name="prompt">Prompt message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>User-entered URL.</returns>
    Task<string?> PromptForUrlAsync(string prompt = "Enter URL: ", CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts user for inline text input at the bottom of the screen.
    /// Used for command mode (:) and search (/).
    /// </summary>
    /// <param name="prompt">Prompt character (e.g. ":" or "/").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="isSecret">When true, echoes '*' instead of the actual character.</param>
    /// <returns>User-entered text, or null if cancelled (Escape).</returns>
    Task<string?> PromptForInputAsync(string prompt, CancellationToken cancellationToken = default, bool isSecret = false, int? row = null, int? col = null);
}
