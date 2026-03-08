// Educational and personal use only.

namespace TermReader.Infrastructure.Configuration;

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
    /// Gets the podcast title. Default: TermReader Podcast.
    /// </summary>
    public string Title { get; init; } = "TermReader Podcast";

    /// <summary>
    /// Gets the podcast description.
    /// </summary>
    public string Description { get; init; } = "Articles converted to audio by TermReader.";

    /// <summary>
    /// Gets the podcast author. Default: TermReader.
    /// </summary>
    public string Author { get; init; } = "TermReader";

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
}
