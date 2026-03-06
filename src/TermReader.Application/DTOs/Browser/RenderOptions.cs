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
    /// Matches actual status bar rendering: blank + separator + status text.
    /// </summary>
    public int StatusBarLines { get; init; } = 3;

    /// <summary>
    /// Number of lines to reserve for header.
    /// Matches actual header rendering: blank + box top + title + url + box bottom + blank.
    /// </summary>
    public int HeaderLines { get; init; } = 6;

    /// <summary>
    /// Whether the terminal supports 256-color mode.
    /// Detected from COLORTERM environment variable.
    /// </summary>
    public bool Use256Colors { get; init; }

    /// <summary>
    /// Available height for content (excluding header and status bar).
    /// </summary>
    public int ContentHeight => TerminalHeight - HeaderLines - StatusBarLines;

    /// <summary>
    /// Set of normalized URLs currently in the page cache.
    /// Used by renderers to show pre-load/cache indicators on link tree items.
    /// </summary>
    public IReadOnlySet<string>? CachedUrls { get; init; }
}
