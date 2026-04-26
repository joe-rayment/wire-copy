// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Options;
using TermReader.Infrastructure.Podcast.Cache;

namespace TermReader.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates TTS audio cache configuration settings.
/// </summary>
public class TtsAudioCacheConfigurationValidator : IValidateOptions<TtsAudioCacheConfiguration>
{
    public ValidateOptionsResult Validate(string? name, TtsAudioCacheConfiguration options)
    {
        var errors = new List<string>();

        if (options.MaxSizeBytes <= 0)
        {
            errors.Add($"{nameof(TtsAudioCacheConfiguration.MaxSizeBytes)} must be positive. Got: {options.MaxSizeBytes}");
        }

        if (options.Ttl <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(TtsAudioCacheConfiguration.Ttl)} must be positive. Got: {options.Ttl}");
        }

        if (!string.IsNullOrWhiteSpace(options.BasePath) && options.BasePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            errors.Add($"{nameof(TtsAudioCacheConfiguration.BasePath)} contains invalid path characters.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
