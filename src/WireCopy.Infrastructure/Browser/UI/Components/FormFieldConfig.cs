// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Configuration for a form field — label, validation, and display options.
/// </summary>
internal sealed class FormFieldConfig
{
    public required string Label { get; init; }

    public string? Placeholder { get; init; }

    public string? HelpText { get; init; }

    public bool IsSecret { get; init; }

    public Func<string, string?>? Validate { get; init; }

    public int MaxLength { get; init; } = 256;

    public string? InitialValue { get; init; }

    /// <summary>
    /// Optional callback invoked when the user presses a printable character
    /// while editing the field. Returning <c>true</c> consumes the keystroke
    /// — the FormField's chrome (label, borders, validation row) is re-rendered
    /// and the input loop continues without inserting the character. Returning
    /// <c>false</c> lets the character fall through to normal text insertion.
    ///
    /// <para>
    /// Used to wire hotkey overlays into a FormField — e.g. the GCS bucket
    /// row uses this to render a verbose <c>?</c> help panel without leaving
    /// the input loop (workspace-dlq5).
    /// </para>
    /// </summary>
    public Func<char, bool>? OnExtraKey { get; init; }
}
