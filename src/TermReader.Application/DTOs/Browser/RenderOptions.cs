// Educational and personal use only.

namespace TermReader.Application.DTOs.Browser;

/// <summary>
/// Options for rendering content to the terminal.
/// </summary>
public record RenderOptions
{
    /// <summary>
    /// Width of the terminal window.
    /// </summary>
    public int TerminalWidth { get; init; } = 80;

    /// <summary>
    /// Height of the terminal window.
    /// </summary>
    public int TerminalHeight { get; init; } = 24;

    /// <summary>
    /// Maximum width for text content (for wrapping).
    /// </summary>
    public int MaxContentWidth { get; init; } = 80;

    /// <summary>
    /// Number of lines to reserve for status bar.
    /// </summary>
    public int StatusBarLines { get; init; } = 1;

    /// <summary>
    /// Number of lines to reserve for header.
    /// </summary>
    public int HeaderLines { get; init; } = 3;

    /// <summary>
    /// Whether to use colors in rendering.
    /// </summary>
    public bool UseColors { get; init; } = true;

    /// <summary>
    /// Theme name (for future color scheme support).
    /// </summary>
    public string Theme { get; init; } = "default";

    /// <summary>
    /// Available height for content (excluding header and status bar).
    /// </summary>
    public int ContentHeight => TerminalHeight - HeaderLines - StatusBarLines;
}
