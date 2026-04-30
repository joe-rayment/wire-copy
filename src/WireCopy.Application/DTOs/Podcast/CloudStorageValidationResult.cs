// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Result of validating a cloud storage connection.
/// </summary>
public record CloudStorageValidationResult
{
    /// <summary>
    /// Gets whether the validation succeeded.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Gets the user-friendly error message, if validation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the error type, if validation failed.
    /// </summary>
    public CloudStorageValidationErrorType? ErrorType { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    public static CloudStorageValidationResult Valid() =>
        new() { IsValid = true };

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    public static CloudStorageValidationResult Invalid(
        CloudStorageValidationErrorType errorType,
        string errorMessage) =>
        new() { IsValid = false, ErrorType = errorType, ErrorMessage = errorMessage };
}

/// <summary>
/// Types of cloud storage validation errors.
/// </summary>
public enum CloudStorageValidationErrorType
{
    /// <summary>Credentials are invalid or missing.</summary>
    CredentialsInvalid,

    /// <summary>The specified bucket was not found.</summary>
    BucketNotFound,

    /// <summary>Bucket creation failed.</summary>
    BucketCreationFailed,

    /// <summary>Access denied to the bucket or object.</summary>
    AccessDenied,

    /// <summary>Network connectivity error.</summary>
    NetworkError,

    /// <summary>The operation timed out.</summary>
    Timeout,
}
