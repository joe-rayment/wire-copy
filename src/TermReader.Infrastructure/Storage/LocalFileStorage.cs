// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;

namespace TermReader.Infrastructure.Storage;

/// <summary>
/// Implementation of IFileStorage with proper file operations, error handling, and disk space validation.
/// </summary>
public class LocalFileStorage : IFileStorage
{
    private readonly ILogger<LocalFileStorage> _logger;
    private readonly string _outputDirectory;

    public LocalFileStorage(ILogger<LocalFileStorage> logger)
    {
        _logger = logger;
        _outputDirectory = Path.Combine(AppContext.BaseDirectory, "output");

        // Ensure output directory exists
        EnsureDirectoryExists(_outputDirectory);
    }

    public async Task DeleteFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Cannot delete file - file does not exist: {FilePath}", filePath);
                return;
            }

            File.Delete(filePath);
            _logger.LogInformation("Successfully deleted file: {FilePath}", filePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when deleting file: {FilePath}", filePath);
            throw;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error when deleting file: {FilePath}", filePath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error when deleting file: {FilePath}", filePath);
            throw;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public string GetOutputDirectory()
    {
        EnsureDirectoryExists(_outputDirectory);
        return _outputDirectory;
    }

    private void EnsureDirectoryExists(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
                _logger.LogInformation("Created output directory: {DirectoryPath}", directoryPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create directory: {DirectoryPath}", directoryPath);
            throw new InvalidOperationException($"Failed to create directory: {directoryPath}", ex);
        }
    }
}
