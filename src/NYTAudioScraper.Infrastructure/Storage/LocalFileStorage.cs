// Educational and personal use only.

using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;

namespace NYTAudioScraper.Infrastructure.Storage;

/// <summary>
/// Implementation of IFileStorage with proper file operations, error handling, and disk space validation.
/// </summary>
public class LocalFileStorage : IFileStorage
{
    private const long MinimumFreeDiskSpaceBytes = 100 * 1024 * 1024; // 100 MB minimum
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

        await Task.CompletedTask;
    }

    public string GetOutputDirectory()
    {
        EnsureDirectoryExists(_outputDirectory);
        return _outputDirectory;
    }

    public async Task<string> SaveAudioAsync(
        byte[] audioData,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        if (audioData == null || audioData.Length == 0)
        {
            throw new ArgumentException("Audio data cannot be null or empty", nameof(audioData));
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name cannot be null or empty", nameof(fileName));
        }

        // Sanitize filename to prevent path traversal attacks
        var sanitizedFileName = SanitizeFileName(fileName);
        var filePath = Path.Combine(_outputDirectory, sanitizedFileName);

        try
        {
            // Ensure output directory exists
            EnsureDirectoryExists(_outputDirectory);

            // Validate disk space before writing
            ValidateDiskSpace(audioData.Length);

            // Use atomic write operation: write to temp file, then move
            var tempFilePath = Path.Combine(_outputDirectory, $"{Guid.NewGuid()}.tmp");

            _logger.LogInformation("Saving audio file: {FileName} ({Size:N0} bytes)", sanitizedFileName, audioData.Length);

            // Write to temp file using stream for better memory efficiency
            await using (var fileStream = new FileStream(
                tempFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920, // 80 KB buffer
                useAsync: true))
            {
                await fileStream.WriteAsync(audioData, cancellationToken);
                await fileStream.FlushAsync(cancellationToken);
            }

            // Atomic move operation
            if (File.Exists(filePath))
            {
                _logger.LogWarning("File already exists, will be overwritten: {FilePath}", filePath);
                File.Delete(filePath);
            }

            File.Move(tempFilePath, filePath);

            _logger.LogInformation("Successfully saved audio file: {FilePath}", filePath);
            return filePath;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied when saving file: {FilePath}", filePath);
            throw new InvalidOperationException($"Access denied when saving file: {filePath}", ex);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error when saving file: {FilePath}", filePath);
            throw new InvalidOperationException($"IO error when saving file: {filePath}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error when saving file: {FilePath}", filePath);
            throw;
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        // Remove path traversal attempts
        var sanitized = Path.GetFileName(fileName);

        // Remove invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c, '_');
        }

        // Ensure filename is not too long (max 255 chars on most systems)
        if (sanitized.Length > 255)
        {
            var extension = Path.GetExtension(sanitized);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = nameWithoutExtension.Substring(0, 255 - extension.Length) + extension;
        }

        return sanitized;
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

    private void ValidateDiskSpace(long requiredBytes)
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(_outputDirectory) ?? "/");
            var availableSpace = driveInfo.AvailableFreeSpace;

            if (availableSpace < requiredBytes + MinimumFreeDiskSpaceBytes)
            {
                var errorMessage = $"Insufficient disk space. Required: {requiredBytes:N0} bytes, " +
                                 $"Available: {availableSpace:N0} bytes, " +
                                 $"Minimum buffer: {MinimumFreeDiskSpaceBytes:N0} bytes";
                _logger.LogError(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }

            _logger.LogDebug(
                "Disk space check passed. Available: {AvailableSpace:N0} bytes, Required: {RequiredBytes:N0} bytes",
                availableSpace,
                requiredBytes);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // If we can't check disk space (e.g., on some platforms), log warning but continue
            _logger.LogWarning(ex, "Unable to validate disk space, continuing anyway");
        }
    }
}
