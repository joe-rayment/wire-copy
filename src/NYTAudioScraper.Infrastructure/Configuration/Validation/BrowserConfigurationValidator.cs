using Microsoft.Extensions.Options;

namespace NYTAudioScraper.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates Browser configuration settings
/// </summary>
public class BrowserConfigurationValidator : IValidateOptions<BrowserConfiguration>
{
    public ValidateOptionsResult Validate(string? name, BrowserConfiguration options)
    {
        var errors = new List<string>();

        // Validate ImplicitWaitSeconds
        if (options.ImplicitWaitSeconds < 0)
        {
            errors.Add($"{nameof(BrowserConfiguration.ImplicitWaitSeconds)} cannot be negative. Got: {options.ImplicitWaitSeconds}");
        }

        if (options.ImplicitWaitSeconds > 300)
        {
            errors.Add($"{nameof(BrowserConfiguration.ImplicitWaitSeconds)} is too high (> 5 minutes). Got: {options.ImplicitWaitSeconds}");
        }

        // Validate PageLoadTimeoutSeconds
        if (options.PageLoadTimeoutSeconds <= 0)
        {
            errors.Add($"{nameof(BrowserConfiguration.PageLoadTimeoutSeconds)} must be positive. Got: {options.PageLoadTimeoutSeconds}");
        }

        if (options.PageLoadTimeoutSeconds < 5)
        {
            errors.Add($"{nameof(BrowserConfiguration.PageLoadTimeoutSeconds)} is too low (< 5 seconds). Pages may not load completely. Got: {options.PageLoadTimeoutSeconds}");
        }

        if (options.PageLoadTimeoutSeconds > 300)
        {
            errors.Add($"{nameof(BrowserConfiguration.PageLoadTimeoutSeconds)} is too high (> 5 minutes). Got: {options.PageLoadTimeoutSeconds}");
        }

        // Validate UserAgent
        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            errors.Add($"{nameof(BrowserConfiguration.UserAgent)} cannot be empty.");
        }
        else if (options.UserAgent.Length < 10)
        {
            errors.Add($"{nameof(BrowserConfiguration.UserAgent)} seems too short to be valid. Got: '{options.UserAgent}'");
        }

        // Validate ExperimentalOptions
        if (options.ExperimentalOptions == null)
        {
            errors.Add($"{nameof(BrowserConfiguration.ExperimentalOptions)} cannot be null.");
        }
        else if (options.ExperimentalOptions.Length % 2 != 0)
        {
            errors.Add($"{nameof(BrowserConfiguration.ExperimentalOptions)} must contain key-value pairs (even number of elements). Got: {options.ExperimentalOptions.Length} elements");
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
