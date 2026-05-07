// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Options;

namespace WireCopy.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates <see cref="OpenAiHierarchyConfiguration"/> — model name and
/// numeric ceilings must be sensible. The API key lives elsewhere
/// (<see cref="OpenAiTtsConfiguration"/> / settings store) and is not
/// validated here.
/// </summary>
public class OpenAiHierarchyConfigurationValidator : IValidateOptions<OpenAiHierarchyConfiguration>
{
    public ValidateOptionsResult Validate(string? name, OpenAiHierarchyConfiguration options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            errors.Add($"{nameof(OpenAiHierarchyConfiguration.Model)} cannot be empty.");
        }

        if (options.MaxTokens <= 0)
        {
            errors.Add($"{nameof(OpenAiHierarchyConfiguration.MaxTokens)} must be positive. Got: {options.MaxTokens}");
        }

        if (options.AiCuratedCacheDays <= 0)
        {
            errors.Add($"{nameof(OpenAiHierarchyConfiguration.AiCuratedCacheDays)} must be positive. Got: {options.AiCuratedCacheDays}");
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
