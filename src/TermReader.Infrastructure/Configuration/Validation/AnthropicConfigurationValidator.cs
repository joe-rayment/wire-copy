// Educational and personal use only.

using Microsoft.Extensions.Options;

namespace TermReader.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates Anthropic configuration settings.
/// </summary>
public class AnthropicConfigurationValidator : IValidateOptions<AnthropicConfiguration>
{
    public ValidateOptionsResult Validate(string? name, AnthropicConfiguration options)
    {
        var errors = new List<string>();

        // Do NOT validate ApiKey — it's optional at config time
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            errors.Add($"{nameof(AnthropicConfiguration.Model)} cannot be empty.");
        }

        if (options.MaxTokens <= 0)
        {
            errors.Add($"{nameof(AnthropicConfiguration.MaxTokens)} must be positive. Got: {options.MaxTokens}");
        }

        if (options.MaxBudgetUsd <= 0)
        {
            errors.Add($"{nameof(AnthropicConfiguration.MaxBudgetUsd)} must be positive. Got: {options.MaxBudgetUsd}");
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
