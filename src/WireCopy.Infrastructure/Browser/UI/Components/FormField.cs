// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Reusable inline form field component that renders a labeled text input
/// with a rounded border, placeholder, validation, and error display.
///
/// Visual layout (5 lines, or 6 with optional Subtitle):
///   Label
///   [Subtitle - optional one-line "where to find this" pointer]
///   top border
///   inner input row
///   bottom border
///   help / error
///
/// workspace-cgnt: <c>inputCol</c> bumped from 2 to 4 so the cursor lands
/// inside the borders rather than overwriting the left side, and the
/// underlying clear is narrowed to the box's inner width via the
/// <c>clearWidth</c> arg on <see cref="IInputHandler.PromptForInputAsync"/>.
/// </summary>
internal static class FormField
{
    /// <summary>
    /// Default number of terminal rows the component occupies (no subtitle).
    /// Use <see cref="HeightFor"/> when the field carries a Subtitle.
    /// </summary>
    public const int Height = 5;

    private const string Reset = "\x1b[0m";
    private const string Dim = "\x1b[2m";

    /// <summary>
    /// Returns the number of terminal rows the component will occupy for the
    /// given field config — 6 when a Subtitle is present, else <see cref="Height"/>.
    /// </summary>
    public static int HeightFor(FormFieldConfig field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return string.IsNullOrEmpty(field.Subtitle) ? Height : Height + 1;
    }

    /// <summary>
    /// Renders the form field and captures user input. Returns the entered value
    /// or null if the user pressed Escape to cancel.
    /// </summary>
    public static async Task<string?> PromptAsync(
        IInputHandler input,
        FormFieldConfig field,
        ThemePalette palette,
        int startRow,
        int fieldWidth,
        CancellationToken ct)
    {
        var innerWidth = fieldWidth - 4; // 2 border chars + 2 padding spaces
        if (innerWidth < 10)
        {
            innerWidth = 10;
        }

        var boxWidth = innerWidth + 4;
        string? errorMessage = null;
        var hasSubtitle = !string.IsNullOrEmpty(field.Subtitle);
        var topBorderOffset = hasSubtitle ? 2 : 1; // label[+subtitle], then top border

        while (!ct.IsCancellationRequested)
        {
            // Render the field chrome (label, optional subtitle, borders, help/error)
            RenderFieldChrome(palette, field, startRow, boxWidth, innerWidth, errorMessage);

            // Position cursor INSIDE the box for input. workspace-cgnt:
            // col 2 = left border, col 3 = padding space, col 4 = first
            // input cell. Bumping inputCol to 4 keeps the left border intact
            // when the input handler clears the row.
            var inputRow = startRow + topBorderOffset + 1; // label[+sub], top border, then input
            var inputCol = 4;

            // Wrap the field's optional OnExtraKey so that after the caller
            // handles the keystroke (typically by drawing an overlay) we can
            // restore the FormField chrome — TerminalInputHandler only
            // re-renders the input row itself.
            Func<char, bool>? interceptKey = field.OnExtraKey is null
                ? null
                : c =>
                {
                    var handled = field.OnExtraKey(c);
                    if (handled)
                    {
                        RenderFieldChrome(palette, field, startRow, boxWidth, innerWidth, errorMessage);
                    }

                    return handled;
                };

            // workspace-cgnt: When the production TerminalInputHandler is
            // wired up, call its specialised PromptForFieldInputAsync so the
            // input clear stops short of the right border. For substitutes
            // (and any future IInputHandler implementations), fall back to
            // the standard 7-arg PromptForInputAsync — without this fallback
            // we'd break NSubstitute mocks of the interface (see workspace-qi5p
            // tests for CollectionCommandHandler).
            string? value;
            if (input is TerminalInputHandler concrete)
            {
                value = await concrete.PromptForFieldInputAsync(
                    prompt: string.Empty,
                    cancellationToken: ct,
                    isSecret: field.IsSecret,
                    row: inputRow,
                    col: inputCol,
                    initialInput: field.InitialValue,
                    interceptKey: interceptKey,
                    clearWidth: innerWidth).ConfigureAwait(false);
            }
            else
            {
                value = await input.PromptForInputAsync(
                    string.Empty,
                    ct,
                    isSecret: field.IsSecret,
                    row: inputRow,
                    col: inputCol,
                    initialInput: field.InitialValue,
                    interceptKey: interceptKey).ConfigureAwait(false);
            }

            if (value == null)
            {
                // User pressed Escape — cancel
                ClearFieldArea(startRow, boxWidth, hasSubtitle);
                return null;
            }

            // Enforce max length
            if (value.Length > field.MaxLength)
            {
                value = value[..field.MaxLength];
            }

            // Run validation
            if (field.Validate != null)
            {
                errorMessage = field.Validate(value);
                if (errorMessage != null)
                {
                    // Validation failed — re-render with error and retry
                    continue;
                }
            }

            // Success — show brief confirmation and return. Validation row
            // is the row after the bottom border, which depends on whether
            // the field carries a Subtitle (workspace-cgnt).
            var helpRow = startRow + topBorderOffset + 3; // [label, sub?, top, input, bottom, help]
            RenderValidationMessage(palette, helpRow, boxWidth, null, field.HelpText);
            return value;
        }

        return null;
    }

    /// <summary>
    /// Renders the static parts of the field: label, optional subtitle, border
    /// box, and status line. Subtitle is the workspace-cgnt addition - when
    /// present, it shifts the box and validation row down by one.
    /// </summary>
    private static void RenderFieldChrome(
        ThemePalette palette,
        FormFieldConfig field,
        int startRow,
        int boxWidth,
        int innerWidth,
        string? errorMessage)
    {
        var hasSubtitle = !string.IsNullOrEmpty(field.Subtitle);

        // Row 0: Label
        Console.SetCursorPosition(0, startRow);
        ClearLine(boxWidth + 2);
        Console.SetCursorPosition(2, startRow);
        Console.Write($"{palette.PrimaryText.AnsiFg}{field.Label}{Reset}");

        var nextRow = startRow + 1;

        // Optional subtitle row - the "where to find this" pointer rendered
        // directly under the label so users see it BEFORE they start typing.
        if (hasSubtitle)
        {
            Console.SetCursorPosition(0, nextRow);
            ClearLine(boxWidth + 2);
            Console.SetCursorPosition(2, nextRow);
            var maxSubLen = Math.Max(1, boxWidth);
            var sub = field.Subtitle!;
            if (sub.Length > maxSubLen)
            {
                sub = sub[..maxSubLen];
            }

            Console.Write($"{palette.SecondaryText.AnsiFg}{Dim}{sub}{Reset}");
            nextRow++;
        }

        // Top border
        Console.SetCursorPosition(0, nextRow);
        ClearLine(boxWidth + 2);
        Console.SetCursorPosition(2, nextRow);
        Console.Write($"{palette.HeaderBorderFg.AnsiFg}╭{new string('─', boxWidth - 2)}╮{Reset}");
        nextRow++;

        // Input row
        Console.SetCursorPosition(0, nextRow);
        ClearLine(boxWidth + 2);
        Console.SetCursorPosition(2, nextRow);
        WriteInputRow(palette, field, innerWidth);
        nextRow++;

        // Bottom border
        Console.SetCursorPosition(0, nextRow);
        ClearLine(boxWidth + 2);
        Console.SetCursorPosition(2, nextRow);
        Console.Write($"{palette.HeaderBorderFg.AnsiFg}╰{new string('─', boxWidth - 2)}╯{Reset}");
        nextRow++;

        // Validation message or help text
        RenderValidationMessage(palette, nextRow, boxWidth, errorMessage, field.HelpText);
    }

    private static void WriteInputRow(
        ThemePalette palette, FormFieldConfig field, int innerWidth)
    {
        string displayContent;
        int displayLength;

        if (!string.IsNullOrEmpty(field.InitialValue))
        {
            var truncLen = Math.Min(field.InitialValue.Length, innerWidth);
            displayContent = field.IsSecret
                ? new string('*', truncLen)
                : field.InitialValue[..truncLen];
            displayLength = truncLen;
        }
        else if (!string.IsNullOrEmpty(field.Placeholder))
        {
            var truncLen = Math.Min(field.Placeholder.Length, innerWidth);
            var placeholderText = field.Placeholder[..truncLen];
            displayContent = $"{Dim}{palette.SecondaryText.AnsiFg}{placeholderText}{Reset}";
            displayLength = truncLen;
        }
        else
        {
            displayContent = string.Empty;
            displayLength = 0;
        }

        var padding = new string(' ', Math.Max(0, innerWidth - displayLength));
        Console.Write(
            $"{palette.HeaderBorderFg.AnsiFg}│ " +
            $"{palette.PrimaryText.AnsiFg}{displayContent}{padding}" +
            $" {palette.HeaderBorderFg.AnsiFg}│{Reset}");
    }

    private static void RenderValidationMessage(
        ThemePalette palette, int row, int width, string? errorMessage, string? helpText)
    {
        Console.SetCursorPosition(0, row);
        ClearLine(width + 2);
        Console.SetCursorPosition(2, row);

        if (errorMessage != null)
        {
            var truncated = errorMessage.Length > width - 6
                ? errorMessage[..(width - 6)]
                : errorMessage;
            Console.Write($"{palette.ErrorFg.AnsiFg}✗ {truncated}{Reset}");
        }
        else if (helpText != null)
        {
            var truncated = helpText.Length > width - 6
                ? helpText[..(width - 6)]
                : helpText;
            Console.Write($"{palette.SecondaryText.AnsiFg}{Dim}{truncated}{Reset}");
        }
    }

    private static void ClearLine(int width)
    {
        var clearWidth = Math.Min(width, Console.WindowWidth - 1);
        Console.Write(new string(' ', Math.Max(0, clearWidth)));
    }

    private static void ClearFieldArea(int startRow, int width, bool hasSubtitle = false)
    {
        var height = hasSubtitle ? Height + 1 : Height;
        for (var i = 0; i < height; i++)
        {
            Console.SetCursorPosition(0, startRow + i);
            ClearLine(width + 2);
        }
    }
}
