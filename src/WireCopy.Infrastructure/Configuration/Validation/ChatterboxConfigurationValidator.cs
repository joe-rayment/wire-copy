// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Options;

namespace WireCopy.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates Chatterbox narration engine configuration settings.
/// </summary>
public class ChatterboxConfigurationValidator : IValidateOptions<ChatterboxConfiguration>
{
    private static readonly HashSet<string> ValidDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "cuda", "mps", "cpu",
    };

    public ValidateOptionsResult Validate(string? name, ChatterboxConfiguration options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.UvPath))
        {
            errors.Add($"{nameof(ChatterboxConfiguration.UvPath)} cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.WorkerRelativePath))
        {
            errors.Add($"{nameof(ChatterboxConfiguration.WorkerRelativePath)} cannot be empty.");
        }

        if (!ValidDevices.Contains(options.Device))
        {
            errors.Add($"{nameof(ChatterboxConfiguration.Device)} must be one of: {string.Join(", ", ValidDevices)}. Got: '{options.Device}'");
        }

        if (options.CfgWeight < 0f || options.CfgWeight > 1f)
        {
            errors.Add($"{nameof(ChatterboxConfiguration.CfgWeight)} must be between 0.0 and 1.0. Got: {options.CfgWeight}");
        }

        if (options.DefaultExaggeration < 0f || options.DefaultExaggeration > 2f)
        {
            errors.Add($"{nameof(ChatterboxConfiguration.DefaultExaggeration)} must be between 0.0 and 2.0. Got: {options.DefaultExaggeration}");
        }

        if (options.MaxChunkSize < 50 || options.MaxChunkSize > 1000)
        {
            errors.Add($"{nameof(ChatterboxConfiguration.MaxChunkSize)} must be between 50 and 1000. Got: {options.MaxChunkSize}");
        }

        if (options.StartTimeoutSeconds <= 0)
        {
            errors.Add($"{nameof(ChatterboxConfiguration.StartTimeoutSeconds)} must be positive. Got: {options.StartTimeoutSeconds}");
        }

        if (options.LoadTimeoutSeconds <= 0)
        {
            errors.Add($"{nameof(ChatterboxConfiguration.LoadTimeoutSeconds)} must be positive. Got: {options.LoadTimeoutSeconds}");
        }

        if (options.SpeakTimeoutSecondsPerChunk <= 0)
        {
            errors.Add($"{nameof(ChatterboxConfiguration.SpeakTimeoutSecondsPerChunk)} must be positive. Got: {options.SpeakTimeoutSecondsPerChunk}");
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
