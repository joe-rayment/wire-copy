// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Defines a single step in a multi-step wizard flow.
/// Each step has a title, optional description, one or more form fields,
/// and an optional async validation gate.
/// </summary>
internal sealed class WizardStep
{
    /// <summary>
    /// Step title shown in the header.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Optional description shown below the header.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Form fields to collect input for this step.
    /// </summary>
    public required List<FormFieldConfig> Fields { get; init; }

    /// <summary>
    /// Optional async validation that runs after all fields pass individual validation.
    /// Receives all collected values (from this step and previous steps).
    /// Returns an error message string, or null if valid.
    /// </summary>
    public Func<Dictionary<string, string>, Task<string?>>? OnValidateAsync { get; init; }

    /// <summary>
    /// Whether this step can be skipped (Enter with empty fields advances).
    /// </summary>
    public bool IsOptional { get; init; }
}
