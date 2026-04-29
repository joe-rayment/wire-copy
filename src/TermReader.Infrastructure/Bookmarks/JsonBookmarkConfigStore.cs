// Licensed under the MIT License. See LICENSE in the repository root.

using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;
using TermReader.Domain.Entities.Bookmarks;

namespace TermReader.Infrastructure.Bookmarks;

/// <summary>
/// JSON-backed implementation of <see cref="IBookmarkConfigStore"/>. The user
/// config file lives at <c>{LocalAppData}/TermReader/bookmarks.json</c>; the
/// shipped defaults are loaded from an embedded resource in TermReader.Persistence.
/// </summary>
public sealed class JsonBookmarkConfigStore : IBookmarkConfigStore
{
    /// <summary>
    /// Current schema version written to new config files. Bump when introducing
    /// a v2 migration step.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private const string EmbeddedResourceName = "TermReader.Persistence.Resources.bookmarks.default.json";

    // Relaxed encoder so apostrophes and non-ASCII letters appear literally in
    // the user-facing bookmarks.json instead of \u escape sequences.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger<JsonBookmarkConfigStore> _logger;
    private readonly Assembly _resourceAssembly;

    public JsonBookmarkConfigStore(ILogger<JsonBookmarkConfigStore> logger)
        : this(logger, ResolveDefaultUserConfigPath(), typeof(TermReader.Persistence.AppDbContext).Assembly)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets callers override the user config path
    /// and the assembly carrying the embedded shipped defaults.
    /// </summary>
    internal JsonBookmarkConfigStore(ILogger<JsonBookmarkConfigStore> logger, string userConfigPath, Assembly resourceAssembly)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        UserConfigPath = userConfigPath ?? throw new ArgumentNullException(nameof(userConfigPath));
        _resourceAssembly = resourceAssembly ?? throw new ArgumentNullException(nameof(resourceAssembly));
    }

    public string UserConfigPath { get; }

    public bool UserConfigExists() => File.Exists(UserConfigPath);

    public async Task<BookmarkConfigFile?> LoadUserConfigAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(UserConfigPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            UserConfigPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return await DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BookmarkConfigFile> LoadShippedDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = _resourceAssembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedResourceName}' not found in assembly '{_resourceAssembly.FullName}'. " +
                "Ensure bookmarks.default.json is registered as an EmbeddedResource in TermReader.Persistence.csproj.");

        var parsed = await DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);
        return parsed
            ?? throw new InvalidOperationException("Embedded shipped-defaults file is empty or malformed.");
    }

    public Task SaveUserConfigAsync(IReadOnlyList<Bookmark> bookmarks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);

        // Snapshot in current SortOrder; the array order is the file's source of truth.
        var entries = bookmarks
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(b => new BookmarkConfigEntry(b.Name, b.Url))
            .ToList();

        var config = new BookmarkConfigFile(CurrentSchemaVersion, entries);
        return SaveUserConfigAsync(config, cancellationToken);
    }

    public async Task SaveUserConfigAsync(BookmarkConfigFile config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var directory = Path.GetDirectoryName(UserConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var dto = ToDto(config);
        var tempPath = UserConfigPath + ".tmp";

        // Write to temp + fsync + rename so a crash mid-write never leaves a half-file.
        await using (var tempStream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            await JsonSerializer.SerializeAsync(tempStream, dto, WriteOptions, cancellationToken).ConfigureAwait(false);
            await tempStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                tempStream.Flush(flushToDisk: true);
            }
            catch (IOException ex)
            {
                // Some platforms (e.g. tmpfs) don't support flush-to-disk. Logged for diagnostics; the rename below
                // is still a best-effort atomic step.
                _logger.LogDebug(ex, "fsync (Flush(flushToDisk:true)) failed for {Path}; continuing with rename.", tempPath);
            }
        }

        try
        {
            File.Move(tempPath, UserConfigPath, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup on failure to avoid leaking a stale temp file.
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException cleanupEx)
            {
                _logger.LogDebug(cleanupEx, "Failed to clean up temp file {Path} after rename failure.", tempPath);
            }

            throw;
        }
    }

    private static string ResolveDefaultUserConfigPath()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localData, "TermReader", "bookmarks.json");
    }

    private static async Task<BookmarkConfigFile?> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var dto = await JsonSerializer.DeserializeAsync<BookmarkConfigDto>(stream, ReadOptions, cancellationToken)
            .ConfigureAwait(false);
        if (dto is null)
        {
            return null;
        }

        var entries = (dto.Bookmarks ?? new List<BookmarkEntryDto>())
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.Url))
            .Select(e => new BookmarkConfigEntry(e.Name!.Trim(), e.Url!.Trim()))
            .ToList();

        return new BookmarkConfigFile(dto.Version <= 0 ? CurrentSchemaVersion : dto.Version, entries);
    }

    private static BookmarkConfigDto ToDto(BookmarkConfigFile config)
    {
        return new BookmarkConfigDto
        {
            Version = config.Version <= 0 ? CurrentSchemaVersion : config.Version,
            Bookmarks = config.Bookmarks
                .Select(e => new BookmarkEntryDto { Name = e.Name, Url = e.Url })
                .ToList(),
        };
    }

    // JSON DTOs — deliberately separate from the domain entity so the file
    // shape can evolve independently.
    private sealed class BookmarkConfigDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentSchemaVersion;

        [JsonPropertyName("bookmarks")]
        public List<BookmarkEntryDto>? Bookmarks { get; set; }
    }

    private sealed class BookmarkEntryDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
