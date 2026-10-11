// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>One browser's last contact with the native-messaging host.</summary>
/// <param name="LastSeenUtc">When the extension in that browser last reached the host.</param>
/// <param name="ExtensionVersion">The extension's manifest version, when it sent one.</param>
public sealed record BrowserConnection(DateTime LastSeenUtc, string? ExtensionVersion);

/// <summary>
/// Reads and writes <c>connections.json</c> (workspace-m7aml): the host records every
/// hello/save under the browser that launched it, and the Settings card reads it to
/// show "connected" per browser. Keys are browser ids: chrome, chromium, vivaldi,
/// brave, edge, or browser when the launcher could not be identified.
/// </summary>
public static class BrowserConnections
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>All recorded browsers; empty when the file is missing or unreadable.</summary>
    public static IReadOnlyDictionary<string, BrowserConnection> Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, BrowserConnection>();
            }

            return JsonSerializer.Deserialize<Dictionary<string, BrowserConnection>>(File.ReadAllText(path), JsonOptions)
                ?? new Dictionary<string, BrowserConnection>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<string, BrowserConnection>();
        }
    }

    /// <summary>Stamps <paramref name="browser"/> as seen now; atomic replace of the file.</summary>
    public static void Record(string path, string browser, string? extensionVersion, DateTime? nowUtc = null)
    {
        var all = new Dictionary<string, BrowserConnection>(Read(path))
        {
            [browser] = new BrowserConnection(nowUtc ?? DateTime.UtcNow, extensionVersion),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
