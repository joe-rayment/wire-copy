// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Application.DTOs;

/// <summary>
/// Result of validating TTS API credentials by making a minimal test call.
/// </summary>
public record TtsValidationResult
{
    /// <summary>
    /// Gets whether the API key is valid and operational.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Gets the human-readable error message if validation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the error code categorizing the failure (e.g. "invalid_key", "insufficient_credits").
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static TtsValidationResult Valid()
    {
        return new TtsValidationResult { IsValid = true };
    }

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static TtsValidationResult Invalid(string errorMessage, string errorCode)
    {
        return new TtsValidationResult
        {
            IsValid = false,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode,
        };
    }
}
