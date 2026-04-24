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
    /// Matches actual status bar rendering: line 1 + line 2.
    /// </summary>
    public int StatusBarLines { get; init; } = 2;

    /// <summary>
    /// Number of lines to reserve for header.
    /// Matches actual header rendering: title + thin rule.
    /// </summary>
    public int HeaderLines { get; init; } = 2;

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

    /// <summary>
    /// Current pre-load progress for showing in the status bar.
    /// </summary>
    public PreloadProgress? CacheProgress { get; init; }

    /// <summary>
    /// Visual state of the podcast CTA button (0=Idle, 1=Pressed, 2=Disabled, 3=Unconfigured).
    /// Mapped to PodcastCtaState enum in the rendering layer.
    /// </summary>
    public int PodcastButtonState { get; init; }

    /// <summary>
    /// Memory cache usage percentage (0-100). Used by the status bar to show
    /// a warning indicator when the cache is nearly full.
    /// </summary>
    public double CacheUsagePercent { get; init; }

    /// <summary>
    /// Current layout variant label for status bar display (e.g., "Grid 1/3").
    /// Null when there is only one variant for the current mode.
    /// </summary>
    public string? LayoutVariantLabel { get; init; }
}
