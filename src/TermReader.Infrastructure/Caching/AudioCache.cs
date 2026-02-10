// Educational and personal use only.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;

namespace TermReader.Infrastructure.Caching;

/// <summary>
/// Disk-based audio cache using SHA256 content hashing.
/// </summary>
public class AudioCache : IAudioCache
{
    private readonly string _cacheDirectory;
    private readonly ILogger<AudioCache> _logger;

    public AudioCache(string cacheDirectory, ILogger<AudioCache> logger)
    {
        _cacheDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Ensure cache directory exists
        if (!Directory.Exists(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory);
            _logger.LogInformation("Created audio cache directory: {Directory}", _cacheDirectory);
        }
    }

    public async Task<byte[]?> GetAsync(string content, string voiceId, CancellationToken cancellationToken = default)
    {
        var cacheKey = GenerateCacheKey(content, voiceId);
        var cachePath = GetCachePath(cacheKey);

        if (!File.Exists(cachePath))
        {
            _logger.LogDebug("Audio cache MISS: {Key}", cacheKey);
            return null;
        }

        try
        {
            var audioData = await File.ReadAllBytesAsync(cachePath, cancellationToken);
            _logger.LogInformation("Audio cache HIT: {Key} ({Size:N0} bytes)", cacheKey, audioData.Length);
            return audioData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read cached audio: {Key}", cacheKey);
            return null;
        }
    }

    public async Task SetAsync(string content, string voiceId, byte[] audioData, CancellationToken cancellationToken = default)
    {
        var cacheKey = GenerateCacheKey(content, voiceId);
        var cachePath = GetCachePath(cacheKey);

        try
        {
            await File.WriteAllBytesAsync(cachePath, audioData, cancellationToken);
            _logger.LogInformation("Audio cached: {Key} ({Size:N0} bytes)", cacheKey, audioData.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache audio: {Key}", cacheKey);
            throw;
        }
    }

    public async Task CleanupAsync(int maxAgeDays = 30, CancellationToken cancellationToken = default)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-maxAgeDays);
        var filesRemoved = 0;
        var bytesFreed = 0L;

        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "*.mp3");

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTimeUtc < cutoffDate)
                {
                    bytesFreed += fileInfo.Length;
                    File.Delete(file);
                    filesRemoved++;
                }
            }

            _logger.LogInformation(
                "Audio cache cleanup: removed {Count} files, freed {Size:N0} bytes",
                filesRemoved,
                bytesFreed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup audio cache");
            throw;
        }

        await Task.CompletedTask;
    }

    public Task<long> GetCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "*.mp3");
            var totalSize = files.Sum(f => new FileInfo(f).Length);
            return Task.FromResult(totalSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate cache size");
            return Task.FromResult(0L);
        }
    }

    /// <summary>
    /// Generates a cache key from content and voice ID using SHA256.
    /// </summary>
    private static string GenerateCacheKey(string content, string voiceId)
    {
        var input = $"{content}:{voiceId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Gets the full file path for a cache key.
    /// </summary>
    private string GetCachePath(string cacheKey)
    {
        return Path.Combine(_cacheDirectory, $"{cacheKey}.mp3");
    }
}
