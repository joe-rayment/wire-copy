// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Shared renderer for "settings rows" — the row layout used on both the
/// Generate Podcast confirmation screen and the unified <c>:config</c> Setup
/// screen (workspace-fn1u). Both screens use the same selection accent
/// (▌), status icon, label, value column, and right-aligned <c>[Enter] action</c>
/// button so the visual language stays consistent.
///
/// Layout (visible columns, ANSI escapes excluded):
///   col 0..1  : 2-space outer margin
///   col 2     : selection-indicator gutter (▌ when selected, blank otherwise)
///   col 3     : single space separator
///   col 4     : status icon
///   col 5     : single space
///   col 6...  : label (padded to <paramref name="labelCol"/>), value, then right-aligned action
///
/// The selection indicator lives in a fixed-width gutter so the label/value
/// text never shifts horizontally between selected and unselected rows. The
/// optional sub-line is indented to col 6 so it visually attaches to the row.
/// </summary>
internal static class SettingsRowRenderer
{
    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Builds a single selectable settings row. Returns the main line and an
    /// optional sub-line (warning or helper text indented to the content column).
    /// </summary>
    /// <returns>(mainLine, subLine) — subLine is null when neither warning nor helper text was provided.</returns>
    internal static (string MainLine, string? SubLine) Build(
        ThemePalette palette,
        int width,
        bool isSelected,
        bool isWarning,
        string statusIcon,
        string statusColor,
        string label,
        string value,
        string valueColor,
        string actionLabel,
        string? warningText = null,
        string? helperText = null,
        int labelCol = 24)
    {
        var indicatorChar = isSelected ? "▌" : " ";
        var indicatorColor = isWarning ? palette.GetWarningFg().AnsiFg : palette.GetMutedFg().AnsiFg;
        var indicatorCell = $"{indicatorColor}{indicatorChar}{Reset}";

        var paddedLabel = label.Length < labelCol ? label + new string(' ', labelCol - label.Length) : label;

        // When the row is in a warning state, override the label/value colors
        // so the whole row visually owns its error state.
        var effectiveStatusColor = isWarning ? palette.GetWarningFg().AnsiFg : statusColor;
        var effectiveLabelColor = isWarning ? palette.GetWarningFg().AnsiFg : palette.PrimaryText.AnsiFg;
        var effectiveValueColor = isWarning ? palette.GetWarningFg().AnsiFg : valueColor;

        var actionBtn = $"{palette.GetAccentFg().AnsiFg}[Enter]{Reset} {palette.PrimaryText.AnsiFg}{actionLabel}{Reset}";

        // Visible-character widths (used to right-align the action button)
        const int marginLen = 2;     // "  "
        const int gutterLen = 1;     // ▌ or space
        const int gapLen = 1;        // space between gutter and content
        var contentPlainLen = 1 /*statusIcon*/ + 1 /*space*/ + paddedLabel.Length + value.Length;
        var actionPlainLen = "[Enter] ".Length + actionLabel.Length;
        var totalPlainLen = marginLen + gutterLen + gapLen + contentPlainLen + actionPlainLen;
        var pad = Math.Max(2, width - totalPlainLen);

        var content = $"{effectiveStatusColor}{statusIcon}{Reset} " +
                      $"{effectiveLabelColor}{paddedLabel}{Reset}" +
                      $"{effectiveValueColor}{value}{Reset}";

        string mainLine;
        if (isSelected)
        {
            // Highlight the content area only — the outer margin and indicator gutter
            // stay outside the highlight so the gutter remains visible and the content
            // does not horizontally shift.
            mainLine = $"  {indicatorCell} " +
                       $"{palette.SelectedItemBg.AnsiBg}{palette.SelectedItemFg.AnsiFg}{content}{new string(' ', pad)}{actionBtn}{Reset}";
        }
        else
        {
            mainLine = $"  {indicatorCell} {content}{new string(' ', pad)}{actionBtn}";
        }

        string? subLine = null;
        const string subIndent = "      "; // 2 margin + 1 gutter + 1 gap + 2 (icon+space) = 6
        if (warningText != null)
        {
            subLine = $"{subIndent}{palette.GetWarningFg().AnsiFg}{warningText}{Reset}";
        }
        else if (helperText != null)
        {
            subLine = $"{subIndent}{palette.SecondaryText.AnsiFg}{helperText}{Reset}";
        }

        return (mainLine, subLine);
    }

    /// <summary>
    /// Strips CSI ANSI escape sequences so callers can compute visible column
    /// positions (e.g. for layout assertions in tests).
    /// </summary>
    internal static string StripAnsi(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }

        return System.Text.RegularExpressions.Regex.Replace(s, "\x1b\\[[0-9;]*[A-Za-z]", string.Empty);
    }
}
