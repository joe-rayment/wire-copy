// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Browser;

/// <summary>
/// Serialisable view-model of the launcher screen, exported over the shell
/// channel (method <c>launcherState</c>) so the desktop shell can render the
/// launcher as an HTML pane (workspace-0jxo / epic workspace-9hh1). This is the
/// SAME slot composition the TUI grid renders — <see cref="LauncherSlot"/> facts
/// come from the one renderer-side builder — projected into a paging window of
/// <see cref="Columns"/> × 3 tiles ("Launcher Responsive.dc.html": the grid
/// flows 1..5 columns with the window width).
/// Field names are camelCase on the wire (the channel serialises with
/// <see cref="System.Text.Json.JsonSerializerDefaults.Web"/>).
/// </summary>
public sealed record LauncherViewState
{
    /// <summary>The VISIBLE window only (≤6), in grid order.</summary>
    public required IReadOnlyList<LauncherSlot> Slots { get; init; }

    /// <summary>Index into <see cref="Slots"/> of the selected tile; -1 = URL bar focused.</summary>
    public int SelectedIndex { get; init; }

    /// <summary>
    /// Column count of the pane's responsive grid (1..5, "Launcher Responsive
    /// .dc.html"). Computed renderer-side from the terminal width and ALSO used
    /// for arrow-key movement in shell mode, so the pane must lay the slots out
    /// in exactly this many columns for keys to match the screen.
    /// </summary>
    public int Columns { get; init; } = 2;

    /// <summary>The URL bar's typed buffer + cursor while go-to-URL input is active; null otherwise.</summary>
    public LauncherUrlTyping? UrlTyping { get; init; }

    /// <summary>The ':' / '/' prompt line while one is active over this pane (workspace-up3zx); null otherwise.</summary>
    public PromptLineState? PromptLine { get; init; }

    /// <summary>The "add API keys" setup-hint copy when shown; null otherwise.</summary>
    public string? SetupHint { get; init; }

    /// <summary>The paging window: which slice of the full slot list is visible.</summary>
    public required LauncherPaging Paging { get; init; }

    /// <summary>Total bookmark count (the right-aligned footer count).</summary>
    public int BookmarkCount { get; init; }

    /// <summary>Footer auxiliary line: scheduled-run badge and/or transient status.</summary>
    public required LauncherFooter Footer { get; init; }

    /// <summary>The tagline string the TUI renders (e.g. "All copy, no nonsense").</summary>
    public required string Tagline { get; init; }

    /// <summary>The version string the TUI renders (e.g. "v1.0").</summary>
    public required string Version { get; init; }
}

/// <summary>
/// One launcher tile. Bookmark tiles carry a <see cref="Url"/> and a domain
/// <see cref="Hint"/>; the Reading List tile carries no URL, a saved-articles
/// <see cref="Hint"/>, and <see cref="Star"/> = true; padding cells past the end
/// of the list are <c>kind = "empty"</c>.
/// </summary>
public sealed record LauncherSlot
{
    /// <summary>Global virtual slot index (the digit-jump address; Reading List = 1).</summary>
    public int Index { get; init; }

    /// <summary>"bookmark", "readingList", or "empty".</summary>
    public required string Kind { get; init; }

    /// <summary>Display title as the TUI renders it (bookmark names are upper-cased); null for empty.</summary>
    public string? Title { get; init; }

    /// <summary>Bookmark href; null for the Reading List and empty tiles.</summary>
    public string? Url { get; init; }

    /// <summary>Subtitle line: a bookmark's domain, or the Reading List's saved-articles copy; null for empty.</summary>
    public string? Hint { get; init; }

    /// <summary>True for the Reading List tile (the ★ marker).</summary>
    public bool Star { get; init; }

    /// <summary>Digit-jump badge ("1".."9") for the first nine slots; null past slot 9 and for empty tiles.</summary>
    public string? Badge { get; init; }
}

/// <summary>The URL bar's in-progress typed buffer and cursor position.</summary>
public sealed record LauncherUrlTyping
{
    public required string Buffer { get; init; }

    public int Cursor { get; init; }
}

/// <summary>The paging window over the full slot list.</summary>
public sealed record LauncherPaging
{
    /// <summary>Global virtual index of the first slot in the window.</summary>
    public int Offset { get; init; }

    /// <summary>Number of real (non-empty) tiles in the window.</summary>
    public int Visible { get; init; }

    /// <summary>Total number of real tiles (bookmarks + Reading List slot).</summary>
    public int Total { get; init; }
}

/// <summary>The launcher footer's auxiliary line.</summary>
public sealed record LauncherFooter
{
    /// <summary>Scheduled-run badge text, or null.</summary>
    public string? Badge { get; init; }

    /// <summary>Transient status message, or null.</summary>
    public string? Status { get; init; }
}
