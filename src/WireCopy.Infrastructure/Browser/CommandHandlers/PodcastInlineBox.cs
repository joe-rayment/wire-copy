// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Shared renderer for the small 3-row bordered inline box that sits above
/// the status bar (workspace-nahg). Extracted from the duplicated private
/// RenderBox/ClearBoxRows in <see cref="PodcastCostGateModal"/> and
/// <see cref="PodcastMissingKeyModal"/> so the cancellation confirm and the
/// invalid-key border flash share a single layout implementation.
/// </summary>
internal static class PodcastInlineBox
{
    /// <summary>
    /// How long the border stays in the warning colour after an unhandled
    /// keystroke before the box repaints normally (workspace-nahg item 4).
    /// One render frame's worth — long enough to register as a flash,
    /// short enough not to feel like input lag.
    /// </summary>
    internal const int InvalidKeyFlashMs = 120;

    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";

    /// <summary>
    /// Renders the 3-row bordered box (summary left, hint right-aligned)
    /// positioned above the 2-line status bar. Pass
    /// <paramref name="borderFgOverride"/> to recolour the border (used for
    /// the invalid-key warning flash); null uses the theme's header border.
    /// </summary>
    internal static void RenderBox(
        RenderOptions options,
        ThemePalette palette,
        string summary,
        string hint,
        string hintPlain,
        string? borderFgOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(palette);

        var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
        var boxWidth = Math.Max(60, Math.Min(options.TerminalWidth - 4, 120));

        var leftPad = Math.Max(0, (options.TerminalWidth - boxWidth) / 2);
        var pad = new string(' ', leftPad);
        var borderFg = borderFgOverride ?? palette.HeaderBorderFg.AnsiFg;

        // Layout the inner row: summary on left, hint right-aligned.
        var contentWidth = boxWidth - 4;
        var summaryWidth = RenderHelpers.GetDisplayWidth(summary);
        var hintWidth = RenderHelpers.GetDisplayWidth(hintPlain);
        var gapWidth = Math.Max(2, contentWidth - summaryWidth - hintWidth);

        var styledSummary = $"{Bold}{palette.PrimaryText.AnsiFg}{summary}{Reset}";

        // Position the 3-line box so it fits ABOVE the 2-line status bar and is
        // fully visible: 3 box rows + 2 status bar rows = TerminalHeight - 5.
        var topRow = Math.Max(0, options.TerminalHeight - 5);
        helpers.WriteAt(0, topRow, $"{pad}{borderFg}╭{new string('─', boxWidth - 2)}╮{Reset}");
        helpers.WriteAt(0, topRow + 1, $"{pad}{borderFg}│{Reset} {styledSummary}{new string(' ', gapWidth)}{hint} {borderFg}│{Reset}");
        helpers.WriteAt(0, topRow + 2, $"{pad}{borderFg}╰{new string('─', boxWidth - 2)}╯{Reset}");
    }

    /// <summary>
    /// Blanks the rows where the box was painted so the next full-page
    /// render starts from a clean state (WriteAt leaves ghost rows otherwise).
    /// </summary>
    internal static void ClearBoxRows(RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
        var topRow = Math.Max(0, options.TerminalHeight - 5);
        var blank = new string(' ', Math.Max(1, options.TerminalWidth));
        for (var row = topRow; row < Math.Min(options.TerminalHeight - 2, topRow + 3); row++)
        {
            helpers.WriteAt(0, row, blank);
        }
    }

    /// <summary>
    /// workspace-nahg item 4: visual feedback for an unhandled keystroke on
    /// an inline modal. Briefly flashes the box border in the theme's warning
    /// colour, then restores the normal border — so the user sees the input
    /// was received but ignored, instead of a silent identical re-paint.
    /// </summary>
    internal static async Task FlashInvalidKeyAsync(
        RenderOptions options,
        ThemePalette palette,
        string summary,
        string hint,
        string hintPlain,
        CancellationToken ct)
    {
        RenderBox(options, palette, summary, hint, hintPlain, palette.GetWarningFg().AnsiFg);
        try
        {
            await Task.Delay(InvalidKeyFlashMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down — skip the restore paint; the caller's loop
            // observes the cancellation immediately after.
            return;
        }

        RenderBox(options, palette, summary, hint, hintPlain);
    }
}
