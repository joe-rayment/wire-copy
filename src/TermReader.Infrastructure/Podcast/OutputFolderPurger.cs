// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;

namespace TermReader.Infrastructure.Podcast;

/// <summary>
/// Removes podcast output files (*.m4b, *.mp3) older than a configurable TTL
/// from the user's output folder. Behaves like the article-content cache TTL
/// sweep, but scoped strictly to podcast outputs.
/// </summary>
internal sealed class OutputFolderPurger
{
    private static readonly string[] PurgeableExtensions = [".m4b", ".m4a", ".mp3", ".xml"];

    private readonly ILogger<OutputFolderPurger> _logger;

    public OutputFolderPurger(ILogger<OutputFolderPurger> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scans <paramref name="folder"/> for *.m4b/*.mp3 files older than <paramref name="ttl"/>
    /// and deletes them. Other file types are left alone. Missing folders and
    /// individual permission errors are swallowed so app startup never crashes
    /// because of a janky filesystem.
    /// </summary>
    /// <returns>The number of files actually deleted.</returns>
    public Task<int> PurgeOldFilesAsync(string folder, TimeSpan ttl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return Task.FromResult(0);
        }

        if (!Directory.Exists(folder))
        {
            _logger.LogDebug("Output folder does not exist, skipping purge: {Folder}", folder);
            return Task.FromResult(0);
        }

        var deleted = 0;
        var cutoff = DateTime.UtcNow - ttl;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Failed to enumerate output folder, skipping purge: {Folder}", folder);
            return Task.FromResult(0);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(file);
            if (!PurgeableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTime lastWrite;
            try
            {
                lastWrite = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogDebug(ex, "Could not stat output file, skipping: {File}", file);
                continue;
            }

            if (lastWrite >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(file);
                _logger.LogDebug(
                    "Purged old podcast output: {File} (age {AgeHours:F1}h)",
                    file,
                    (DateTime.UtcNow - lastWrite).TotalHours);
                deleted++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogWarning(ex, "Failed to delete old output file: {File}", file);
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Purged {Count} podcast output file(s) older than {Hours:F1}h from {Folder}",
                deleted,
                ttl.TotalHours,
                folder);
        }

        return Task.FromResult(deleted);
    }
}
