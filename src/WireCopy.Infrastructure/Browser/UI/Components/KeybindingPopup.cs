// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Renders a Helix-style keybinding popup overlay anchored to the bottom
/// of the screen. Shows context-sensitive keybindings for the current view mode.
/// The popup paints over existing content without destroying it — the caller
/// re-renders the page after dismissal.
/// </summary>
internal static class KeybindingPopup
{
    private const string Reset = "\x1b[0m";
    private const string Dim = "\x1b[2m";

    /// <summary>
    /// Gets the keybindings to display for a given view mode.
    /// Returns the full set of bindings, not the abbreviated hint tiers.
    /// </summary>
    public static (string Key, string Description)[] GetBindings(ViewMode mode)
    {
        return mode switch
        {
            ViewMode.Hierarchical =>
            [
                ("Enter", "open link"),
                ("Space", "toggle expand/collapse"),
                ("s", "save to reading list"),
                ("A", "save all links"),
                ("h / l", "collapse / expand group"),
                ("v", "switch to reader view"),
                ("r / t", "reader / tree view"),
                ("/", "search"),
                ("n / N", "next / prev match"),
                ("R", "refresh (bypass cache)"),
                ("I", "interactive refresh (login)"),
                ("o", "open URL bar"),
                ("w", "dock / undock browser (right)"),
                ("b", "go back"),
                ("q", "quit"),
            ],
            ViewMode.Readable =>
            [
                ("s", "save to reading list"),
                ("o", "open in browser"),
                ("w", "dock / undock browser (right)"),
                ("[ / ]", "narrow / widen width"),
                ("0", "reset width"),
                ("f", "speed read on/off"),
                ("< / >", "slower / faster WPM"),
                ("v", "switch to link view"),
                ("r / t", "reader / tree view"),
                ("/", "search"),
                ("n / N", "next / prev match"),
                ("R", "refresh (bypass cache)"),
                ("b", "go back"),
                ("q", "quit"),
            ],
            ViewMode.CollectionList =>
            [
                ("Enter", "open collection"),
                ("d", "delete collection"),
                (":new", "create collection"),
                ("b", "go back"),
                ("q", "quit"),
            ],
            ViewMode.CollectionItems =>
            [
                ("Enter", "open article"),
                ("d", "remove item"),
                ("X", "clear all items"),
                ("J / K", "reorder items"),
                ("p", "generate podcast"),
                (":", "command line"),
                ("b", "go back"),
                ("q", "quit"),
            ],
            ViewMode.Launcher =>
            [
                ("Enter", "open bookmark"),
                ("o", "open URL bar"),
                ("a", "add bookmark"),
                ("d", "delete bookmark"),
                ("J / K", "reorder bookmarks"),
                (":rename", "rename bookmark"),
                ("c", "Setup / settings"),
                ("Ctrl+p", "cycle theme"),
                ("q", "quit"),
            ],
            _ =>
            [
                ("?", "show this help"),
                ("q", "quit"),
            ],
        };
    }

    /// <summary>
    /// Renders the keybinding popup at the bottom of the terminal.
    /// Returns the number of lines used by the popup (for cursor restoration).
    /// </summary>
    public static int Render(ViewMode mode, ThemePalette palette, int terminalWidth, int terminalHeight)
    {
        var bindings = GetBindings(mode);
        var title = StatusBarRenderer.GetModeLabel(mode);

        // Calculate popup dimensions
        var maxKeyWidth = 0;
        foreach (var (key, _) in bindings)
        {
            if (key.Length > maxKeyWidth)
            {
                maxKeyWidth = key.Length;
            }
        }

        var innerWidth = Math.Clamp(maxKeyWidth + 25, 30, Math.Max(30, terminalWidth - 4));

        // Clip bindings to fit terminal height
        var maxBindings = Math.Max(2, terminalHeight - 4); // 4 = borders + hint + status bar
        if (bindings.Length > maxBindings)
        {
            bindings = bindings[..maxBindings];
        }

        var popupHeight = bindings.Length + 2; // +2 for top/bottom borders
        var startRow = Math.Max(0, terminalHeight - popupHeight - 1); // -1 for status bar
        var startCol = Math.Max(0, (terminalWidth - innerWidth - 2) / 2); // center horizontally

        var borderColor = $"{palette.SecondaryText.AnsiFg}";
        var keyColor = $"{palette.GetAccentFg().AnsiFg}";
        var descColor = $"{palette.PrimaryText.AnsiFg}";
        var titleColor = $"{palette.StatusBarTextFg.AnsiFg}";

        // Top border with title
        // Content rows are: │ + space + innerWidth + space + │ = innerWidth + 4 visible chars
        // So top border must span innerWidth + 2 dashes between ┌ and ┐
        var titlePadded = $" {title} ";
        var totalDashes = innerWidth + 2; // dashes between ┌ and ┐
        var topBorderWidth = Math.Max(0, totalDashes - titlePadded.Length - 1);
        var topLeft = new string('\u2500', 1);
        var topRight = new string('\u2500', topBorderWidth);
        var topLine = $"{borderColor}\u250c{topLeft}{Reset}{titleColor}{titlePadded}{Reset}{borderColor}{topRight}\u2510{Reset}";

        Console.SetCursorPosition(startCol, startRow);
        Console.Write(topLine);

        // Binding rows
        for (var i = 0; i < bindings.Length; i++)
        {
            var (key, desc) = bindings[i];
            var keyPadded = key.PadRight(maxKeyWidth + 1);
            var contentWidth = maxKeyWidth + 1 + desc.Length;
            var rowPadding = Math.Max(0, innerWidth - contentWidth);

            var row = startRow + 1 + i;
            if (row >= terminalHeight - 1)
            {
                break; // Don't write past the terminal
            }

            Console.SetCursorPosition(startCol, row);
            Console.Write(
                $"{borderColor}\u2502{Reset} {keyColor}{keyPadded}{Reset}{descColor}{desc}{Reset}{new string(' ', rowPadding)} {borderColor}\u2502{Reset}");
        }

        // Bottom border
        var bottomRow = startRow + 1 + bindings.Length;
        if (bottomRow < terminalHeight - 1)
        {
            var bottomLine = $"{borderColor}\u2514{new string('\u2500', innerWidth + 2)}\u2518{Reset}";
            Console.SetCursorPosition(startCol, bottomRow);
            Console.Write(bottomLine);
        }

        // Hint below popup (if space available)
        var hintRow = startRow + 2 + bindings.Length;
        if (hintRow < terminalHeight - 1)
        {
            Console.SetCursorPosition(startCol, hintRow);
            Console.Write($"{Dim}{palette.SecondaryText.AnsiFg}press any key to dismiss{Reset}");
        }

        return popupHeight + 1; // +1 for hint line
    }
}
