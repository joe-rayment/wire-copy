// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

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
