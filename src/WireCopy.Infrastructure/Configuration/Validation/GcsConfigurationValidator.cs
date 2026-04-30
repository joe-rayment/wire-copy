// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Options;

namespace WireCopy.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates Google Cloud Storage configuration settings.
/// </summary>
public class GcsConfigurationValidator : IValidateOptions<GcsConfiguration>
{
    public ValidateOptionsResult Validate(string? name, GcsConfiguration options)
    {
        var errors = new List<string>();

        if (options.BucketName is not null && !GcsConfiguration.IsValidBucketName(options.BucketName))
        {
            errors.Add($"{nameof(GcsConfiguration.BucketName)} must be 3-63 characters of lowercase letters, numbers, hyphens, underscores, and periods. Got: '{options.BucketName}'");
        }

        if (options.ServiceAccountKeyPath is not null && !File.Exists(options.ServiceAccountKeyPath))
        {
            errors.Add($"{nameof(GcsConfiguration.ServiceAccountKeyPath)} file does not exist: '{options.ServiceAccountKeyPath}'");
        }

        if (string.IsNullOrWhiteSpace(options.BucketLocation))
        {
            errors.Add($"{nameof(GcsConfiguration.BucketLocation)} cannot be empty.");
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
