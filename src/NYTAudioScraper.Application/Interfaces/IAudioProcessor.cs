using NYTAudioScraper.Domain.ValueObjects;

namespace NYTAudioScraper.Application.Interfaces;

/// <summary>
/// Service for audio processing operations (conversion, concatenation, etc.)
/// </summary>
public interface IAudioProcessor
{
    /// <summary>
    /// Concatenates multiple audio files into a single M4B audiobook
    /// </summary>
    /// <param name="inputFiles">List of audio file paths to concatenate</param>
    /// <param name="outputPath">Output M4B file path</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to created audiobook file</returns>
    Task<string> CreateAudiobookAsync(
        IEnumerable<string> inputFiles,
        string outputPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets metadata from an audio file
    /// </summary>
    /// <param name="filePath">Path to audio file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Audio metadata</returns>
    Task<AudioMetadata> GetMetadataAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
