// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WireCopy.Infrastructure.Browser.Cache;

/// <summary>
/// Durable record of interrupted prefetch work (workspace-mya7): when the user
/// takes over the browser mid-cache, the remaining queue is written here so the
/// app knows exactly where it left off — across pauses AND restarts. Deleted
/// once the queue drains.
/// </summary>
public sealed class PreloadCheckpoint
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WireCopy",
        "preload-checkpoint.json");

    public string? PageUrl { get; init; }

    public List<string> RemainingUrls { get; init; } = [];

    public DateTimeOffset SavedAt { get; init; }

    public static void Save(string path, string? pageUrl, IReadOnlyList<string> remaining, ILogger logger)
    {
        try
        {
            var checkpoint = new PreloadCheckpoint
            {
                PageUrl = pageUrl,
                RemainingUrls = remaining.ToList(),
                SavedAt = DateTimeOffset.UtcNow,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(checkpoint));
            logger.LogInformation(
                "Prefetch checkpoint saved: {Count} items remaining for {Page}",
                remaining.Count,
                pageUrl);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to save prefetch checkpoint (non-fatal)");
        }
    }

    /// <summary>Loads a fresh checkpoint or null (missing, stale, unreadable).</summary>
    public static PreloadCheckpoint? Load(string path, ILogger logger)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var checkpoint = JsonSerializer.Deserialize<PreloadCheckpoint>(File.ReadAllText(path));
            if (checkpoint == null || checkpoint.RemainingUrls.Count == 0
                || DateTimeOffset.UtcNow - checkpoint.SavedAt > MaxAge)
            {
                Delete(path, logger);
                return null;
            }

            return checkpoint;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to load prefetch checkpoint (non-fatal)");
            return null;
        }
    }

    public static void Delete(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete prefetch checkpoint (non-fatal)");
        }
    }
}
