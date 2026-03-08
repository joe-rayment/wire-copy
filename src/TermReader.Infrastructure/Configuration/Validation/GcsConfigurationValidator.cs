// Educational and personal use only.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TermReader.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates Google Cloud Storage configuration settings.
/// </summary>
public partial class GcsConfigurationValidator : IValidateOptions<GcsConfiguration>
{
    public ValidateOptionsResult Validate(string? name, GcsConfiguration options)
    {
        var errors = new List<string>();

        if (options.BucketName is not null)
        {
            if (options.BucketName.Length < 3 || options.BucketName.Length > 63)
            {
                errors.Add($"{nameof(GcsConfiguration.BucketName)} must be between 3 and 63 characters. Got: {options.BucketName.Length} characters");
            }
            else if (!BucketNamePattern().IsMatch(options.BucketName))
            {
                errors.Add($"{nameof(GcsConfiguration.BucketName)} must contain only lowercase letters, numbers, hyphens, underscores, and periods. Got: '{options.BucketName}'");
            }
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

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{1,61}[a-z0-9]$")]
    private static partial Regex BucketNamePattern();
}
