// <copyright file="NYTConfigurationValidator.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using Microsoft.Extensions.Options;

namespace NYTAudioScraper.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates NYT configuration settings
/// </summary>
public class NYTConfigurationValidator : IValidateOptions<NYTConfiguration>
{
    public ValidateOptionsResult Validate(string? name, NYTConfiguration options)
    {
        var errors = new List<string>();

        // Validate BaseUrl
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            errors.Add($"{nameof(NYTConfiguration.BaseUrl)} cannot be empty.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{nameof(NYTConfiguration.BaseUrl)} must be a valid HTTP/HTTPS URL. Got: '{options.BaseUrl}'");
        }
        else if (!options.BaseUrl.Contains("nytimes.com", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{nameof(NYTConfiguration.BaseUrl)} must be from nytimes.com domain. Got: '{options.BaseUrl}'");
        }

        // Validate MaxArticles
        if (options.MaxArticles <= 0)
        {
            errors.Add($"{nameof(NYTConfiguration.MaxArticles)} must be positive. Got: {options.MaxArticles}");
        }

        if (options.MaxArticles > 1000)
        {
            errors.Add($"{nameof(NYTConfiguration.MaxArticles)} cannot exceed 1000. Got: {options.MaxArticles}");
        }

        // Validate RateLimitDelayMs
        if (options.RateLimitDelayMs < 0)
        {
            errors.Add($"{nameof(NYTConfiguration.RateLimitDelayMs)} cannot be negative. Got: {options.RateLimitDelayMs}");
        }

        if (options.RateLimitDelayMs < 1000)
        {
            errors.Add($"{nameof(NYTConfiguration.RateLimitDelayMs)} should be at least 1000ms (1 second) to respect rate limits. Got: {options.RateLimitDelayMs}ms. " +
                      "Setting lower values may violate NYT's Terms of Service.");
        }

        // Validate credentials if login is not skipped
        if (!options.SkipLogin)
        {
            if (string.IsNullOrWhiteSpace(options.Email))
            {
                errors.Add($"{nameof(NYTConfiguration.Email)} is required when SkipLogin is false. Set it via configuration or use --skip-login flag.");
            }

            if (string.IsNullOrWhiteSpace(options.Password))
            {
                errors.Add($"{nameof(NYTConfiguration.Password)} is required when SkipLogin is false. Set it via configuration or use --skip-login flag.");
            }
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
