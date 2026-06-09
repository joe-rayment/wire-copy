// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Renders toast notifications as rounded-box overlays in the top-right corner.
/// Toasts overlay existing content using direct cursor positioning.
/// </summary>
internal static class ToastRenderer
{
    private const string Reset = "\x1b[0m";
    private const int MaxContentWidth = 36;
    private const int BoxPadding = 2;     // "│ " and " │"
    private const int BorderChars = 2;    // "╭" + "╮" or "╰" + "╯"
    private const int RightMargin = 1;

    // Icons per toast type (see design system)
    private const char InfoIcon = '⚡';        // ⚡
    private const char SuccessIcon = '✔';     // ✔
    private const char ErrorIcon = '✗';       // ✗
    private const char CelebrationIcon = '✦'; // ✦

    /// <summary>
    /// Renders a toast notification overlay at the top-right corner of the terminal.
    /// Uses direct cursor positioning to overlay existing content without shifting layout.
    /// Compatibility overload that bypasses frame buffering — kept for tests and
    /// non-buffered fallbacks. Prefer the overload that takes <see cref="RenderHelpers"/>
    /// from production render paths so the toast survives the frame flush.
    /// </summary>
    public static void RenderToast(ToastNotification toast, ThemePalette palette, int terminalWidth)
    {
        RenderToastInternal(toast, palette, terminalWidth, writer: null);
    }

    /// <summary>
    /// Renders a toast via <see cref="RenderHelpers.WriteAt"/>, ensuring its
    /// escape sequences accumulate into the active frame buffer (when buffering)
    /// and therefore aren't overwritten by the subsequent EndFrame flush.
    /// </summary>
    public static void RenderToast(ToastNotification toast, ThemePalette palette, int terminalWidth, RenderHelpers helpers)
    {
        RenderToastInternal(toast, palette, terminalWidth, helpers);
    }

    private static void RenderToastInternal(ToastNotification toast, ThemePalette palette, int terminalWidth, RenderHelpers? writer)
    {
        var borderColor = GetBorderColor(toast.Type, palette);
        var icon = GetIcon(toast.Type);
        var iconColor = borderColor;

        // Position: top-right corner (calculated below after final sizing)
        var startRow = 1; // 0-indexed row 1 (second line from top)

        var borderFg = borderColor.AnsiFg;
        var messageFg = palette.PrimaryText.AnsiFg;
        var detailFg = palette.SecondaryText.AnsiFg;

        // Separate icon rendering from the message + detail
        var iconStr = icon.ToString();
        var iconWidth = RenderHelpers.GetDisplayWidth(iconStr);

        // Build message + detail part (after "icon ")
        var messageStr = toast.Message;
        var detailStr = toast.Detail ?? string.Empty;

        // Calculate available width for message + detail (after icon + space)
        var availableForText = MaxContentWidth - iconWidth - 1; // -1 for space after icon
        var messageAndDetail = string.IsNullOrEmpty(detailStr)
            ? messageStr
            : $"{messageStr} {detailStr}";
        var truncatedText = RenderHelpers.TruncateText(messageAndDetail, availableForText);

        // Calculate actual content width based on what we'll render
        var actualContentWidth = iconWidth + 1 + RenderHelpers.GetDisplayWidth(truncatedText);
        var innerWidth = Math.Max(actualContentWidth, 1);
        var boxWidth = innerWidth + BoxPadding + BorderChars;

        // workspace-8fkv: terminalWidth is already the uncovered width when docked, so add
        // the content origin to keep a left-docked browser from sitting over the toast.
        var startCol = (writer?.ColumnOffset ?? 0) + Math.Max(0, terminalWidth - boxWidth - RightMargin);

        // Split truncated text back into message and detail for coloring
        string renderedMessage;
        string renderedDetail;
        if (string.IsNullOrEmpty(detailStr) || truncatedText.Length <= messageStr.Length)
        {
            renderedMessage = RenderHelpers.TruncateText(messageStr, availableForText);
            renderedDetail = string.Empty;
        }
        else
        {
            renderedMessage = messageStr;
            var remainingWidth = availableForText - RenderHelpers.GetDisplayWidth(messageStr) - 1;
            renderedDetail = RenderHelpers.TruncateText(detailStr, Math.Max(0, remainingWidth));
        }

        var contentLineWidth = iconWidth + 1
            + RenderHelpers.GetDisplayWidth(renderedMessage)
            + (string.IsNullOrEmpty(renderedDetail) ? 0 : 1 + RenderHelpers.GetDisplayWidth(renderedDetail));
        var paddingRight = Math.Max(0, innerWidth - contentLineWidth);

        var topBorder = $"{borderFg}╭{new string('─', innerWidth + BoxPadding)}╮{Reset}";
        var detailPart = string.IsNullOrEmpty(renderedDetail)
            ? string.Empty
            : $" {detailFg}{renderedDetail}";
        var contentLine =
            $"{borderFg}│ " +
            $"{iconColor.AnsiFg}{iconStr}" +
            $" {messageFg}{renderedMessage}" +
            $"{detailPart}" +
            $"{new string(' ', paddingRight)}" +
            $" {borderFg}│{Reset}";
        var bottomBorder = $"{borderFg}╰{new string('─', innerWidth + BoxPadding)}╯{Reset}";

        if (writer != null)
        {
            // Buffered path: escapes accumulate into the active frame buffer
            // and survive the EndFrame flush.
            writer.WriteAt(startCol, startRow, topBorder);
            writer.WriteAt(startCol, startRow + 1, contentLine);
            writer.WriteAt(startCol, startRow + 2, bottomBorder);
            return;
        }

        try
        {
            Console.SetCursorPosition(startCol, startRow);
            Console.Write(topBorder);
            Console.SetCursorPosition(startCol, startRow + 1);
            Console.Write(contentLine);
            Console.SetCursorPosition(startCol, startRow + 2);
            Console.Write(bottomBorder);
        }
        catch
        {
            // Ignore errors in non-standard console environments
        }
    }

    private static ThemeColor GetBorderColor(ToastType type, ThemePalette palette)
    {
        return type switch
        {
            ToastType.Info => palette.GetAccentFg(),
            ToastType.Success => palette.GetSuccessFg(),
            ToastType.Error => palette.ErrorFg,
            ToastType.Celebration => palette.GetCelebrationFg(),
            _ => palette.GetAccentFg(),
        };
    }

    private static char GetIcon(ToastType type)
    {
        return type switch
        {
            ToastType.Info => InfoIcon,
            ToastType.Success => SuccessIcon,
            ToastType.Error => ErrorIcon,
            ToastType.Celebration => CelebrationIcon,
            _ => InfoIcon,
        };
    }
}
