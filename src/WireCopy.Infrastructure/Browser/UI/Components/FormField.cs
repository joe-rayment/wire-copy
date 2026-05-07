// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Reusable inline form field component that renders a labeled text input
/// with a rounded border, placeholder, validation, and error display.
///
/// Visual layout (5 lines):
///   Label
///   ╭──────────────────────────────────────╮
///   │ input text                           │
///   ╰──────────────────────────────────────╯
///   ✓ Help text  — or —  ✗ Error message
/// </summary>
internal static class FormField
{
    /// <summary>
    /// Total number of terminal rows this component occupies.
    /// </summary>
    public const int Height = 5;

    private const string Reset = "\x1b[0m";
    private const string Dim = "\x1b[2m";

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

        while (!ct.IsCancellationRequested)
        {
            // Render the field chrome (label, borders, help/error)
            RenderFieldChrome(palette, field, startRow, boxWidth, innerWidth, errorMessage);

            // Position cursor inside the box for input
            var inputRow = startRow + 2; // label=0, top border=1, input=2
            var inputCol = 2; // "│ " = 2 chars

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

            // Use the input handler to capture text
            var value = await input.PromptForInputAsync(
                string.Empty,
                ct,
                isSecret: field.IsSecret,
                row: inputRow,
                col: inputCol,
                initialInput: field.InitialValue,
                interceptKey: interceptKey).ConfigureAwait(false);

            if (value == null)
            {
                // User pressed Escape — cancel
                ClearFieldArea(startRow, boxWidth);
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

            // Success — show brief confirmation and return
            RenderValidationMessage(palette, startRow + 4, boxWidth, null, field.HelpText);
            return value;
        }

        return null;
    }

    /// <summary>
    /// Renders the static parts of the field: label, border box, and status line.
    /// </summary>
    private static void RenderFieldChrome(
        ThemePalette palette,
        FormFieldConfig field,
        int startRow,
        int boxWidth,
        int innerWidth,
        string? errorMessage)
    {
        // Row 0: Label
        Console.SetCursorPosition(0, startRow);
        ClearLine(boxWidth + 2);
        Console.SetCursorPosition(2, startRow);
        Console.Write($"{palette.PrimaryText.AnsiFg}{field.Label}{Reset}");

        // Row 1: Top border ╭───╮
        Console.SetCursorPosition(0, startRow + 1);
        ClearLine(boxWidth + 2);
        Console.SetCursorPosition(2, startRow + 1);
        Console.Write($"{palette.HeaderBorderFg.AnsiFg}\u256d{new string('\u2500', boxWidth - 2)}\u256e{Reset}");

        // Row 2: Input area │ ... │
        Console.SetCursorPosition(0, startRow + 2);
        ClearLine(boxWidth + 2);
        Console.SetCursorPosition(2, startRow + 2);
        WriteInputRow(palette, field, innerWidth);

        // Row 3: Bottom border ╰───╯
        Console.SetCursorPosition(0, startRow + 3);
        ClearLine(boxWidth + 2);
        Console.SetCursorPosition(2, startRow + 3);
        Console.Write($"{palette.HeaderBorderFg.AnsiFg}\u2570{new string('\u2500', boxWidth - 2)}\u256f{Reset}");

        // Row 4: Validation message or help text
        RenderValidationMessage(palette, startRow + 4, boxWidth, errorMessage, field.HelpText);
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
            $"{palette.HeaderBorderFg.AnsiFg}\u2502 " +
            $"{palette.PrimaryText.AnsiFg}{displayContent}{padding}" +
            $" {palette.HeaderBorderFg.AnsiFg}\u2502{Reset}");
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
            Console.Write($"{palette.ErrorFg.AnsiFg}\u2717 {truncated}{Reset}");
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

    private static void ClearFieldArea(int startRow, int width)
    {
        for (var i = 0; i < Height; i++)
        {
            Console.SetCursorPosition(0, startRow + i);
            ClearLine(width + 2);
        }
    }
}
