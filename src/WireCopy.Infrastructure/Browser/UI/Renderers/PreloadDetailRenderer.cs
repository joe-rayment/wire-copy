// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the prefetch detail overlay (workspace-v75w) — a centered, card-style
/// panel that exposes per-URL stage, the queue tail, and recent outcomes from
/// <see cref="PreloadProgress"/>. Drawn over the existing view when the toggle
/// is active (see <c>RenderOptions.ShowPreloadDetail</c>).
///
/// <para>
/// Panel structure (top-to-bottom):
/// </para>
/// <list type="bullet">
///   <item>Title bar: <c>PREFETCH</c></item>
///   <item>Summary: <c>X/Y cached · Z queued · paused/running</c></item>
///   <item>Now: current URL + stage chip</item>
///   <item>Up next: up to 10 queued URLs</item>
///   <item>Recent: up to 10 history entries with glyph + elapsedMs</item>
/// </list>
/// </summary>
internal sealed class PreloadDetailRenderer
{
    internal const int MinPanelWidth = 50;
    internal const int MaxPanelWidth = 120;
    internal const int MinTerminalWidthForOverlay = MinPanelWidth + 6;
    internal const int MinTerminalHeightForOverlay = 8;

    // workspace-fh7g: stall thresholds. Past WarningThreshold the "Now:" URL
    // renders in warning color with elapsed time. Past StuckThreshold a hint
    // line appears telling the user the load is probably wedged and how to
    // recover.
    internal static readonly TimeSpan StallWarningThreshold = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan StallStuckThreshold = TimeSpan.FromSeconds(30);

    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const int MaxListEntries = 10;

    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;

    public PreloadDetailRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
    }

    /// <summary>
    /// Renders the prefetch detail panel as a centered overlay. No-op when
    /// <paramref name="progress"/> is null — callers wire this in so the panel
    /// only appears when the user has toggled it on AND the preloader has data
    /// to report. Also a no-op when the terminal is too small to fit the
    /// minimum panel width (workspace-v75w QA punch-list item 3) — avoids
    /// painting a 54-wide box into a 20-column terminal and producing wrap junk.
    /// </summary>
    public void Render(PreloadProgress? progress, int terminalWidth, int terminalHeight)
    {
        if (progress == null)
        {
            return;
        }

        // Need at least MinPanelWidth + 4 chrome cols + 2 outer cols of breathing room.
        // Below that, give up rather than overflow — the caller's main view stays visible.
        if (terminalWidth < MinTerminalWidthForOverlay || terminalHeight < MinTerminalHeightForOverlay)
        {
            return;
        }

        var palette = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var lines = BuildPanelLines(progress, palette, terminalWidth);

        var innerWidth = Math.Min(MaxPanelWidth, Math.Max(MinPanelWidth, terminalWidth - 8));
        var boxWidth = innerWidth + 4;
        var leftPad = Math.Max(0, (terminalWidth - boxWidth) / 2);

        // workspace-c8v3: reserve at least the status-bar's 2-line footprint at
        // the bottom so the overlay never paints over the chrome below. The
        // panel is centered in the remaining height — same visual rule as the
        // prior "topPad = (height - lines.Count - 2) / 3" but enforced via the
        // available-height clamp.
        const int StatusBarReservedLines = 2;
        var availableHeight = Math.Max(0, terminalHeight - StatusBarReservedLines);
        var topPad = Math.Max(0, (availableHeight - (lines.Count + 2)) / 3);
        var borderFg = palette.HeaderBorderFg.AnsiFg;

        var titleLabel = " PREFETCH ";
        var titleBar = $"{borderFg}╭─{Bold}{titleLabel}{Reset}{borderFg}{new string('─', boxWidth - 3 - titleLabel.Length)}╮{Reset}";

        // Absolute positioning so the overlay doesn't disturb _linesWritten on
        // the underlying view (status bar stays intact, link list stays intact).
        // (col, row) is 0-indexed and matches RenderHelpers.WriteAt's contract.
        var row = topPad;
        _helpers.WriteAt(leftPad, row++, titleBar);

        foreach (var line in lines)
        {
            if (row >= availableHeight)
            {
                // Clamp: never paint over the reserved status-bar region.
                break;
            }

            var displayWidth = RenderHelpers.GetDisplayWidth(line.PlainText);
            var rightPadding = Math.Max(0, innerWidth - displayWidth);
            var rendered = $"{borderFg}│{Reset} {line.StyledText}{new string(' ', rightPadding)} {borderFg}│{Reset}";
            _helpers.WriteAt(leftPad, row++, rendered);
        }

        if (row < availableHeight)
        {
            var bottomBorder = $"{borderFg}╰{new string('─', boxWidth - 2)}╯{Reset}";
            _helpers.WriteAt(leftPad, row, bottomBorder);
        }
    }

    /// <summary>
    /// Builds the styled+plain content lines for the panel. Exposed for tests
    /// so they can verify content/layout without binding to console output.
    /// </summary>
    internal static List<PanelLine> BuildPanelLines(PreloadProgress progress, ThemePalette palette, int terminalWidth)
    {
        var innerWidth = Math.Min(MaxPanelWidth, Math.Max(MinPanelWidth, terminalWidth - 8));

        var lines = new List<PanelLine>
        {
            BuildSummaryLine(progress, palette),
            PanelLine.Blank,
            BuildNowLine(progress, palette, innerWidth),
        };

        var stageChip = BuildStageChip(progress.CurrentStage, palette);
        if (stageChip != null)
        {
            lines.Add(new PanelLine($"      {stageChip.Value.Styled}", $"      {stageChip.Value.Plain}"));
        }

        // workspace-fh7g: past the 30s "stuck" threshold, surface a recovery
        // hint so the user knows the load is jammed and how to retry. Both
        // a non-empty CurrentlyFetchingUrl AND a past-stuck elapsed are
        // required — an idle preloader with a stale elapsed value should
        // never trip this.
        if (!string.IsNullOrWhiteSpace(progress.CurrentlyFetchingUrl)
            && progress.ElapsedOnCurrent is { } elapsed
            && elapsed >= StallStuckThreshold)
        {
            const string Hint = "looks stuck — Shift+R to retry";
            var styled = $"      {palette.GetWarningFg().AnsiFg}{Hint}{Reset}";
            var plain = $"      {Hint}";
            lines.Add(new PanelLine(styled, plain));
        }

        lines.Add(PanelLine.Blank);
        lines.Add(BuildSectionHeader("Up next", palette));

        if (progress.UpcomingUrls.Count == 0)
        {
            lines.Add(BuildBodyText("(queue is empty)", palette));
        }
        else
        {
            var maxUrlWidth = innerWidth - 4;
            foreach (var url in progress.UpcomingUrls.Take(MaxListEntries))
            {
                var truncated = RenderHelpers.TruncateUrl(url, maxUrlWidth);
                var styled = $"  {palette.SecondaryText.AnsiFg}→{Reset} {palette.PrimaryText.AnsiFg}{truncated}{Reset}";
                var plain = $"  → {truncated}";
                lines.Add(new PanelLine(styled, plain));
            }
        }

        lines.Add(PanelLine.Blank);
        lines.Add(BuildSectionHeader("Recent", palette));

        if (progress.RecentItems.Count == 0)
        {
            lines.Add(BuildBodyText("(no history yet)", palette));
        }
        else
        {
            const int elapsedReserve = 8;
            var maxUrlWidth = innerWidth - 4 - elapsedReserve;
            foreach (var entry in progress.RecentItems.Take(MaxListEntries))
            {
                lines.Add(BuildHistoryLine(entry, palette, innerWidth, maxUrlWidth));
            }
        }

        return lines;
    }

    /// <summary>
    /// Public for tests: returns the (styled, plain) chip for a stage, or null
    /// when the stage is <see cref="PreloadStage.Idle"/> (no chip drawn).
    /// </summary>
    internal static (string Styled, string Plain)? BuildStageChip(PreloadStage stage, ThemePalette palette)
    {
        var (label, color) = stage switch
        {
            PreloadStage.Fetching => ("loading", palette.PrimaryText.AnsiFg),
            PreloadStage.Detecting => ("detecting", palette.PrimaryText.AnsiFg),
            PreloadStage.ExtractingContent => ("extracting", palette.GetAccentFg().AnsiFg),
            PreloadStage.PersistingCache => ("caching", palette.GetDimFg().AnsiFg),
            _ => (string.Empty, string.Empty),
        };

        if (string.IsNullOrEmpty(label))
        {
            return null;
        }

        var styled = $"{color}⚡ {label}{Reset}";
        var plain = $"⚡ {label}";
        return (styled, plain);
    }

    /// <summary>
    /// Compact "Xs" / "Xm Ys" elapsed-time formatter (workspace-fh7g). Used
    /// by the "Now:" line of the prefetch detail panel to surface how long
    /// the in-flight URL has been pending. Sub-second elapsed is rendered as
    /// "&lt;1s" so the suffix is never empty when the timer is just starting.
    /// </summary>
    internal static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1)
        {
            return "<1s";
        }

        var total = (int)Math.Round(elapsed.TotalSeconds);
        if (total < 60)
        {
            return $"{total}s";
        }

        var minutes = total / 60;
        var rem = total % 60;
        return rem == 0 ? $"{minutes}m" : $"{minutes}m {rem}s";
    }

    private static PanelLine BuildSummaryLine(PreloadProgress progress, ThemePalette palette)
    {
        var total = Math.Max(progress.TotalCacheableLinks, 0);
        var cached = Math.Max(progress.CachedCount, 0);
        var queued = progress.UpcomingUrls.Count;
        string state;
        if (progress.PausedByUser)
        {
            state = "paused (you're using the browser)";
        }
        else
        {
            state = progress.IsActivelyFetching ? "running" : "paused";
        }

        var stateColor = progress.IsActivelyFetching ? palette.GetAccentFg().AnsiFg : palette.GetDimFg().AnsiFg;
        var sep = $"{palette.GetDimFg().AnsiFg} · {Reset}";

        var styled = $"{palette.PrimaryText.AnsiFg}{cached.ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)} cached{Reset}{sep}" +
                     $"{palette.PrimaryText.AnsiFg}{queued.ToString(CultureInfo.InvariantCulture)} queued{Reset}{sep}" +
                     $"{stateColor}{state}{Reset}";
        var plain = $"{cached}/{total} cached · {queued} queued · {state}";
        return new PanelLine(styled, plain);
    }

    private static PanelLine BuildNowLine(PreloadProgress progress, ThemePalette palette, int innerWidth)
    {
        if (string.IsNullOrWhiteSpace(progress.CurrentlyFetchingUrl))
        {
            return new PanelLine($"{palette.GetDimFg().AnsiFg}Now: idle{Reset}", "Now: idle");
        }

        // workspace-fh7g: only surface the elapsed suffix + warning color once
        // we've crossed the 8s stall threshold. Below that, the panel stays
        // calm — adding "(2s)" to every Now line during normal operation
        // would just be visual noise.
        var elapsed = progress.ElapsedOnCurrent;
        var isStall = elapsed.HasValue && elapsed.Value >= StallWarningThreshold;
        var elapsedSuffix = isStall ? $" ({FormatElapsed(elapsed!.Value)})" : string.Empty;

        var maxUrlWidth = Math.Max(10, innerWidth - "Now: ".Length - elapsedSuffix.Length);
        var truncated = RenderHelpers.TruncateUrl(progress.CurrentlyFetchingUrl, maxUrlWidth);

        var urlColor = isStall ? palette.GetWarningFg().AnsiFg : palette.PrimaryText.AnsiFg;
        var suffixColor = isStall ? palette.GetWarningFg().AnsiFg : palette.GetDimFg().AnsiFg;

        var styled = $"{palette.SecondaryText.AnsiFg}Now: {Reset}{urlColor}{truncated}{Reset}" +
                     $"{suffixColor}{elapsedSuffix}{Reset}";
        var plain = $"Now: {truncated}{elapsedSuffix}";
        return new PanelLine(styled, plain);
    }

    private static PanelLine BuildSectionHeader(string label, ThemePalette palette)
    {
        var styled = $"{palette.SecondaryText.AnsiFg}{Bold}{label}{Reset}";
        return new PanelLine(styled, label);
    }

    private static PanelLine BuildBodyText(string text, ThemePalette palette)
    {
        var styled = $"  {palette.GetDimFg().AnsiFg}{text}{Reset}";
        var plain = $"  {text}";
        return new PanelLine(styled, plain);
    }

    private static PanelLine BuildHistoryLine(PreloadHistoryEntry entry, ThemePalette palette, int innerWidth, int maxUrlWidth)
    {
        var (glyph, color) = entry.Outcome switch
        {
            PreloadOutcome.Cached => ("✓", palette.GetSuccessFg().AnsiFg),
            PreloadOutcome.Skipped => ("⏭", palette.GetDimFg().AnsiFg),
            PreloadOutcome.Failed => ("✗", palette.GetWarningFg().AnsiFg),
            _ => ("?", palette.SecondaryText.AnsiFg),
        };

        var elapsed = $"{entry.ElapsedMs.ToString(CultureInfo.InvariantCulture)}ms";
        var truncatedUrl = RenderHelpers.TruncateUrl(entry.Url, Math.Max(10, maxUrlWidth));
        var leftPlain = $"  {glyph} {truncatedUrl}";
        var leftWidth = RenderHelpers.GetDisplayWidth(leftPlain);

        // workspace-v04i: surface the skip/failure reason ("paywall", "needs JS",
        // "bot detection", …) dim after the URL so the user can tell WHY an entry
        // was skipped without leaving the panel. Dropped when the row is too
        // tight to show a meaningful fragment.
        var reasonStyled = string.Empty;
        var reasonPlain = string.Empty;
        if (!string.IsNullOrWhiteSpace(entry.Reason))
        {
            var room = innerWidth - leftWidth - elapsed.Length - 1;
            if (room >= 6)
            {
                var reasonText = RenderHelpers.TruncateText($" — {entry.Reason}", room);
                reasonStyled = $"{palette.GetDimFg().AnsiFg}{reasonText}{Reset}";
                reasonPlain = reasonText;
            }
        }

        var pad = Math.Max(1, innerWidth - leftWidth - RenderHelpers.GetDisplayWidth(reasonPlain) - elapsed.Length);

        var styled = $"  {color}{glyph}{Reset} {palette.PrimaryText.AnsiFg}{truncatedUrl}{Reset}{reasonStyled}{new string(' ', pad)}{palette.GetDimFg().AnsiFg}{elapsed}{Reset}";
        var plain = $"{leftPlain}{reasonPlain}{new string(' ', pad)}{elapsed}";
        return new PanelLine(styled, plain);
    }

    /// <summary>
    /// One styled+plain pair representing a single rendered line inside the panel.
    /// Plain text drives width measurement; styled text drives terminal output.
    /// </summary>
    internal readonly record struct PanelLine(string StyledText, string PlainText)
    {
        public static PanelLine Blank => new(string.Empty, string.Empty);
    }
}
