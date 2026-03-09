// Educational and personal use only.

using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using TermReader.Application.Interfaces;

namespace TermReader.Infrastructure.Configuration;

/// <summary>
/// Persists user settings to an encrypted JSON file in LocalApplicationData.
/// Follows the ThemeProvider pattern: synchronous, best-effort, atomic writes.
/// </summary>
internal sealed class UserSettingsStore : IUserSettingsStore
{
    private const string ProtectionPurpose = "TermReader.UserSettings";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TermReader",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly IDataProtector _protector;
    private readonly ILogger<UserSettingsStore> _logger;
    private readonly object _lock = new();
    private Dictionary<string, SettingsEntry> _entries;

    public UserSettingsStore(IDataProtectionProvider dataProtection, ILogger<UserSettingsStore> logger)
    {
        _protector = dataProtection.CreateProtector(ProtectionPurpose);
        _logger = logger;
        _entries = LoadFromDisk();
    }

    public string? Get(string key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (!entry.Encrypted)
            {
                return entry.Value;
            }

            try
            {
                return _protector.Unprotect(entry.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt setting '{Key}'; removing corrupt entry", key);
                _entries.Remove(key);
                SaveToDisk();
                return null;
            }
        }
    }

    public void Set(string key, string value, bool encrypt = false)
    {
        lock (_lock)
        {
            var storedValue = encrypt ? _protector.Protect(value) : value;
            _entries[key] = new SettingsEntry(storedValue, encrypt);
            SaveToDisk();
        }
    }

    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_entries.Remove(key))
            {
                SaveToDisk();
            }
        }
    }

    private Dictionary<string, SettingsEntry> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new Dictionary<string, SettingsEntry>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(SettingsPath);
            var file = JsonSerializer.Deserialize<SettingsFile>(json);
            return file?.Settings
                       ?? new Dictionary<string, SettingsEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path}; starting fresh", SettingsPath);
            return new Dictionary<string, SettingsEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);

            var file = new SettingsFile { Settings = _entries };
            var json = JsonSerializer.Serialize(file, JsonOptions);

            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to save settings to {Path}", SettingsPath);
        }
    }

    private sealed record SettingsEntry(string Value, bool Encrypted);

    private sealed class SettingsFile
    {
        public Dictionary<string, SettingsEntry> Settings { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }
}
