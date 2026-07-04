// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Options;

namespace WireCopy.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates OpenAI TTS configuration settings.
/// </summary>
public class OpenAiTtsConfigurationValidator : IValidateOptions<OpenAiTtsConfiguration>
{
    private static readonly HashSet<string> ValidOutputFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "opus", "aac", "flac", "wav", "pcm",
    };

    public ValidateOptionsResult Validate(string? name, OpenAiTtsConfiguration options)
    {
        var errors = new List<string>();

        // Do NOT validate ApiKey — it's optional at config time
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            errors.Add($"{nameof(OpenAiTtsConfiguration.Model)} cannot be empty.");
        }

        // Voice is deliberately NOT whitelisted: the API's catalogue evolves (verse/marin/cedar
        // post-date the original ten) and OpenAiTtsService forwards the string VERBATIM, so a hard
        // whitelist here made valid new voices unusable at options resolution. The API is the
        // source of truth — a genuinely bad voice surfaces as an actionable 400 at key-validation
        // or generation time. Only emptiness is rejected.
        if (string.IsNullOrWhiteSpace(options.Voice))
        {
            errors.Add($"{nameof(OpenAiTtsConfiguration.Voice)} cannot be empty.");
        }

        if (options.Speed < 0.25f || options.Speed > 4.0f)
        {
            errors.Add($"{nameof(OpenAiTtsConfiguration.Speed)} must be between 0.25 and 4.0. Got: {options.Speed}");
        }

        if (!ValidOutputFormats.Contains(options.OutputFormat))
        {
            errors.Add($"{nameof(OpenAiTtsConfiguration.OutputFormat)} must be one of: {string.Join(", ", ValidOutputFormats)}. Got: '{options.OutputFormat}'");
        }

        if (options.MaxChunkSize <= 0 || options.MaxChunkSize > 4096)
        {
            errors.Add($"{nameof(OpenAiTtsConfiguration.MaxChunkSize)} must be between 1 and 4096. Got: {options.MaxChunkSize}");
        }

        if (options.MaxBudgetUsd <= 0)
        {
            errors.Add($"{nameof(OpenAiTtsConfiguration.MaxBudgetUsd)} must be positive. Got: {options.MaxBudgetUsd}");
        }

        if (options.MaxRetries < 0 || options.MaxRetries > 10)
        {
            errors.Add($"{nameof(OpenAiTtsConfiguration.MaxRetries)} must be between 0 and 10. Got: {options.MaxRetries}");
        }

        if (options.RetryBaseDelayMs < 0)
        {
            errors.Add($"{nameof(OpenAiTtsConfiguration.RetryBaseDelayMs)} cannot be negative. Got: {options.RetryBaseDelayMs}");
        }

        if (options.InterChunkDelayMs < 0)
        {
            errors.Add($"{nameof(OpenAiTtsConfiguration.InterChunkDelayMs)} cannot be negative. Got: {options.InterChunkDelayMs}");
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
