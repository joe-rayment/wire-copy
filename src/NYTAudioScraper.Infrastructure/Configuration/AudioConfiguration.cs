// <copyright file="AudioConfiguration.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

namespace NYTAudioScraper.Infrastructure.Configuration;

/// <summary>
/// Configuration for audio processing
/// </summary>
public class AudioConfiguration
{
    public const string SectionName = "Audio";

    public string OutputFormat { get; init; } = "m4b";
    public string Codec { get; init; } = "aac";
    public int BitRate { get; init; } = 64000;
    public int SampleRate { get; init; } = 44100;
    public int Channels { get; init; } = 1; // Mono
    public string OutputDirectory { get; init; } = "output";
}
