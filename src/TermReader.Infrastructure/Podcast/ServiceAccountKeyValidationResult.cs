// Educational and personal use only.

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Result of validating a GCS service account key file.
/// </summary>
public sealed record ServiceAccountKeyValidationResult
{
    public bool IsValid { get; private init; }

    public string? ErrorMessage { get; private init; }

    public static ServiceAccountKeyValidationResult Valid() => new() { IsValid = true };

    public static ServiceAccountKeyValidationResult Invalid(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}
