// Educational and personal use only.

namespace TermReader.Application.Interfaces;

/// <summary>
/// Persistent key-value store for user settings that survive app restarts.
/// Sensitive values (e.g., API keys) are encrypted at rest.
/// </summary>
public interface IUserSettingsStore
{
    /// <summary>
    /// Gets a setting value by key. Returns null if not found.
    /// </summary>
    string? Get(string key);

    /// <summary>
    /// Sets a setting value. Use <paramref name="encrypt"/> for sensitive values.
    /// </summary>
    void Set(string key, string value, bool encrypt = false);

    /// <summary>
    /// Removes a setting by key.
    /// </summary>
    void Remove(string key);
}
