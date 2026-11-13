namespace WireCopy.Application.DTOs.Browser;

/// <summary>
/// One screen drawn only with the launcher's elements: masthead, bar, tiles, boxed half-tile pairs, hint row, footer.
/// </summary>
public sealed record TileScreen
{
    /// <summary>"routines" | "routine" | "schedule" | "addSite" | "run".</summary>
    public required string Screen { get; init; }

    /// <summary>E.g. "Routines › Morning brief".</summary>
    public required string Eyebrow { get; init; }

    public required string Title { get; init; }

    public string Tagline { get; init; } = "";

    /// <summary>Header-right, first line.</summary>
    public string CountLabel { get; init; } = "";

    /// <summary>False: tiles carry no digit badge — they cannot be jumped to.</summary>
    public bool Numbered { get; init; } = true;

    /// <summary>Header-right attention line.</summary>
    public string? Badge { get; init; }

    /// <summary>Header-right status line ("on" / "off").</summary>
    public string? Status { get; init; }

    public string FooterCount { get; init; } = "";

    /// <summary>Null = this screen has no bar.</summary>
    public string? BarPlaceholder { get; init; }

    /// <summary>Text being typed in the bar.</summary>
    public LauncherUrlTyping? BarTyping { get; init; }

    /// <summary>The line under the bar (errors, guidance).</summary>
    public string? HintRow { get; init; }

    /// <summary>Row-major grid cells.</summary>
    public required IReadOnlyList<TileCell> Cells { get; init; }

    public int Columns { get; init; } = 2;

    /// <summary>Rows per page; null = the launcher's rule. A screen that must never page sets enough rows for all cells.</summary>
    public int? RowsPerPage { get; init; }

    /// <summary>Index into the flattened selectable items (cell order; Top before Bottom); -1 = the bar.</summary>
    public int Selected { get; init; }

    /// <summary>Footer label, e.g. "Routine".</summary>
    public required string AppLabel { get; init; }

    public required IReadOnlyList<LauncherHint> Hints { get; init; }

    /// <summary>A question shown in the footer row instead of the hints.</summary>
    public PromptLineState? Confirm { get; init; }
}

/// <summary>A grid cell: one whole tile, OR a boxed pair of two half tiles with a legend, OR empty (all null).</summary>
public sealed record TileCell
{
    public Tile? Tile { get; init; }

    public Tile? Top { get; init; }

    public Tile? Bottom { get; init; }

    public string? Legend { get; init; }
}

public sealed record Tile
{
    /// <summary>Handler-defined key: "site:&lt;url&gt;", "name", "schedule", "podcast", "lastRun".</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string Hint { get; init; } = "";

    public string? Detail { get; init; }

    public string? Alert { get; init; }

    public bool Disabled { get; init; }
}
