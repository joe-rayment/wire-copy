// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Application.DTOs;

/// <summary>
/// Cost estimate for TTS generation of a text input.
/// </summary>
public record TtsCostEstimate
{
    /// <summary>
    /// Gets the total number of characters in the input text.
    /// </summary>
    public required int CharacterCount { get; init; }

    /// <summary>
    /// Gets the number of chunks the text will be split into for API requests.
    /// </summary>
    public required int ChunkCount { get; init; }

    /// <summary>
    /// Gets the estimated cost in USD based on current API pricing.
    /// </summary>
    public required decimal EstimatedCostUsd { get; init; }

    /// <summary>
    /// Gets the estimated audio duration in minutes.
    /// </summary>
    public required double EstimatedDurationMinutes { get; init; }

    /// <summary>
    /// Gets a human-readable summary of the estimate.
    /// </summary>
    public string Summary => $"{CharacterCount:N0} chars, {ChunkCount} chunks, ~{EstimatedDurationMinutes:F1} min, ~${EstimatedCostUsd:F4}";
}
