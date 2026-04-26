// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Infrastructure.Browser.UI.Components;

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
}
