// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Browser;

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
    /// Visual state of the podcast CTA button (0=Idle, 1=Pressed, 2=Disabled, 3=Unconfigured, 5=Generating).
    /// Mapped to PodcastCtaState enum in the rendering layer.
    /// </summary>
    public int PodcastButtonState { get; init; }

    /// <summary>
    /// Progress fraction (0.0 to 1.0) for the podcast generation progress bar.
    /// Only meaningful when <see cref="PodcastButtonState"/> is 5 (Generating).
    /// </summary>
    public double PodcastProgressFraction { get; init; }

    /// <summary>
    /// Number of articles in the active collection. Used by the podcast CTA
    /// hero tier to display article count and estimated duration.
    /// </summary>
    public int PodcastArticleCount { get; init; }

    /// <summary>
    /// Public RSS feed URL of the active podcast generation job, or null
    /// when no job is active or the job is local-only (no GCS bucket).
    /// workspace-y41e: surfaced on the reading-list CTA's Generating state
    /// so the user can subscribe in their podcast app before walking away.
    /// </summary>
    public string? PodcastFeedUrl { get; init; }

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

    /// <summary>
    /// Current layout variant name for the active view mode (e.g., "Grid"/"List"/"Compact" for the launcher,
    /// "Comfortable"/"FullWidth"/"Narrow" for the reader, "Standard"/"Compact" for collection items).
    /// Used by renderers to select the appropriate layout algorithm. The hierarchical link tree no longer
    /// has variants — there is a single layout (workspace-utaw).
    /// </summary>
    public string? LayoutVariant { get; init; }

    /// <summary>
    /// Paywalled domains relevant to the current page where authentication cookies are
    /// missing or expired. Surfaced by the status bar so the user knows pre-fetch is
    /// silently doing nothing on those sites and can recover via <c>:cookies import</c>
    /// (or <c>Shift+I</c>). Empty when cookies are present, when the current page is not
    /// a paywalled domain, or when no domains are configured.
    /// </summary>
    public IReadOnlyList<string>? MissingCookieDomains { get; init; }

    /// <summary>
    /// Whether the headed browser window is currently docked beside the terminal.
    /// Drives the persistent "⇉ docked" status-bar affordance (workspace-v7mb) so the
    /// side-by-side "concert" state stays visible after the transient status message fades.
    /// </summary>
    public bool BrowserDocked { get; init; }

    /// <summary>
    /// Column the app's content should start at while the browser is docked over part of
    /// the terminal (workspace-8fkv). Zero for the undocked and right-dock cases (content
    /// stays flush-left; the browser covers the right), positive for left-dock (content is
    /// pushed into the uncovered RIGHT columns and the browser covers the blanked left).
    /// Applied as a global left shift by the renderers ON TOP of any per-view left margin
    /// (e.g. reader centering). Paired with <see cref="TerminalWidth"/>, which is already
    /// the uncovered width when docked.
    /// </summary>
    public int ContentLeftOffset { get; init; }

    /// <summary>
    /// Typed human-action signal currently active for the page being rendered
    /// (CAPTCHA, login wall, cookie consent, 2FA, paywall, region block, generic).
    /// When non-null, the status bar replaces the legacy <see cref="MissingCookieDomains"/>
    /// cookie badge with a variant-aware "⏸ {verb} at {domain}" badge per
    /// workspace-0b9s. When null, the status bar falls back to the existing
    /// cookie-domain badge for backwards compatibility.
    /// </summary>
    public HumanActionRequired? RequiredAction { get; init; }

    /// <summary>
    /// When true the launcher renders a "set up API keys" hint row above the
    /// bookmark grid and exposes it as a focusable element (selectedIndex = -2).
    /// Driven by <c>SettingsCommandHandler.IsFirstRun</c>: visible while none of
    /// the four primary credentials have been configured.
    /// </summary>
    public bool ShowSetupHint { get; init; }

    /// <summary>
    /// workspace-frpl.13 (B11) — launcher badge text for unacknowledged scheduled-run
    /// failures/recoveries, or null when nothing needs attention. Surfaced on the
    /// launcher footer so a scheduled run that failed while the user was away is never
    /// silent. Populated only in launcher mode.
    /// </summary>
    public string? ScheduledRunBadge { get; init; }

    /// <summary>
    /// Number of items currently saved to the Reading List collection. Surfaced
    /// to the launcher's Reading List tile (workspace-fbcn) so its subtitle can
    /// read "{N} saved articles" when populated, or the empty-state copy when
    /// zero. Populated only on launcher view; null elsewhere.
    /// </summary>
    public int? ReadingListItemCount { get; init; }

    /// <summary>
    /// When true the prefetch detail panel (workspace-v75w) is drawn as an
    /// overlay on top of the active link-list / collection-items view. The
    /// panel reads from <see cref="CacheProgress"/> and is a no-op when that
    /// is null. The toggle keybind lives in a separate child task
    /// (workspace-c8v3); this flag is the renderer-facing surface.
    /// </summary>
    public bool ShowPreloadDetail { get; init; }
}
