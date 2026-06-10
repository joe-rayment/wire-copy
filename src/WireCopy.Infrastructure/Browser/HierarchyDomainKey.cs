// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Derives the storage key for per-site hierarchy configs (workspace-felb).
/// The key is the lower-cased host plus ":port" when the URL carries a
/// non-default port — without the port, every local site (127.0.0.1:NNNN)
/// shares one config file, so a config saved against one local server
/// silently applies to all of them.
/// </summary>
internal static class HierarchyDomainKey
{
    /// <summary>Returns the config key for a URL, or null when it isn't an absolute URL.</summary>
    public static string? TryFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }

    /// <summary>Returns the config key for a URL, or "unknown" when it isn't an absolute URL.</summary>
    public static string FromUrl(string url) => TryFromUrl(url) ?? "unknown";
}
