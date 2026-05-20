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
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const int MaxListEntries = 10;
    private const int MinPanelWidth = 50;
    private const int MaxPanelWidth = 120;

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
    /// to report.
    /// </summary>
    public void Render(PreloadProgress? progress, int terminalWidth, int terminalHeight)
    {
        if (progress == null)
        {
            return;
        }

        var palette = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var lines = BuildPanelLines(progress, palette, terminalWidth);

        var innerWidth = Math.Min(MaxPanelWidth, Math.Max(MinPanelWidth, terminalWidth - 8));
        var boxWidth = innerWidth + 4;
        var leftPad = Math.Max(0, (terminalWidth - boxWidth) / 2);
        var topPad = Math.Max(0, (terminalHeight - (lines.Count + 2)) / 3);
        var pad = new string(' ', leftPad);
        var borderFg = palette.HeaderBorderFg.AnsiFg;

        for (var i = 0; i < topPad; i++)
        {
            _helpers.WriteLine();
        }

        var titleLabel = " PREFETCH ";
        var titleBar = $"{pad}{borderFg}╭─{Bold}{titleLabel}{Reset}{borderFg}{new string('─', boxWidth - 3 - titleLabel.Length)}╮{Reset}";
        _helpers.WriteLine(titleBar);

        foreach (var line in lines)
        {
            var displayWidth = RenderHelpers.GetDisplayWidth(line.PlainText);
            var rightPadding = Math.Max(0, innerWidth - displayWidth);
            _helpers.WriteLine($"{pad}{borderFg}│{Reset} {line.StyledText}{new string(' ', rightPadding)} {borderFg}│{Reset}");
        }

        _helpers.WriteLine($"{pad}{borderFg}╰{new string('─', boxWidth - 2)}╯{Reset}");
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

    private static PanelLine BuildSummaryLine(PreloadProgress progress, ThemePalette palette)
    {
        var total = Math.Max(progress.TotalCacheableLinks, 0);
        var cached = Math.Max(progress.CachedCount, 0);
        var queued = progress.UpcomingUrls.Count;
        var state = progress.IsActivelyFetching ? "running" : "paused";

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

        var maxUrlWidth = Math.Max(10, innerWidth - "Now: ".Length);
        var truncated = RenderHelpers.TruncateUrl(progress.CurrentlyFetchingUrl, maxUrlWidth);
        var styled = $"{palette.SecondaryText.AnsiFg}Now: {Reset}{palette.PrimaryText.AnsiFg}{truncated}{Reset}";
        var plain = $"Now: {truncated}";
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
        var pad = Math.Max(1, innerWidth - leftWidth - elapsed.Length);

        var styled = $"  {color}{glyph}{Reset} {palette.PrimaryText.AnsiFg}{truncatedUrl}{Reset}{new string(' ', pad)}{palette.GetDimFg().AnsiFg}{elapsed}{Reset}";
        var plain = $"{leftPlain}{new string(' ', pad)}{elapsed}";
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
