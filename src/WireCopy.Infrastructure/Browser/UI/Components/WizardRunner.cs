// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.Interfaces.Browser;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Orchestrates a multi-step wizard flow, rendering step headers with progress
/// indicators, collecting form field values, and handling back/cancel navigation.
///
/// Visual layout:
///   ╭─ Title ─ Step N of M ─────────────────────╮
///   │ Description text                           │
///   ╰────────────────────────────────────────────╯
///
///     Field Label
///     ╭──────────────────────────────────────────╮
///     │ input                                    │
///     ╰──────────────────────────────────────────╯
///     help text
///
///   Enter:next   Esc:back   Step N of M
/// </summary>
internal static class WizardRunner
{
    private const string Reset = "\x1b[0m";
    private const string Dim = "\x1b[2m";

    /// <summary>
    /// Runs the wizard and returns all collected values, or null if cancelled.
    /// </summary>
    public static async Task<Dictionary<string, string>?> RunAsync(
        IInputHandler input,
        List<WizardStep> steps,
        ThemePalette palette,
        CancellationToken ct)
    {
        if (steps.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var allValues = new Dictionary<string, string>();
        var stepIndex = 0;

        while (stepIndex < steps.Count && !ct.IsCancellationRequested)
        {
            var step = steps[stepIndex];
            var totalSteps = steps.Count;
            var termWidth = Math.Max(40, Console.WindowWidth);
            var fieldWidth = Math.Min(termWidth - 6, 60);

            // Clear screen and render step header
            Console.Clear();
            var currentRow = RenderStepHeader(palette, step, stepIndex + 1, totalSteps, fieldWidth);
            currentRow++; // blank line after header

            // Collect values for each field in this step
            var stepValues = new Dictionary<string, string>();
            var cancelled = false;
            var fieldIndex = 0;

            while (fieldIndex < step.Fields.Count && !ct.IsCancellationRequested)
            {
                var field = step.Fields[fieldIndex];
                var fieldKey = field.Label;

                // Pre-fill from previously collected values (for back navigation)
                var preFilledField = field;
                if (allValues.TryGetValue(fieldKey, out var existingValue))
                {
                    preFilledField = new FormFieldConfig
                    {
                        Label = field.Label,
                        Placeholder = field.Placeholder,
                        HelpText = field.HelpText,
                        IsSecret = field.IsSecret,
                        Validate = field.Validate,
                        MaxLength = field.MaxLength,
                        InitialValue = existingValue,
                    };
                }

                var value = await FormField.PromptAsync(
                    input, preFilledField, palette, currentRow, fieldWidth, ct).ConfigureAwait(false);

                if (value == null)
                {
                    if (fieldIndex > 0)
                    {
                        // Go back to previous field in this step
                        fieldIndex--;

                        // Re-render: clear screen and re-render header
                        Console.Clear();
                        currentRow = RenderStepHeader(palette, step, stepIndex + 1, totalSteps, fieldWidth);
                        currentRow++;

                        // Re-render previous fields as static text
                        for (var i = 0; i < fieldIndex; i++)
                        {
                            currentRow = RenderCompletedField(palette, step.Fields[i], stepValues, currentRow);
                        }

                        continue;
                    }

                    if (stepIndex > 0)
                    {
                        // Go back to previous step
                        stepIndex--;
                        break;
                    }

                    // Cancel wizard (Escape on first field of first step)
                    cancelled = true;
                    break;
                }

                stepValues[fieldKey] = value;
                fieldIndex++;
                currentRow += FormField.Height + 1; // field height + blank line
            }

            if (cancelled)
            {
                return null;
            }

            if (fieldIndex < step.Fields.Count)
            {
                // Went back to previous step — loop continues
                continue;
            }

            // All fields collected — run step-level validation if present
            var mergedValues = new Dictionary<string, string>(allValues);
            foreach (var kv in stepValues)
            {
                mergedValues[kv.Key] = kv.Value;
            }

            if (step.OnValidateAsync != null)
            {
                var validationRow = currentRow;
                RenderSpinner(palette, validationRow, "Validating...");

                var error = await step.OnValidateAsync(mergedValues).ConfigureAwait(false);

                if (error != null)
                {
                    // Show error and retry this step
                    ClearLine(validationRow, fieldWidth + 6);
                    Console.SetCursorPosition(2, validationRow);
                    Console.Write($"{palette.ErrorFg.AnsiFg}\u2717 {error}{Reset}");

                    // Wait for keypress before retrying
                    await input.PromptForInputAsync(
                        string.Empty, ct, row: validationRow + 1, col: 2).ConfigureAwait(false);

                    // Don't advance — re-render this step
                    continue;
                }

                ClearLine(validationRow, fieldWidth + 6);
            }

            // Merge step values into all values
            foreach (var kv in stepValues)
            {
                allValues[kv.Key] = kv.Value;
            }

            stepIndex++;
        }

        if (stepIndex >= steps.Count)
        {
            return allValues;
        }

        return null;
    }

    /// <summary>
    /// Renders the step header with title, step indicator, and optional description.
    /// Returns the next available row after the header.
    /// </summary>
    private static int RenderStepHeader(
        ThemePalette palette, WizardStep step, int stepNumber, int totalSteps, int fieldWidth)
    {
        var stepIndicator = totalSteps > 1
            ? $" \u2500 Step {stepNumber} of {totalSteps} "
            : " ";

        var titlePart = $"\u2500 {step.Title} ";
        var headerContent = titlePart + stepIndicator;
        var remainingRule = Math.Max(0, fieldWidth - headerContent.Length - 2);

        // Row 0: Top border with title
        Console.SetCursorPosition(2, 1);
        Console.Write(
            $"{palette.HeaderBorderFg.AnsiFg}\u256d{headerContent}" +
            $"{new string('\u2500', remainingRule)}\u256e{Reset}");

        var row = 2;

        if (!string.IsNullOrEmpty(step.Description))
        {
            // Row 1: Description inside box
            var desc = step.Description.Length > fieldWidth - 4
                ? step.Description[..(fieldWidth - 4)]
                : step.Description;
            Console.SetCursorPosition(2, row);
            Console.Write(
                $"{palette.HeaderBorderFg.AnsiFg}\u2502 {palette.SecondaryText.AnsiFg}" +
                $"{desc.PadRight(fieldWidth - 4)}" +
                $"{palette.HeaderBorderFg.AnsiFg} \u2502{Reset}");
            row++;
        }

        // Bottom border
        Console.SetCursorPosition(2, row);
        Console.Write($"{palette.HeaderBorderFg.AnsiFg}\u2570{new string('\u2500', fieldWidth - 2)}\u256f{Reset}");
        row++;

        return row;
    }

    /// <summary>
    /// Renders a completed field as static text (non-editable).
    /// Returns the next available row.
    /// </summary>
    private static int RenderCompletedField(
        ThemePalette palette,
        FormFieldConfig field,
        Dictionary<string, string> values,
        int startRow)
    {
        var value = values.GetValueOrDefault(field.Label, string.Empty);
        var display = field.IsSecret && value.Length > 0
            ? new string('*', Math.Min(value.Length, 20))
            : value;

        Console.SetCursorPosition(2, startRow);
        Console.Write($"{palette.SecondaryText.AnsiFg}{field.Label}{Reset}");

        Console.SetCursorPosition(2, startRow + 1);
        Console.Write($"{Dim}{palette.SecondaryText.AnsiFg}{display}{Reset}");

        return startRow + 3; // label + value + blank line
    }

    private static void RenderSpinner(ThemePalette palette, int row, string message)
    {
        Console.SetCursorPosition(2, row);
        Console.Write($"{palette.GetAccentFg().AnsiFg}\u2847 {message}{Reset}");
    }

    private static void ClearLine(int row, int width)
    {
        Console.SetCursorPosition(0, row);
        var clearWidth = Math.Min(width, Console.WindowWidth - 1);
        Console.Write(new string(' ', Math.Max(0, clearWidth)));
    }
}
