// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Tiny 3-line "Analyzing reading list — N/M" box that overlays the bottom
/// of the current view while the silent cache-analysis runs ahead of the
/// cost-gate modal (workspace-reym). Sits above the status bar at the same
/// position as <see cref="PodcastCostGateModal"/> so the morph to the
/// cost-gate modal is visually seamless.
///
/// <para>
/// Built as a standalone helper rather than reusing
/// <see cref="NavigationService.SetStatusMessage(string)"/> because the
/// CollectionItems render path constructs a fresh empty
/// <see cref="Domain.ValueObjects.Browser.NavigationContext"/> and drops the
/// live status message on the floor — a pre-existing gap that the user
/// experienced as a 30-second silent hang after pressing <c>p</c>.
/// </para>
/// </summary>
internal static class PodcastAnalyzingIndicator
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";

    /// <summary>
    /// Renders the indicator box at the bottom of the current view. Idempotent
    /// — call repeatedly with updated <paramref name="completed"/> as
    /// articles get analyzed; each call paints the same three rows so the
    /// content updates without scroll churn.
    /// </summary>
    public static void Render(RenderOptions options, ThemePalette palette, int completed, int total)
    {
        var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
        var boxWidth = Math.Max(60, Math.Min(options.TerminalWidth - 4, 120));
        var leftPad = Math.Max(0, (options.TerminalWidth - boxWidth) / 2);
        var pad = new string(' ', leftPad);
        var borderFg = palette.HeaderBorderFg.AnsiFg;

        var summary = $"Analyzing reading list — {completed}/{total}";
        var hint = $"{palette.SecondaryText.AnsiFg}please wait{Reset}";
        var hintPlain = "please wait";

        // workspace-m8es.1: the unified podcast bar (CTA style) so the
        // analyzing box already speaks the generating flow's visual language.
        const int barLength = 14;
        var fraction = total > 0 ? (double)completed / total : 0;
        var bar = UI.Components.Indicators.PodcastBar(palette, fraction, barLength);

        var contentWidth = boxWidth - 4;
        var summaryWidth = RenderHelpers.GetDisplayWidth(summary);
        var hintWidth = RenderHelpers.GetDisplayWidth(hintPlain);
        var gapWidth = Math.Max(2, contentWidth - summaryWidth - barLength - 1 - hintWidth);

        var styledSummary = $"{Bold}{palette.PrimaryText.AnsiFg}{summary}{Reset}";

        var topRow = Math.Max(0, options.TerminalHeight - 5);
        helpers.WriteAt(0, topRow, $"{pad}{borderFg}╭{new string('─', boxWidth - 2)}╮{Reset}");
        helpers.WriteAt(0, topRow + 1, $"{pad}{borderFg}│{Reset} {styledSummary}{new string(' ', gapWidth)}{bar} {hint} {borderFg}│{Reset}");
        helpers.WriteAt(0, topRow + 2, $"{pad}{borderFg}╰{new string('─', boxWidth - 2)}╯{Reset}");
    }

    /// <summary>
    /// Blanks the three rows the indicator wrote to so the next full repaint
    /// (typically the cost-gate modal or the progress screen) starts from a
    /// clean state. Mirrors <c>PodcastCostGateModal.ClearBoxRows</c>.
    /// </summary>
    public static void Clear(RenderOptions options)
    {
        var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
        var topRow = Math.Max(0, options.TerminalHeight - 5);
        var blank = new string(' ', Math.Max(1, options.TerminalWidth));
        for (var row = topRow; row < Math.Min(options.TerminalHeight - 2, topRow + 3); row++)
        {
            helpers.WriteAt(0, row, blank);
        }
    }
}
