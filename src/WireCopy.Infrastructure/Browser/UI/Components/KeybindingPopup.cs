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
                ("j / k", "down / up"),
                ("h / l", "collapse / expand group"),
                ("gg / G", "top / bottom"),
                ("v", "switch to reader view"),
                ("r / t", "reader / tree view"),
                ("g l", "set up site layout (AI)"),
                ("/", "search"),
                ("n / N", "next / prev match"),
                ("R", "refresh (bypass cache)"),
                ("I", "interactive refresh (login)"),
                ("\\", "prefetch progress panel"),
                ("o", "open URL bar"),
                ("|", "dock browser (side pane)"),
                ("y", "open the browser's page here"),
                ("b / B", "go back / forward"),
                (":", "command line"),
                ("q", "quit"),
            ],
            ViewMode.Readable =>
            [
                ("s", "save to reading list"),
                ("o", "open in browser"),
                ("|", "dock / undock browser (side pane)"),
                ("y", "open the browser's page here"),
                ("e / E", "regenerate / tune article layout"),
                ("[ / ]", "narrow / widen width"),
                ("0", "reset width"),
                ("j / k", "scroll down / up"),
                ("Ctrl+D / Ctrl+U", "page down / up"),
                ("f", "speed read on/off"),
                ("< / >", "slower / faster WPM"),
                ("v", "switch to link view"),
                ("r / t", "reader / tree view"),
                ("/", "search"),
                ("n / N", "next / prev match"),
                ("R", "refresh (bypass cache)"),
                ("\\", "prefetch progress panel"),
                ("b / B", "go back / forward"),
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
                ("\\", "prefetch progress panel"),
                ("b", "go back"),
                ("q", "quit"),
            ],
            ViewMode.Launcher =>
            [
                ("Enter", "open bookmark"),
                ("1-9", "jump to bookmark"),
                ("o", "open URL bar"),
                ("a", "add bookmark"),
                ("d", "delete bookmark"),
                ("J / K", "reorder bookmarks"),
                (":rename", "rename bookmark"),
                ("c", "Setup / settings"),
                ("Ctrl+P", "cycle theme"),
                ("q", "quit"),
            ],
            _ =>
            [
                ("?", "show this help"),
                (":", "command line"),
                ("gg / G", "top / bottom"),
                ("Ctrl+P", "cycle theme"),
                ("q / Ctrl+C", "quit"),
            ],
        };
    }

    /// <summary>
    /// workspace-syj1.4 — the ':' command-line reference shown by ':help', so typing
    /// ':help' documents the colon commands rather than the keybinding popup ('?').
    /// Verb, argument shape, one-line description.
    /// </summary>
    public static (string Key, string Description)[] GetCommandLineBindings()
    {
        return
        [
            (":open <url>", "open a URL (also :go, :o)"),
            (":back / :forward", "history (also :b, :f)"),
            (":home", "back to the launcher"),
            (":add", "add a bookmark"),
            (":rename <name>", "rename bookmark / collection"),
            (":collections", "open reading list (also :readlater)"),
            (":new <name>", "create a collection"),
            (":export [format]", "export the open collection"),
            (":podcast", "generate / restore a podcast"),
            (":schedules", "recurring podcast schedules"),
            (":settings", "podcast settings"),
            (":config", "settings screen"),
            (":set apikey|bucket|key", "set a credential/setting"),
            (":clear apikey|bucket|key", "clear a credential/setting"),
            (":cred", "site login credentials"),
            (":cookies", "cookie import / status"),
            (":cache [info|clear]", "page cache stats / clear"),
            (":layout", "site layout chooser (Ctrl+L)"),
            (":reanalyze", "fresh AI layout analysis"),
            (":dump", "dump raw HTML to fixtures/"),
            (":help", "this reference · ? key hints"),
            (":q", "quit"),
        ];
    }

    /// <summary>
    /// Renders the keybinding popup at the bottom of the terminal.
    /// Returns the number of lines used by the popup (for cursor restoration).
    /// </summary>
    public static int Render(ViewMode mode, ThemePalette palette, int terminalWidth, int terminalHeight)
        => RenderCore(GetBindings(mode), StatusBarRenderer.GetModeLabel(mode), palette, terminalWidth, terminalHeight);

    /// <summary>
    /// workspace-syj1.4 — renders the ':' command reference popup (same chrome as the
    /// keybinding popup, command-line content). Returns the number of lines used.
    /// </summary>
    public static int RenderCommandReference(ThemePalette palette, int terminalWidth, int terminalHeight)
        => RenderCore(GetCommandLineBindings(), "Command line", palette, terminalWidth, terminalHeight);

    /// <summary>
    /// Measures the inner content width needed so the longest row
    /// (key column + gap + description) and the top-border title both fit.
    /// Clamped to a 30-column floor and the terminal width minus margins;
    /// when the terminal is narrower than the content, descriptions are
    /// truncated at render time rather than overflowing the border.
    /// </summary>
    internal static int ComputeInnerWidth(
        (string Key, string Description)[] bindings, string title, int maxKeyWidth, int terminalWidth)
    {
        var needed = 0;
        foreach (var (_, desc) in bindings)
        {
            needed = Math.Max(needed, maxKeyWidth + 1 + RenderHelpers.GetDisplayWidth(desc));
        }

        // Top border is ┌─<title padded><dashes>┐ spanning innerWidth + 2 between
        // the corners; keep at least one trailing dash after the title.
        needed = Math.Max(needed, RenderHelpers.GetDisplayWidth($" {title} ") + 1);

        return Math.Clamp(needed, 30, Math.Max(30, terminalWidth - 4));
    }

    /// <summary>
    /// Builds the visible (ANSI-free) content of each binding row, padded to
    /// exactly <paramref name="innerWidth"/> columns. Descriptions that don't
    /// fit are ellipsis-truncated so no row can exceed the border width.
    /// </summary>
    internal static (string KeyPadded, string Description, int Padding)[] BuildRows(
        (string Key, string Description)[] bindings, int maxKeyWidth, int innerWidth)
    {
        var rows = new (string, string, int)[bindings.Length];
        for (var i = 0; i < bindings.Length; i++)
        {
            var (key, desc) = bindings[i];
            var keyPadded = key + new string(' ', Math.Max(0, maxKeyWidth + 1 - RenderHelpers.GetDisplayWidth(key)));
            var fitted = RenderHelpers.TruncateText(desc, Math.Max(0, innerWidth - maxKeyWidth - 1));
            var contentWidth = maxKeyWidth + 1 + RenderHelpers.GetDisplayWidth(fitted);
            rows[i] = (keyPadded, fitted, Math.Max(0, innerWidth - contentWidth));
        }

        return rows;
    }

    private static int RenderCore((string Key, string Description)[] bindings, string title, ThemePalette palette, int terminalWidth, int terminalHeight)
    {
        // Calculate popup dimensions
        var maxKeyWidth = 0;
        foreach (var (key, _) in bindings)
        {
            var keyWidth = RenderHelpers.GetDisplayWidth(key);
            if (keyWidth > maxKeyWidth)
            {
                maxKeyWidth = keyWidth;
            }
        }

        var innerWidth = ComputeInnerWidth(bindings, title, maxKeyWidth, terminalWidth);

        // Clip bindings to fit terminal height
        var maxBindings = Math.Max(2, terminalHeight - 4); // 4 = borders + hint + status bar
        if (bindings.Length > maxBindings)
        {
            bindings = bindings[..maxBindings];
        }

        var popupHeight = bindings.Length + 2; // +2 for top/bottom borders
        var startRow = Math.Max(0, terminalHeight - popupHeight - 1); // -1 for status bar

        // workspace-s621: center within the dock-aware viewport (terminalWidth is
        // already the uncovered width; the origin shifts under a left dock).
        var startCol = OverlayViewport.Left + Math.Max(0, (terminalWidth - innerWidth - 2) / 2);

        var borderColor = $"{palette.SecondaryText.AnsiFg}";
        var keyColor = $"{palette.GetAccentFg().AnsiFg}";
        var descColor = $"{palette.PrimaryText.AnsiFg}";
        var titleColor = $"{palette.StatusBarTextFg.AnsiFg}";

        // Top border with title
        // Content rows are: │ + space + innerWidth + space + │ = innerWidth + 4 visible chars
        // So top border must span innerWidth + 2 dashes between ┌ and ┐
        var totalDashes = innerWidth + 2; // dashes between ┌ and ┐
        var titlePadded = $" {RenderHelpers.TruncateText(title, Math.Max(1, totalDashes - 4))} ";
        var topBorderWidth = Math.Max(0, totalDashes - RenderHelpers.GetDisplayWidth(titlePadded) - 1);
        var topLeft = new string('\u2500', 1);
        var topRight = new string('\u2500', topBorderWidth);
        var topLine = $"{borderColor}\u250c{topLeft}{Reset}{titleColor}{titlePadded}{Reset}{borderColor}{topRight}\u2510{Reset}";

        Console.SetCursorPosition(startCol, startRow);
        Console.Write(topLine);

        // Binding rows
        var rows = BuildRows(bindings, maxKeyWidth, innerWidth);
        for (var i = 0; i < rows.Length; i++)
        {
            var (keyPadded, desc, rowPadding) = rows[i];

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
