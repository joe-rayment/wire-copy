// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WireCopy.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates Podcast configuration settings.
/// </summary>
public partial class PodcastConfigurationValidator : IValidateOptions<PodcastConfiguration>
{
    public ValidateOptionsResult Validate(string? name, PodcastConfiguration options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.AudioCodec))
        {
            errors.Add($"{nameof(PodcastConfiguration.AudioCodec)} cannot be empty.");
        }

        if (!BitratePattern().IsMatch(options.AudioBitrate))
        {
            errors.Add($"{nameof(PodcastConfiguration.AudioBitrate)} must be in format '<number>k' (e.g., '64k', '128k'). Got: '{options.AudioBitrate}'");
        }

        if (options.AudioChannels is not (1 or 2))
        {
            errors.Add($"{nameof(PodcastConfiguration.AudioChannels)} must be 1 (mono) or 2 (stereo). Got: {options.AudioChannels}");
        }

        int[] validSampleRates = [8000, 16000, 22050, 44100, 48000];
        if (!validSampleRates.Contains(options.SampleRate))
        {
            errors.Add($"{nameof(PodcastConfiguration.SampleRate)} must be one of: {string.Join(", ", validSampleRates)}. Got: {options.SampleRate}");
        }

        if (options.TempDirectory is not null && !Directory.Exists(options.TempDirectory))
        {
            errors.Add($"{nameof(PodcastConfiguration.TempDirectory)} does not exist: '{options.TempDirectory}'");
        }

        if (string.IsNullOrWhiteSpace(options.Title))
        {
            errors.Add($"{nameof(PodcastConfiguration.Title)} cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.Author))
        {
            errors.Add($"{nameof(PodcastConfiguration.Author)} cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.Language))
        {
            errors.Add($"{nameof(PodcastConfiguration.Language)} cannot be empty.");
        }

        if (options.ImageUrl is not null)
        {
            if (!Uri.TryCreate(options.ImageUrl, UriKind.Absolute, out var parsedUri))
            {
                errors.Add($"{nameof(PodcastConfiguration.ImageUrl)} is not a valid absolute URL. Got: '{options.ImageUrl}'");
            }
            else if (parsedUri.Scheme is not ("http" or "https"))
            {
                errors.Add($"{nameof(PodcastConfiguration.ImageUrl)} must use http or https scheme. Got: '{parsedUri.Scheme}'");
            }
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    [GeneratedRegex(@"^\d+k$", RegexOptions.IgnoreCase)]
    private static partial Regex BitratePattern();
}
