// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// File-based persistent storage for AI-generated page hierarchy configurations.
/// Stores one JSON file per domain in {AppData}/WireCopy/hierarchy/.
/// </summary>
public class HierarchyConfigStore : IHierarchyConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Regex CorruptAsideFileName =
        new(@"\.corrupt-\d{14}(-\d+)?\.json$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _storagePath;
    private readonly ILogger<HierarchyConfigStore> _logger;
    private readonly object _lock = new();

    public HierarchyConfigStore(ILogger<HierarchyConfigStore> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WireCopy",
            "hierarchy");
    }

    public Task<SiteHierarchyConfig?> GetConfigAsync(string url)
    {
        try
        {
            var configs = LoadConfigsForSite(url);

            // Two passes so a config matching the URL as typed always beats one
            // that only matches the www-toggled variant (workspace-42q8.1: legacy
            // patterns captured the literal saved host, so a layout saved on
            // www.x.com never matched x.com/... and vice versa).
            var match = SelectMatch(configs, url);
            if (match != null)
            {
                _logger.LogDebug("Found hierarchy config for {Url} (pattern: {Pattern})", url, match.UrlPattern);
                return Task.FromResult<SiteHierarchyConfig?>(match);
            }

            return Task.FromResult<SiteHierarchyConfig?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load hierarchy config for {Url}", url);
            return Task.FromResult<SiteHierarchyConfig?>(null);
        }
    }

    public Task<IReadOnlyList<SiteHierarchyConfig>> GetConfigsForDomainAsync(string url)
    {
        try
        {
            return Task.FromResult<IReadOnlyList<SiteHierarchyConfig>>(LoadConfigsForSite(url));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load hierarchy configs for the domain of {Url}", url);
            return Task.FromResult<IReadOnlyList<SiteHierarchyConfig>>(Array.Empty<SiteHierarchyConfig>());
        }
    }

    public Task<bool> SaveConfigAsync(SiteHierarchyConfig config)
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

                // workspace-42q8.1: a same-pattern config in the legacy "www." variant
                // file would shadow-duplicate this save (both variants are probed on
                // read), so supersede it there too.
                PurgePatternFromSiblingVariant(config.Domain, config.UrlPattern);
            }

            _logger.LogInformation(
                "Saved hierarchy config for {Domain} (pattern: {Pattern}, {SectionCount} sections)",
                config.Domain,
                config.UrlPattern,
                config.Sections.Count);
        }
        catch (Exception ex)
        {
            // workspace-9k27.4: report the failure — the caller was showing
            // "✔ Site set up" over a config that vanished on restart.
            _logger.LogWarning(ex, "Failed to save hierarchy config for {Domain}", config.Domain);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public Task<bool> DeleteConfigAsync(string url)
    {
        try
        {
            var removedAny = false;
            lock (_lock)
            {
                // workspace-zh1o1: remove only the config GetConfigAsync would have selected
                // for this URL (plus its exact-same-pattern copy in the sibling www/non-www
                // file) — never every config whose pattern merely prefix-matches, which
                // used to take the homepage layout down with a section's.
                var selected = SelectMatch(LoadConfigsForSite(url), url);
                if (selected == null)
                {
                    return Task.FromResult(false);
                }

                foreach (var filePath in CandidateFilePaths(url))
                {
                    if (!File.Exists(filePath))
                    {
                        continue;
                    }

                    var configs = LoadConfigsFromFile(filePath);
                    var originalCount = configs.Count;
                    configs.RemoveAll(c => string.Equals(c.UrlPattern, selected.UrlPattern, StringComparison.Ordinal));
                    if (configs.Count == originalCount)
                    {
                        continue;
                    }

                    removedAny = true;
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
            }

            if (removedAny)
            {
                _logger.LogInformation("Deleted hierarchy config for {Url}", url);
            }

            return Task.FromResult(removedAny);
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
            foreach (var file in Directory.GetFiles(_storagePath, "*.json").Where(f => !IsCorruptAsideFile(f)))
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

    public Task<int> ClearLegacySnapshotsAsync()
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(_storagePath))
            {
                return Task.FromResult(0);
            }

            lock (_lock)
            {
                foreach (var file in Directory.GetFiles(_storagePath, "*.json").Where(f => !IsCorruptAsideFile(f)))
                {
                    var configs = LoadConfigsFromFile(file);
                    var kept = configs.Where(c => !ConfigMigration.IsLegacySnapshot(c)).ToList();
                    if (kept.Count == configs.Count)
                    {
                        continue;
                    }

                    removed += configs.Count - kept.Count;
                    if (kept.Count == 0)
                    {
                        File.Delete(file);
                    }
                    else
                    {
                        var tempPath = file + ".tmp";
                        File.WriteAllText(tempPath, JsonSerializer.Serialize(kept, JsonOptions));
                        File.Move(tempPath, file, overwrite: true);
                    }
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation("Cleared {Count} legacy snapshot hierarchy config(s)", removed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear legacy snapshot configs");
        }

        return Task.FromResult(removed);
    }

    /// <summary>One config's URL pattern regex-matched against a URL, with the store's standard guard rails.</summary>
    internal static bool PatternMatches(string url, SiteHierarchyConfig config)
    {
        try
        {
            return Regex.IsMatch(url, config.UrlPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // A corrupt/invalid saved pattern must never take the whole lookup down.
            return false;
        }
    }

    /// <summary>
    /// The same URL with its host's "www." toggled (www.x.com/a → x.com/a and back),
    /// or null when that isn't meaningful (relative URL, IP, single-label host).
    /// </summary>
    internal static string? WwwToggledUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns)
        {
            return null;
        }

        var host = uri.Host;
        string toggledHost;
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            toggledHost = host[4..];
            if (!toggledHost.Contains('.'))
            {
                return null;
            }
        }
        else if (host.Contains('.'))
        {
            toggledHost = "www." + host;
        }
        else
        {
            return null;
        }

        var builder = new UriBuilder(uri) { Host = toggledHost };
        return builder.Uri.ToString();
    }

    private static string? ExtractDomain(string url) => HierarchyDomainKey.TryFromUrl(url);

    /// <summary>True for a file a previous corrupt-load moved aside (<c>name.corrupt-yyyyMMddHHmmss[-n].json</c>).</summary>
    private static bool IsCorruptAsideFile(string filePath) =>
        CorruptAsideFileName.IsMatch(Path.GetFileName(filePath));

    private static string MoveCorruptFileAside(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

        var target = Path.Combine(directory, $"{baseName}.corrupt-{stamp}.json");
        var suffix = 0;
        while (File.Exists(target))
        {
            suffix++;
            target = Path.Combine(directory, $"{baseName}.corrupt-{stamp}-{suffix}.json");
        }

        File.Move(filePath, target);
        return target;
    }

    /// <summary>
    /// The config that applies to a URL: URL as typed first, then its www-toggled twin
    /// (workspace-42q8.1). Within a pass the MOST SPECIFIC match (longest UrlPattern) wins
    /// so a section layout is never shadowed by the homepage's site-wide pattern
    /// (workspace-zh1o1); ties keep file (= save) order.
    /// </summary>
    private static SiteHierarchyConfig? SelectMatch(List<SiteHierarchyConfig> configs, string url)
    {
        foreach (var candidate in new[] { url, WwwToggledUrl(url) })
        {
            if (candidate == null)
            {
                continue;
            }

            SiteHierarchyConfig? best = null;
            foreach (var config in configs)
            {
                if (PatternMatches(candidate, config)
                    && (best == null || config.UrlPattern.Length > best.UrlPattern.Length))
                {
                    best = config;
                }
            }

            if (best != null)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>
    /// Removes any config with the given UrlPattern from the OTHER host-variant file
    /// of <paramref name="domain"/> (www ↔ apex), deleting that file when it empties.
    /// Caller holds <see cref="_lock"/>.
    /// </summary>
    private void PurgePatternFromSiblingVariant(string domain, string urlPattern)
    {
        var host = domain.Split(':')[0];
        string sibling;
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            sibling = domain[4..];
        }
        else if (host.Contains('.') && Uri.CheckHostName(host) == UriHostNameType.Dns)
        {
            sibling = "www." + domain;
        }
        else
        {
            return;
        }

        var siblingPath = GetFilePath(sibling);
        if (!File.Exists(siblingPath))
        {
            return;
        }

        var siblingConfigs = LoadConfigsFromFile(siblingPath);
        if (siblingConfigs.RemoveAll(c => string.Equals(c.UrlPattern, urlPattern, StringComparison.Ordinal)) == 0)
        {
            return;
        }

        if (siblingConfigs.Count == 0)
        {
            File.Delete(siblingPath);
        }
        else
        {
            var tempPath = siblingPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(siblingConfigs, JsonOptions));
            File.Move(tempPath, siblingPath, overwrite: true);
        }
    }

    /// <summary>
    /// Every config saved for the URL's site: the normalized (www-stripped) domain file
    /// plus the legacy "www." variant file, in that order.
    /// </summary>
    private List<SiteHierarchyConfig> LoadConfigsForSite(string url)
    {
        var configs = new List<SiteHierarchyConfig>();
        lock (_lock)
        {
            foreach (var filePath in CandidateFilePaths(url).Where(File.Exists))
            {
                configs.AddRange(LoadConfigsFromFile(filePath));
            }
        }

        return configs;
    }

    /// <summary>
    /// Candidate storage files for a URL's site: the current (www-stripped) key first,
    /// then the legacy "www."-prefixed variant written before workspace-42q8.1.
    /// </summary>
    private IEnumerable<string> CandidateFilePaths(string url)
    {
        var domain = ExtractDomain(url);
        if (domain == null)
        {
            yield break;
        }

        yield return GetFilePath(domain);

        // The key is already www-stripped, so the only other place a config for this
        // site can live is a legacy www-keyed file (dotted DNS hosts only).
        var host = domain.Split(':')[0];
        if (host.Contains('.') && Uri.CheckHostName(host) == UriHostNameType.Dns)
        {
            yield return GetFilePath("www." + domain);
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
            // workspace-zh1o1: the file holds the user's other saved layouts for this
            // domain. Move it aside BEFORE starting fresh so the next save can't
            // overwrite those bytes. If the move fails we throw: every caller catches
            // Exception, so the save/delete is abandoned and the corrupt bytes stay put.
            var asidePath = MoveCorruptFileAside(filePath);
            _logger.LogWarning(ex, "Corrupt hierarchy config file {Path} moved aside to {AsidePath}, starting fresh", filePath, asidePath);
            return new List<SiteHierarchyConfig>();
        }
    }
}
