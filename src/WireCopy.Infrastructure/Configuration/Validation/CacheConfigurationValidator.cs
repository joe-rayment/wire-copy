// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Options;

namespace WireCopy.Infrastructure.Configuration.Validation;

/// <summary>
/// Validates Cache configuration settings.
/// </summary>
public class CacheConfigurationValidator : IValidateOptions<CacheConfiguration>
{
    public ValidateOptionsResult Validate(string? name, CacheConfiguration options)
    {
        var errors = new List<string>();

        if (options.MaxSizeBytes <= 0)
        {
            errors.Add($"{nameof(CacheConfiguration.MaxSizeBytes)} must be positive. Got: {options.MaxSizeBytes}");
        }

        if (options.MaxEntries <= 0)
        {
            errors.Add($"{nameof(CacheConfiguration.MaxEntries)} must be positive. Got: {options.MaxEntries}");
        }

        if (options.DefaultTtlSeconds <= 0)
        {
            errors.Add($"{nameof(CacheConfiguration.DefaultTtlSeconds)} must be positive. Got: {options.DefaultTtlSeconds}");
        }

        if (options.MaxEntrySizeBytes <= 0)
        {
            errors.Add($"{nameof(CacheConfiguration.MaxEntrySizeBytes)} must be positive. Got: {options.MaxEntrySizeBytes}");
        }

        if (options.MaxEntrySizeBytes > options.MaxSizeBytes)
        {
            errors.Add($"{nameof(CacheConfiguration.MaxEntrySizeBytes)} ({options.MaxEntrySizeBytes}) cannot exceed {nameof(CacheConfiguration.MaxSizeBytes)} ({options.MaxSizeBytes}).");
        }

        if (options.EvictionSweepIntervalSeconds <= 0)
        {
            errors.Add($"{nameof(CacheConfiguration.EvictionSweepIntervalSeconds)} must be positive. Got: {options.EvictionSweepIntervalSeconds}");
        }

        if (options.IdleThresholdMs < 0)
        {
            errors.Add($"{nameof(CacheConfiguration.IdleThresholdMs)} cannot be negative. Got: {options.IdleThresholdMs}");
        }

        if (options.PreloadDelayMs <= 0)
        {
            errors.Add($"{nameof(CacheConfiguration.PreloadDelayMs)} must be positive. Got: {options.PreloadDelayMs}");
        }

        if (options.CircuitBreakerCooldownSeconds <= 0)
        {
            errors.Add($"{nameof(CacheConfiguration.CircuitBreakerCooldownSeconds)} must be positive. Got: {options.CircuitBreakerCooldownSeconds}");
        }

        return errors.Any()
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
