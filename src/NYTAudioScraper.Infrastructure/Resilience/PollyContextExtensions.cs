// <copyright file="PollyContextExtensions.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>


using Microsoft.Extensions.Logging;
using Polly;

namespace NYTAudioScraper.Infrastructure.Resilience;

/// <summary>
/// Extension methods for Polly Context
/// </summary>
public static class PollyContextExtensions
{
    private const string LoggerKey = "ILogger";

    public static Context WithLogger(this Context context, ILogger logger)
    {
        context[LoggerKey] = logger;
        return context;
    }

    public static ILogger? GetLogger(this Context context)
    {
        if (context.TryGetValue(LoggerKey, out var logger))
        {
            return logger as ILogger;
        }

        return null;
    }
}
