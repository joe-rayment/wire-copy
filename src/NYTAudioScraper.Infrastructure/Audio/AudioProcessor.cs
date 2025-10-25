using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.ValueObjects;

namespace NYTAudioScraper.Infrastructure.Audio;

/// <summary>
/// Stub implementation of IAudioProcessor
/// </summary>
public class AudioProcessor : IAudioProcessor
{
    private readonly ILogger<AudioProcessor> _logger;

    public AudioProcessor(ILogger<AudioProcessor> logger)
    {
        _logger = logger;
    }

    public Task<string> CreateAudiobookAsync(
        IEnumerable<string> inputFiles,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("CreateAudiobookAsync called with {FileCount} files (stub implementation)",
            inputFiles.Count());
        return Task.FromResult(outputPath);
    }

    public Task<AudioMetadata> GetMetadataAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GetMetadataAsync called for {FilePath} (stub implementation)", filePath);
        return Task.FromResult(AudioMetadata.Default);
    }
}
