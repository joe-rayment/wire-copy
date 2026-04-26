// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Manages a temporary directory lifecycle for audio assembly operations.
/// Creates a unique temp dir, tracks files, and cleans up on dispose.
/// </summary>
internal sealed class TempFileManager : IDisposable
{
    private readonly string _tempDir;
    private readonly ILogger _logger;
    private readonly List<string> _trackedFiles = [];
    private bool _disposed;

    public TempFileManager(string? baseTempDir, ILogger logger)
    {
        _logger = logger;
        var baseDir = baseTempDir ?? Path.GetTempPath();
        _tempDir = Path.Combine(baseDir, $"termreader-podcast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _logger.LogDebug("Created temp directory: {TempDir}", _tempDir);
    }

    public string TempDirectory => _tempDir;

    public string GetTempFilePath(string filename)
    {
        var path = Path.Combine(_tempDir, filename);
        _trackedFiles.Add(path);
        return path;
    }

    public string CreateConcatListFile(IReadOnlyList<string> filePaths, string listName)
    {
        var listPath = GetTempFilePath(listName);
        var lines = filePaths.Select(p => $"file '{p.Replace("'", "'\\''")}'");
        File.WriteAllLines(listPath, lines);
        return listPath;
    }

    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
                _logger.LogDebug("Cleaned up temp directory: {TempDir}", _tempDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up temp directory: {TempDir}", _tempDir);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cleanup();
    }
}
