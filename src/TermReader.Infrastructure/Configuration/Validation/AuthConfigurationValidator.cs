// Educational and personal use only.

using Microsoft.Extensions.Options;

namespace TermReader.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates authentication configuration settings.
/// </summary>
public class AuthConfigurationValidator : IValidateOptions<AuthConfiguration>
{
    public ValidateOptionsResult Validate(string? name, AuthConfiguration options)
    {
        var errors = new List<string>();

        // Validate BaseUrl
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            errors.Add($"{nameof(AuthConfiguration.BaseUrl)} cannot be empty.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{nameof(AuthConfiguration.BaseUrl)} must be a valid HTTP/HTTPS URL. Got: '{options.BaseUrl}'");
        }
        else if (!options.BaseUrl.Contains("nytimes.com", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{nameof(AuthConfiguration.BaseUrl)} must be from nytimes.com domain. Got: '{options.BaseUrl}'");
        }

        // Validate MaxArticles
        if (options.MaxArticles <= 0)
        {
            errors.Add($"{nameof(AuthConfiguration.MaxArticles)} must be positive. Got: {options.MaxArticles}");
        }

        if (options.MaxArticles > 1000)
        {
            errors.Add($"{nameof(AuthConfiguration.MaxArticles)} cannot exceed 1000. Got: {options.MaxArticles}");
        }

        // Validate RateLimitDelayMs
        if (options.RateLimitDelayMs < 0)
        {
            errors.Add($"{nameof(AuthConfiguration.RateLimitDelayMs)} cannot be negative. Got: {options.RateLimitDelayMs}");
        }

        if (options.RateLimitDelayMs < 1000)
        {
            errors.Add($"{nameof(AuthConfiguration.RateLimitDelayMs)} should be at least 1000ms (1 second) to respect rate limits. Got: {options.RateLimitDelayMs}ms. " +
                      "Setting lower values may violate the site's Terms of Service.");
        }

        // Validate credentials if login is not skipped
        if (!options.SkipLogin)
        {
            if (string.IsNullOrWhiteSpace(options.Email))
            {
                errors.Add($"{nameof(AuthConfiguration.Email)} is required when SkipLogin is false. Set it via configuration or use --skip-login flag.");
            }

            if (string.IsNullOrWhiteSpace(options.Password))
            {
                errors.Add($"{nameof(AuthConfiguration.Password)} is required when SkipLogin is false. Set it via configuration or use --skip-login flag.");
            }
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
