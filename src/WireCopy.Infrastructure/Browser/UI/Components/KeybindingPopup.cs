// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using static WireCopy.Infrastructure.Browser.UI.KeyRegistry;

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
        // workspace-9k27.14: key labels interpolate from KeyRegistry (the single
        // source of truth). Composite rows are composed from atoms; a few
        // deliberately-abbreviated forms — "b / B" (Shift+B shown as B), "R"/"I"
        // (Shift+R/Shift+I), "X" (Shift+X), "J / K" (Shift+J/K), "q / Ctrl+C",
        // "Esc", "1-9", ":new"/":rename" — stay literal because their display
        // intentionally differs from the canonical registry label; the enforcement
        // test still resolves every one of them against the real dispatch.
        var top = $"{KeyFor(CommandType.GoToTop)} / {KeyFor(CommandType.GoToBottom)}";
        var downUp = $"{KeyFor(CommandType.MoveDown)} / {KeyFor(CommandType.MoveUp)}";
        var readerTree = $"{KeyFor(CommandType.SwitchToReadable)} / {KeyFor(CommandType.SwitchToHierarchical)}";
        var searchNextPrev = $"{KeyFor(CommandType.SearchNext)} / {KeyFor(CommandType.SearchPrevious)}";
        return mode switch
        {
            ViewMode.Hierarchical =>
            [
                (KeyFor(CommandType.ActivateLink), "open link"),

                // workspace-5wzs: Space maps to ToggleSelection (select/deselect
                // the current link), never expand/collapse (h/l do that).
                (KeyFor(CommandType.ToggleSelection), "select / deselect item"),
                (KeyFor(CommandType.SaveToCollection), "save to reading list"),
                (KeyFor(CommandType.SaveAllToReadingList), "save all links"),
                (downUp, "down / up"),
                ($"{KeyFor(CommandType.CollapseNode)} / {KeyFor(CommandType.ExpandNode)}", "collapse / expand group"),
                (top, "top / bottom"),
                (KeyFor(CommandType.SwitchView), "switch to reader view"),
                (readerTree, "reader / tree view"),
                (KeyFor(CommandType.ChooseLayout), "set up site layout (AI)"),
                (KeyFor(CommandType.AddToSchedule), "add section to a schedule"),
                (KeyFor(CommandType.Search), "search"),
                (searchNextPrev, "next / prev match"),
                ("R", "refresh (bypass cache)"),
                ("I", "interactive refresh (login)"),
                (KeyFor(CommandType.TogglePreloadDetail), "prefetch progress panel"),
                (KeyFor(CommandType.OpenInBrowser), "open URL bar"),
                (KeyFor(CommandType.ToggleBrowserDock), "dock browser (side pane)"),
                (KeyFor(CommandType.AdoptLensPage), "open the browser's page here"),
                ("b / B", "go back / forward"),
                ("Esc", "go back"),
                (KeyFor(CommandType.OpenCommandLine), "command line"),
                (KeyFor(CommandType.CycleTheme), "cycle theme"),
                ("q / Ctrl+C", "quit"),
            ],
            ViewMode.Readable =>
            [
                (KeyFor(CommandType.SaveToCollection), "save to reading list"),
                (KeyFor(CommandType.OpenInBrowser), "open in browser"),
                (KeyFor(CommandType.ToggleBrowserDock), "dock / undock browser (side pane)"),
                (KeyFor(CommandType.AdoptLensPage), "open the browser's page here"),
                ("e / E", "regenerate / tune article layout"),
                ($"{KeyFor(CommandType.DecreaseWidth)} / {KeyFor(CommandType.IncreaseWidth)}", "narrow / widen width"),
                (KeyFor(CommandType.ResetWidth), "reset width"),
                (downUp, "scroll down / up"),
                ($"{KeyFor(CommandType.PageDown)} / {KeyFor(CommandType.PageUp)}", "page down / up"),
                (KeyFor(CommandType.ToggleSpeedRead), "speed read on/off"),
                (KeyFor(CommandType.ToggleSelection), "speed read on/off"),
                ($"{KeyFor(CommandType.SpeedReadSlower)} / {KeyFor(CommandType.SpeedReadFaster)}", "slower / faster WPM"),
                (KeyFor(CommandType.SwitchView), "switch to link view"),
                (readerTree, "reader / tree view"),
                (KeyFor(CommandType.Search), "search"),
                (searchNextPrev, "next / prev match"),
                ("R", "refresh (bypass cache)"),
                (KeyFor(CommandType.TogglePreloadDetail), "prefetch progress panel"),
                ("b / B", "go back / forward"),
                ("Esc", "go back"),
                (KeyFor(CommandType.CycleTheme), "cycle theme"),
                ("q / Ctrl+C", "quit"),
            ],
            ViewMode.CollectionList =>
            [
                (KeyFor(CommandType.ActivateLink), "open collection"),
                (KeyFor(CommandType.SaveToCollection), "set as default collection"),
                (KeyFor(CommandType.DeleteItem), "delete collection"),
                (":new", "create collection"),
                (KeyFor(CommandType.GoBack), "go back"),
                ("Esc", "go back"),
                (KeyFor(CommandType.CycleTheme), "cycle theme"),
                ("q / Ctrl+C", "quit"),
            ],
            ViewMode.CollectionItems =>
            [
                (KeyFor(CommandType.ActivateLink), "open article"),
                (KeyFor(CommandType.DeleteItem), "remove item"),
                ("X", "clear all items"),
                ("J / K", "reorder items"),
                (KeyFor(CommandType.GeneratePodcast), "generate podcast"),
                (KeyFor(CommandType.OpenCommandLine), "command line"),
                (KeyFor(CommandType.TogglePreloadDetail), "prefetch progress panel"),
                (KeyFor(CommandType.GoBack), "go back"),
                ("Esc", "go back"),
                (KeyFor(CommandType.CycleTheme), "cycle theme"),
                ("q / Ctrl+C", "quit"),
            ],
            ViewMode.Launcher =>
            [
                (KeyFor(CommandType.ActivateLink), "open bookmark"),
                (KeyFor(CommandType.JumpToIndex), "jump to bookmark"),
                (KeyFor(CommandType.OpenInBrowser), "open URL bar"),
                (KeyFor(CommandType.AddBookmark), "add bookmark"),
                (KeyFor(CommandType.DeleteItem), "delete bookmark"),
                ("J / K", "reorder bookmarks"),
                (":rename", "rename bookmark"),
                (KeyFor(CommandType.OpenCollections), "Setup / settings"),
                (KeyFor(CommandType.CycleTheme), "cycle theme"),
                ("q / Ctrl+C", "quit"),
            ],
            _ =>
            [
                (KeyFor(CommandType.ShowHelp), "show this help"),
                (KeyFor(CommandType.OpenCommandLine), "command line"),
                (top, "top / bottom"),
                (KeyFor(CommandType.CycleTheme), "cycle theme"),
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
            (":logs", "view recent logs (scroll · f level · / search · c copy · e export)"),
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
            Console.Write($"{Dim}{palette.SecondaryText.AnsiFg}Press any key to dismiss{Reset}");
        }

        return popupHeight + 1; // +1 for hint line
    }
}
