// Educational and personal use only.

namespace TermReader.Infrastructure.Podcast.Cache;

/// <summary>
/// Configuration for the file-system TTS audio cache.
/// </summary>
public class TtsAudioCacheConfiguration
{
    public const string SectionName = "TtsAudioCache";

    /// <summary>
    /// Gets the base directory for cached audio files.
    /// Defaults to {LocalAppData}/TermReader/tts-cache.
    /// </summary>
    public string? BasePath { get; init; }

    /// <summary>
    /// Gets the maximum cache size in bytes. Default: 500 MB.
    /// </summary>
    public long MaxSizeBytes { get; init; } = 500L * 1024 * 1024;

    /// <summary>
    /// Gets the time-to-live for cache entries. Default: 30 days.
    /// </summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Resolves the effective base path, defaulting to LocalAppData if not configured.
    /// </summary>
    public string GetEffectiveBasePath()
    {
        if (!string.IsNullOrWhiteSpace(BasePath))
        {
            return BasePath;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "TermReader", "tts-cache");
    }
}
