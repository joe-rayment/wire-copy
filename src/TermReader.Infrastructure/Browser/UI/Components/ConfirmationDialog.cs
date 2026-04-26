// Licensed under the MIT License. See LICENSE in the repository root.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Components;

/// <summary>
/// Renders a centered confirmation dialog with key hints.
/// For simple y/n confirmations that don't require typed input.
///
/// Visual layout:
///   ╭─ Title ──────────────────────────────╮
///   │ Message text                          │
///   ╰──────────────────────────────────────╯
///
///     y: confirm   Esc: cancel
/// </summary>
internal static class ConfirmationDialog
{
    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Shows a confirmation dialog and waits for a single keypress.
    /// Returns true if the user presses 'y', false for Escape or any other key.
    /// </summary>
    public static async Task<bool> ConfirmAsync(
        IInputHandler input,
        string title,
        string message,
        ThemePalette palette,
        CancellationToken ct,
        bool isDestructive = false)
    {
        var termWidth = Math.Max(40, Console.WindowWidth);
        var boxWidth = Math.Min(termWidth - 4, 60);
        var borderColor = isDestructive ? palette.ErrorFg : palette.HeaderBorderFg;

        Console.Clear();

        // Center vertically
        var startRow = Math.Max(1, (Console.WindowHeight / 2) - 3);

        // Title bar
        var titlePart = $"\u2500 {title} ";
        var remainingRule = Math.Max(0, boxWidth - titlePart.Length - 2);
        Console.SetCursorPosition(2, startRow);
        Console.Write(
            $"{borderColor.AnsiFg}\u256d{titlePart}" +
            $"{new string('\u2500', remainingRule)}\u256e{Reset}");

        // Message line(s) — wrap if needed
        var innerWidth = boxWidth - 4;
        var lines = WrapText(message, innerWidth);
        var row = startRow + 1;
        foreach (var line in lines)
        {
            Console.SetCursorPosition(2, row);
            var textColor = isDestructive ? palette.ErrorFg : palette.PrimaryText;
            Console.Write(
                $"{borderColor.AnsiFg}\u2502 " +
                $"{textColor.AnsiFg}{line.PadRight(innerWidth)}" +
                $" {borderColor.AnsiFg}\u2502{Reset}");
            row++;
        }

        // Bottom border
        Console.SetCursorPosition(2, row);
        Console.Write($"{borderColor.AnsiFg}\u2570{new string('\u2500', boxWidth - 2)}\u256f{Reset}");
        row += 2;

        // Key hints
        Console.SetCursorPosition(4, row);
        Console.Write(
            $"{palette.GetAccentFg().AnsiFg}y{palette.SecondaryText.AnsiFg}: confirm   " +
            $"{palette.GetAccentFg().AnsiFg}Esc{palette.SecondaryText.AnsiFg}: cancel{Reset}");

        // Wait for keypress
        ct.ThrowIfCancellationRequested();
        var command = await input.WaitForInputAsync(ct);
        return command.RawKeyChar is 'y' or 'Y';
    }

    /// <summary>
    /// Shows a destructive confirmation dialog requiring the user to type a specific word.
    /// Returns true only if the user types the exact confirmation word.
    /// </summary>
    public static async Task<bool> ConfirmDestructiveAsync(
        IInputHandler input,
        string title,
        string message,
        IReadOnlyList<string>? affectedItems,
        ThemePalette palette,
        int terminalHeight,
        CancellationToken ct)
    {
        var termWidth = Math.Max(40, Console.WindowWidth);
        var boxWidth = Math.Min(termWidth - 4, 60);
        var fieldWidth = boxWidth;

        Console.Clear();

        var row = 1;

        // Title bar (error-colored for destructive)
        var titlePart = $"\u2500 {title} ";
        var remainingRule = Math.Max(0, boxWidth - titlePart.Length - 2);
        Console.SetCursorPosition(2, row);
        Console.Write(
            $"{palette.ErrorFg.AnsiFg}\u256d{titlePart}" +
            $"{new string('\u2500', remainingRule)}\u256e{Reset}");
        row++;

        // Warning message inside box
        var innerWidth = boxWidth - 4;
        var msgLines = WrapText(message, innerWidth);
        foreach (var line in msgLines)
        {
            Console.SetCursorPosition(2, row);
            Console.Write(
                $"{palette.ErrorFg.AnsiFg}\u2502 " +
                $"{palette.ErrorFg.AnsiFg}{line.PadRight(innerWidth)}" +
                $" {palette.ErrorFg.AnsiFg}\u2502{Reset}");
            row++;
        }

        // Bottom border of title box
        Console.SetCursorPosition(2, row);
        Console.Write($"{palette.ErrorFg.AnsiFg}\u2570{new string('\u2500', boxWidth - 2)}\u256f{Reset}");
        row += 2;

        // Affected items list (if provided)
        if (affectedItems is { Count: > 0 })
        {
            Console.SetCursorPosition(4, row);
            Console.Write($"{palette.SecondaryText.AnsiFg}Items to remove:{Reset}");
            row++;

            // Reserve space for the FormField below (Height=5 + padding)
            var maxItems = Math.Max(1, terminalHeight - row - FormField.Height - 4);
            var displayCount = Math.Min(affectedItems.Count, maxItems);

            for (var i = 0; i < displayCount; i++)
            {
                var displayTitle = affectedItems[i].Length > innerWidth - 4
                    ? affectedItems[i][..(innerWidth - 4)]
                    : affectedItems[i];
                Console.SetCursorPosition(6, row);
                Console.Write($"{palette.ErrorFg.AnsiFg}\u2022{Reset} {palette.PrimaryText.AnsiFg}{displayTitle}{Reset}");
                row++;
            }

            if (affectedItems.Count > displayCount)
            {
                Console.SetCursorPosition(6, row);
                Console.Write($"{palette.SecondaryText.AnsiFg}... and {affectedItems.Count - displayCount} more{Reset}");
                row++;
            }

            row++;
        }

        // FormField for "Type DELETE to confirm"
        var field = new FormFieldConfig
        {
            Label = "Confirm",
            Placeholder = "Type DELETE to confirm",
            Validate = v => string.Equals(v?.Trim(), "DELETE", StringComparison.Ordinal)
                ? null
                : "Type DELETE exactly to confirm this action",
            MaxLength = 10,
        };

        var result = await FormField.PromptAsync(input, field, palette, row, fieldWidth, ct);
        return result != null;
    }

    private static List<string> WrapText(string text, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            return [text];
        }

        var lines = new List<string>();
        var remaining = text;

        while (remaining.Length > maxWidth)
        {
            // Find last space within maxWidth
            var breakAt = remaining.LastIndexOf(' ', maxWidth);
            if (breakAt <= 0)
            {
                breakAt = maxWidth;
            }

            lines.Add(remaining[..breakAt]);
            remaining = remaining[breakAt..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            lines.Add(remaining);
        }

        return lines;
    }
}
