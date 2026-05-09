// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// File-based persistent storage for AI-generated article-extraction layouts.
/// One JSON file per domain at <c>{LocalAppData}/WireCopy/layouts/{domain}.json</c>.
///
/// <para>
/// Mirrors <see cref="Browser.HierarchyConfigStore"/>'s pattern: atomic write
/// via <c>*.tmp</c> + <c>File.Move</c>, UTF-8 indented JSON, camelCase
/// property names, single-domain JSON object as the payload.
/// </para>
/// </summary>
public sealed class ArticleLayoutStore : IArticleLayoutStore
{
    /// <summary>Schema version this writer produces.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _storagePath;
    private readonly ILogger<ArticleLayoutStore> _logger;
    private readonly object _lock = new();

    public ArticleLayoutStore(ILogger<ArticleLayoutStore> logger)
        : this(
              logger,
              Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                  "WireCopy",
                  "layouts"))
    {
    }

    /// <summary>
    /// Test seam: lets unit tests redirect storage to a temp directory so
    /// disk I/O is isolated from the real user-data folder.
    /// </summary>
    internal ArticleLayoutStore(ILogger<ArticleLayoutStore> logger, string storagePath)
    {
        _logger = logger;
        _storagePath = storagePath;
    }

    public Task<ArticleSelectorConfig?> LoadAsync(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return Task.FromResult<ArticleSelectorConfig?>(null);
        }

        try
        {
            var filePath = GetFilePath(domain);
            if (!File.Exists(filePath))
            {
                return Task.FromResult<ArticleSelectorConfig?>(null);
            }

            ArticleSelectorConfig? config;
            lock (_lock)
            {
                var json = File.ReadAllText(filePath);
                config = JsonSerializer.Deserialize<ArticleSelectorConfig>(json, JsonOptions);
            }

            if (config == null)
            {
                return Task.FromResult<ArticleSelectorConfig?>(null);
            }

            // Schema-version mismatch: keep the file (don't clobber user edits)
            // and treat as a cache miss so the AI regenerates on next visit.
            if (config.SchemaVersion != CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "Article layout for {Domain} has schema version {Got}, current is {Expected}; treating as cache miss",
                    domain,
                    config.SchemaVersion,
                    CurrentSchemaVersion);
                return Task.FromResult<ArticleSelectorConfig?>(null);
            }

            return Task.FromResult<ArticleSelectorConfig?>(config);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Corrupt article layout for {Domain}, treating as cache miss", domain);
            return Task.FromResult<ArticleSelectorConfig?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load article layout for {Domain}", domain);
            return Task.FromResult<ArticleSelectorConfig?>(null);
        }
    }

    public Task SaveAsync(ArticleSelectorConfig config)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (string.IsNullOrWhiteSpace(config.Domain))
        {
            throw new ArgumentException("Config must have a non-empty Domain", nameof(config));
        }

        try
        {
            EnsureDirectoryExists();
            var filePath = GetFilePath(config.Domain);

            lock (_lock)
            {
                var tempPath = filePath + ".tmp";
                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
            }

            _logger.LogInformation(
                "Saved article layout for {Domain} ({EntryCount} page-type entries)",
                config.Domain,
                config.PageTypes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save article layout for {Domain}", config.Domain);
        }

        return Task.CompletedTask;
    }

    private string GetFilePath(string domain)
    {
        var safeName = domain.ToLowerInvariant().Replace(":", "_", StringComparison.Ordinal).Replace("/", "_", StringComparison.Ordinal);
        return Path.Combine(_storagePath, $"{safeName}.json");
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_storagePath))
        {
            Directory.CreateDirectory(_storagePath);
        }
    }
}
