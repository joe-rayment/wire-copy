// Educational and personal use only.

namespace TermReader.Domain.ValueObjects.Browser;

/// <summary>
/// Represents a terminal color with both 16-color and 256-color support.
/// </summary>
public readonly record struct ThemeColor(ConsoleColor ConsoleColor, byte AnsiCode)
{
    /// <summary>
    /// ANSI escape sequence for 256-color foreground.
    /// </summary>
    public string AnsiFg => $"\x1b[38;5;{AnsiCode}m";

    /// <summary>
    /// ANSI escape sequence for 256-color background.
    /// </summary>
    public string AnsiBg => $"\x1b[48;5;{AnsiCode}m";
}
