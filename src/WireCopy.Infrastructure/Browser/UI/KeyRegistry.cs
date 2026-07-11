// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;

namespace WireCopy.Infrastructure.Browser.UI;

/// <summary>
/// Single source of truth mapping every <see cref="CommandType"/> that has a
/// keyboard binding to its canonical, human-readable key label (workspace-9k27.14).
///
/// <para>WHY THIS EXISTS: key labels used to be free string literals scattered across
/// the keybinding popup, the status-bar hint tiers, per-command status hints, and
/// docs/KEYBINDINGS.md — all hand-synced copies of the dispatch switch in
/// <see cref="TerminalInputHandler"/>. When a binding moved, the copies went stale
/// silently (the class of bug that killed Shift+L). Renderers and hints now interpolate
/// <see cref="KeyFor"/> instead of repeating the literal, and
/// <c>KeyRegistryEnforcementTests</c> asserts that every label here — and every label
/// advertised anywhere — resolves back through the REAL dispatch to a non-NoOp command.
/// A stale label is a failing test, not a shipped bug.</para>
///
/// <para>The labels are the DECLARED truth; the dispatch in
/// <see cref="TerminalInputHandler.MapKeyInfoToCommand"/> /
/// <see cref="TerminalInputHandler.MapKeyToCommandStatic"/> is the EXECUTED truth.
/// The enforcement test cross-checks the two by driving the dispatch, so they can
/// never drift apart unnoticed.</para>
/// </summary>
internal static class KeyRegistry
{
    // Canonical label per command. The label is the form a user reads AND a form the
    // dispatch actually resolves (verified by KeyRegistryEnforcementTests): a bare
    // capital ("A", "G") for keys the KeyChar switch claims, "Shift+X" / "Ctrl+X" for
    // keys the modifier switch claims, "gg" / "g l" for the goto chords, "1-9" for the
    // launcher digit jump. Composite advertisements ("j / k", "b / B") are composed by
    // callers from these atoms.
    private static readonly IReadOnlyDictionary<CommandType, string> Labels =
        new Dictionary<CommandType, string>
        {
            // Motion
            [CommandType.MoveDown] = "j",
            [CommandType.MoveUp] = "k",
            [CommandType.CollapseNode] = "h",
            [CommandType.ExpandNode] = "l",
            [CommandType.PageDown] = "Ctrl+D",
            [CommandType.PageUp] = "Ctrl+U",
            [CommandType.ParagraphUp] = "{",
            [CommandType.ParagraphDown] = "}",
            [CommandType.GoToTop] = "gg",
            [CommandType.GoToBottom] = "G",

            // Navigation
            [CommandType.ActivateLink] = "Enter",
            [CommandType.GoBack] = "b",
            [CommandType.GoForward] = "Shift+B",
            [CommandType.SwitchView] = "v",
            [CommandType.SwitchToReadable] = "r",
            [CommandType.SwitchToHierarchical] = "t",

            // Application
            [CommandType.Quit] = "q",
            [CommandType.ShowHelp] = "?",
            [CommandType.Refresh] = "F5",
            [CommandType.OpenCommandLine] = ":",

            // Search
            [CommandType.Search] = "/",
            [CommandType.SearchNext] = "n",
            [CommandType.SearchPrevious] = "N",

            // Selection
            [CommandType.ToggleSelection] = "Space",

            // Collections
            [CommandType.SaveToCollection] = "s",
            [CommandType.SaveToSpecific] = "Shift+S",
            [CommandType.SaveAllToReadingList] = "A",
            [CommandType.OpenCollections] = "c",
            [CommandType.DeleteItem] = "d",
            [CommandType.ReorderUp] = "Shift+K",
            [CommandType.ReorderDown] = "Shift+J",
            [CommandType.GeneratePodcast] = "p",
            [CommandType.ClearCollection] = "Shift+X",

            // Launcher
            [CommandType.AddBookmark] = "a",
            [CommandType.JumpToIndex] = "1-9",

            // Width control
            [CommandType.IncreaseWidth] = "]",
            [CommandType.DecreaseWidth] = "[",
            [CommandType.ResetWidth] = "0",

            // Theme
            [CommandType.CycleTheme] = "Ctrl+P",

            // Cache
            [CommandType.ForceRefresh] = "Shift+R",
            [CommandType.InteractiveRefresh] = "Shift+I",

            // Browser
            [CommandType.OpenInBrowser] = "o",
            [CommandType.ToggleBrowserDock] = "|",
            [CommandType.AdoptLensPage] = "y",
            [CommandType.TuneArticleLayout] = "Shift+E",
            [CommandType.RegenerateArticleLayout] = "e",

            // Layout
            [CommandType.ChooseLayout] = "g l",

            // Scheduling (workspace-42q8.4)
            [CommandType.AddToSchedule] = "g s",

            // Job control
            [CommandType.CancelRun] = "x",
            [CommandType.Undo] = "z",
            [CommandType.RestorePodcastModal] = "Shift+P",

            // Speed reading
            [CommandType.ToggleSpeedRead] = "f",
            [CommandType.SpeedReadFaster] = ">",
            [CommandType.SpeedReadSlower] = "<",

            // Prefetch
            [CommandType.TogglePreloadDetail] = "\\",
        };

    /// <summary>
    /// Every command that has a registered key label, for enforcement tests to iterate.
    /// </summary>
    internal static IReadOnlyDictionary<CommandType, string> All => Labels;

    /// <summary>
    /// The canonical key label for <paramref name="command"/> (e.g.
    /// <c>KeyFor(CommandType.ForceRefresh)</c> → "Shift+R"). Throws for commands
    /// that have no keyboard binding — callers only ask for bound commands.
    /// </summary>
    internal static string KeyFor(CommandType command) => Labels[command];
}
