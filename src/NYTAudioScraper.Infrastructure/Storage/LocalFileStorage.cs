using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;

namespace NYTAudioScraper.Infrastructure.Storage;

/// <summary>
/// Stub implementation of IFileStorage
/// </summary>
public class LocalFileStorage : IFileStorage
{
    private readonly ILogger<LocalFileStorage> _logger;
    private readonly string _outputDirectory;

    public LocalFileStorage(ILogger<LocalFileStorage> logger)
    {
        _logger = logger;
        _outputDirectory = Path.Combine(AppContext.BaseDirectory, "output");
    }

    public Task DeleteFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DeleteFileAsync called for {FilePath} (stub implementation)", filePath);
        return Task.CompletedTask;
    }

    public string GetOutputDirectory()
    {
        return _outputDirectory;
    }

    public Task<string> SaveAudioAsync(
        byte[] audioData,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SaveAudioAsync called for {FileName} with {Size} bytes (stub implementation)",
            fileName, audioData.Length);
        var filePath = Path.Combine(_outputDirectory, fileName);
        return Task.FromResult(filePath);
    }
}
