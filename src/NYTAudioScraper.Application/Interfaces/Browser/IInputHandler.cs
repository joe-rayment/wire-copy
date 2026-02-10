// Educational and personal use only.

using NYTAudioScraper.Application.DTOs.Browser;

namespace NYTAudioScraper.Application.Interfaces.Browser;

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
}
