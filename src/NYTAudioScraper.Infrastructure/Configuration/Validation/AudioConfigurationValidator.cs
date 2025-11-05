using Microsoft.Extensions.Options;

namespace NYTAudioScraper.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates Audio configuration settings
/// </summary>
public class AudioConfigurationValidator : IValidateOptions<AudioConfiguration>
{
    private static readonly string[] ValidFormats = { "m4b", "mp3", "m4a", "aac" };
    private static readonly string[] ValidCodecs = { "aac", "libmp3lame", "mp3" };

    public ValidateOptionsResult Validate(string? name, AudioConfiguration options)
    {
        var errors = new List<string>();

        // Validate OutputFormat
        if (string.IsNullOrWhiteSpace(options.OutputFormat))
        {
            errors.Add($"{nameof(AudioConfiguration.OutputFormat)} cannot be empty.");
        }
        else if (!ValidFormats.Contains(options.OutputFormat.ToLowerInvariant()))
        {
            errors.Add($"{nameof(AudioConfiguration.OutputFormat)} must be one of: {string.Join(", ", ValidFormats)}. Got: '{options.OutputFormat}'");
        }

        // Validate Codec
        if (string.IsNullOrWhiteSpace(options.Codec))
        {
            errors.Add($"{nameof(AudioConfiguration.Codec)} cannot be empty.");
        }
        else if (!ValidCodecs.Contains(options.Codec.ToLowerInvariant()))
        {
            errors.Add($"{nameof(AudioConfiguration.Codec)} must be one of: {string.Join(", ", ValidCodecs)}. Got: '{options.Codec}'");
        }

        // Validate BitRate
        if (options.BitRate <= 0)
        {
            errors.Add($"{nameof(AudioConfiguration.BitRate)} must be positive. Got: {options.BitRate}");
        }

        if (options.BitRate < 32000)
        {
            errors.Add($"{nameof(AudioConfiguration.BitRate)} is too low (< 32kbps). Audio quality will be poor. Got: {options.BitRate}");
        }

        if (options.BitRate > 320000)
        {
            errors.Add($"{nameof(AudioConfiguration.BitRate)} is unusually high (> 320kbps). Got: {options.BitRate}");
        }

        // Validate SampleRate
        if (options.SampleRate <= 0)
        {
            errors.Add($"{nameof(AudioConfiguration.SampleRate)} must be positive. Got: {options.SampleRate}");
        }

        var validSampleRates = new[] { 8000, 11025, 16000, 22050, 44100, 48000 };
        if (!validSampleRates.Contains(options.SampleRate))
        {
            errors.Add($"{nameof(AudioConfiguration.SampleRate)} should be a standard value: {string.Join(", ", validSampleRates)}. Got: {options.SampleRate}");
        }

        // Validate Channels
        if (options.Channels <= 0)
        {
            errors.Add($"{nameof(AudioConfiguration.Channels)} must be positive. Got: {options.Channels}");
        }

        if (options.Channels > 2)
        {
            errors.Add($"{nameof(AudioConfiguration.Channels)} should be 1 (mono) or 2 (stereo). Got: {options.Channels}");
        }

        // Validate OutputDirectory
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            errors.Add($"{nameof(AudioConfiguration.OutputDirectory)} cannot be empty.");
        }
        else
        {
            try
            {
                // Check for invalid path characters
                if (options.OutputDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    errors.Add($"{nameof(AudioConfiguration.OutputDirectory)} contains invalid path characters.");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{nameof(AudioConfiguration.OutputDirectory)} is invalid: {ex.Message}");
            }
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
