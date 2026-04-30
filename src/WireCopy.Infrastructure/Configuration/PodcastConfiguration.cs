// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// Configuration for podcast audio assembly and feed generation.
/// </summary>
public class PodcastConfiguration
{
    public const string SectionName = "Podcast";

    /// <summary>
    /// Gets the audio codec for the output file. Default: aac.
    /// </summary>
    public string AudioCodec { get; init; } = "aac";

    /// <summary>
    /// Gets the audio bitrate (e.g., "64k", "128k"). Default: 64k.
    /// </summary>
    public string AudioBitrate { get; init; } = "64k";

    /// <summary>
    /// Gets the number of audio channels. 1 = mono, 2 = stereo. Default: 1.
    /// </summary>
    public int AudioChannels { get; init; } = 1;

    /// <summary>
    /// Gets the audio sample rate in Hz. Default: 44100.
    /// </summary>
    public int SampleRate { get; init; } = 44100;

    /// <summary>
    /// Gets an optional directory for temporary audio files.
    /// Null uses the system temp directory.
    /// </summary>
    public string? TempDirectory { get; init; }

    /// <summary>
    /// Gets whether to use Nero chapter format for M4B files. Default: true.
    /// </summary>
    public bool UseNeroChapters { get; init; } = true;

    /// <summary>
    /// Gets the podcast title. Default: Wire Copy Podcast.
    /// </summary>
    public string Title { get; init; } = "Wire Copy Podcast";

    /// <summary>
    /// Gets the podcast description.
    /// </summary>
    public string Description { get; init; } = "Articles converted to audio by WireCopy.";

    /// <summary>
    /// Gets the podcast author. Default: WireCopy.
    /// </summary>
    public string Author { get; init; } = "WireCopy";

    /// <summary>
    /// Gets the podcast language code. Default: en-us.
    /// </summary>
    public string Language { get; init; } = "en-us";

    /// <summary>
    /// Gets an optional URL for the podcast cover image.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Gets the podcast category. Default: News.
    /// </summary>
    public string Category { get; init; } = "News";

    /// <summary>
    /// Gets an optional podcast subcategory.
    /// </summary>
    public string? Subcategory { get; init; }

    /// <summary>
    /// Gets whether the podcast contains explicit content. Default: false.
    /// </summary>
    public bool Explicit { get; init; } = false;

    /// <summary>
    /// Gets the minimum word count required for an article to be sent to TTS.
    /// Articles below this threshold are skipped to avoid generating audio from garbage content.
    /// Default: 100.
    /// </summary>
    public int MinimumWordCount { get; init; } = 100;

    /// <summary>
    /// Gets the directory where final podcast M4B files are written.
    /// Null resolves to <c>{LocalApplicationData}/WireCopy/output/</c> at runtime
    /// (cross-platform: ~/.local/share/WireCopy/output on Linux,
    /// Library/Application Support/WireCopy/output on macOS,
    /// %LocalAppData%/WireCopy/output on Windows).
    /// Test code may override this to point at a scratch directory.
    /// </summary>
    public string? OutputFolderPath { get; init; }

    /// <summary>
    /// Gets the retention window for files in <see cref="OutputFolderPath"/>, in hours.
    /// Files older than this are auto-purged on app start. Default: 36.
    /// Only *.m4b and *.mp3 files are eligible for purge.
    /// </summary>
    public int OutputRetentionHours { get; init; } = 36;

    /// <summary>
    /// Resolves the effective output folder path, defaulting to
    /// <c>{LocalApplicationData}/WireCopy/output</c> when not explicitly set.
    /// </summary>
    public string ResolveOutputFolderPath()
    {
        if (!string.IsNullOrWhiteSpace(OutputFolderPath))
        {
            return OutputFolderPath;
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localData, "WireCopy", "output");
    }
}
