// <copyright file="ElevenLabsConfigurationValidator.cs" company="NYTAudioScraper">
// Copyright (c) NYTAudioScraper. All rights reserved.
// </copyright>

using Microsoft.Extensions.Options;

namespace NYTAudioScraper.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates ElevenLabs configuration settings
/// </summary>
public class ElevenLabsConfigurationValidator : IValidateOptions<ElevenLabsConfiguration>
{
    public ValidateOptionsResult Validate(string? name, ElevenLabsConfiguration options)
    {
        var errors = new List<string>();

        // Validate ApiKey
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            errors.Add($"{nameof(ElevenLabsConfiguration.ApiKey)} is required. Set it via configuration or environment variable.");
        }

        // Validate BaseUrl
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            errors.Add($"{nameof(ElevenLabsConfiguration.BaseUrl)} cannot be empty.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"{nameof(ElevenLabsConfiguration.BaseUrl)} must be a valid HTTP/HTTPS URL. Got: '{options.BaseUrl}'");
        }

        // Validate DefaultVoiceId
        if (string.IsNullOrWhiteSpace(options.DefaultVoiceId))
        {
            errors.Add($"{nameof(ElevenLabsConfiguration.DefaultVoiceId)} cannot be empty.");
        }

        // Validate Model
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            errors.Add($"{nameof(ElevenLabsConfiguration.Model)} cannot be empty.");
        }

        // Validate CostPerCharacter
        if (options.CostPerCharacter < 0)
        {
            errors.Add($"{nameof(ElevenLabsConfiguration.CostPerCharacter)} cannot be negative. Got: {options.CostPerCharacter}");
        }

        if (options.CostPerCharacter > 1.0m)
        {
            errors.Add($"{nameof(ElevenLabsConfiguration.CostPerCharacter)} seems unreasonably high (>$1 per character). Got: {options.CostPerCharacter}");
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
