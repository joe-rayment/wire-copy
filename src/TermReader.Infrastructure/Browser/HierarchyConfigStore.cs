// Educational and personal use only.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser;

/// <summary>
/// File-based persistent storage for AI-generated page hierarchy configurations.
/// Stores one JSON file per domain in {AppData}/TermReader/hierarchy/.
/// </summary>
public class HierarchyConfigStore : IHierarchyConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _storagePath;
    private readonly ILogger<HierarchyConfigStore> _logger;
    private readonly object _lock = new();

    public HierarchyConfigStore(ILogger<HierarchyConfigStore> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermReader",
            "hierarchy");
    }

    public Task<SiteHierarchyConfig?> GetConfigAsync(string url)
    {
        try
        {
            var domain = ExtractDomain(url);
            if (domain == null)
            {
                return Task.FromResult<SiteHierarchyConfig?>(null);
            }

            var filePath = GetFilePath(domain);
            if (!File.Exists(filePath))
            {
                return Task.FromResult<SiteHierarchyConfig?>(null);
            }

            List<SiteHierarchyConfig> configs;
            lock (_lock)
            {
                var json = File.ReadAllText(filePath);
                configs = JsonSerializer.Deserialize<List<SiteHierarchyConfig>>(json, JsonOptions)
                    ?? new List<SiteHierarchyConfig>();
            }

            // Find matching config by URL pattern (regex match)
            foreach (var config in configs)
            {
                try
                {
                    if (Regex.IsMatch(url, config.UrlPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    {
                        _logger.LogDebug("Found hierarchy config for {Url} (pattern: {Pattern})", url, config.UrlPattern);
                        return Task.FromResult<SiteHierarchyConfig?>(config);
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    _logger.LogWarning("Regex timeout matching pattern {Pattern} against {Url}", config.UrlPattern, url);
                }
            }

            return Task.FromResult<SiteHierarchyConfig?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load hierarchy config for {Url}", url);
            return Task.FromResult<SiteHierarchyConfig?>(null);
        }
    }

    public Task SaveConfigAsync(SiteHierarchyConfig config)
    {
        try
        {
            EnsureDirectoryExists();

            var filePath = GetFilePath(config.Domain);
            List<SiteHierarchyConfig> configs;

            lock (_lock)
            {
                configs = LoadConfigsFromFile(filePath);

                // Remove existing config with same URL pattern
                configs.RemoveAll(c =>
                    string.Equals(c.UrlPattern, config.UrlPattern, StringComparison.Ordinal));

                configs.Add(config);

                // Atomic write via temp file
                var tempPath = filePath + ".tmp";
                var json = JsonSerializer.Serialize(configs, JsonOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
            }

            _logger.LogInformation(
                "Saved hierarchy config for {Domain} (pattern: {Pattern}, {SectionCount} sections)",
                config.Domain,
                config.UrlPattern,
                config.Sections.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save hierarchy config for {Domain}", config.Domain);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteConfigAsync(string url)
    {
        try
        {
            var domain = ExtractDomain(url);
            if (domain == null)
            {
                return Task.FromResult(false);
            }

            var filePath = GetFilePath(domain);
            if (!File.Exists(filePath))
            {
                return Task.FromResult(false);
            }

            lock (_lock)
            {
                var configs = LoadConfigsFromFile(filePath);
                var originalCount = configs.Count;

                configs.RemoveAll(c =>
                {
                    try
                    {
                        return Regex.IsMatch(url, c.UrlPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return false;
                    }
                });

                if (configs.Count == originalCount)
                {
                    return Task.FromResult(false);
                }

                if (configs.Count == 0)
                {
                    File.Delete(filePath);
                }
                else
                {
                    var tempPath = filePath + ".tmp";
                    var json = JsonSerializer.Serialize(configs, JsonOptions);
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, filePath, overwrite: true);
                }
            }

            _logger.LogInformation("Deleted hierarchy config for {Url}", url);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete hierarchy config for {Url}", url);
            return Task.FromResult(false);
        }
    }

    public Task<int> GetConfigCountAsync()
    {
        try
        {
            if (!Directory.Exists(_storagePath))
            {
                return Task.FromResult(0);
            }

            var count = 0;
            foreach (var file in Directory.GetFiles(_storagePath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var configs = JsonSerializer.Deserialize<List<SiteHierarchyConfig>>(json, JsonOptions);
                    count += configs?.Count ?? 0;
                }
                catch
                {
                    // Skip corrupt files
                }
            }

            return Task.FromResult(count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to count hierarchy configs");
            return Task.FromResult(0);
        }
    }

    private static string? ExtractDomain(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private string GetFilePath(string domain)
    {
        // Sanitize domain for filename
        var safeName = domain.Replace(":", "_").Replace("/", "_");
        return Path.Combine(_storagePath, $"{safeName}.json");
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_storagePath))
        {
            Directory.CreateDirectory(_storagePath);
        }
    }

    private List<SiteHierarchyConfig> LoadConfigsFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new List<SiteHierarchyConfig>();
        }

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<SiteHierarchyConfig>>(json, JsonOptions)
                ?? new List<SiteHierarchyConfig>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Corrupt hierarchy config file {Path}, starting fresh", filePath);
            return new List<SiteHierarchyConfig>();
        }
    }
}
