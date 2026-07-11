// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Derives the storage key for per-site hierarchy configs (workspace-felb).
/// The key is the lower-cased host plus ":port" when the URL carries a
/// non-default port — without the port, every local site (127.0.0.1:NNNN)
/// shares one config file, so a config saved against one local server
/// silently applies to all of them.
/// workspace-42q8.1: a leading "www." is STRIPPED from the key — www.nytimes.com
/// and nytimes.com are the same site, and keying them separately made a layout
/// saved on one host variant invisible from the other (the schedules screen then
/// claimed the site "has no saved layout"). Legacy files saved under the www key
/// are still found via <see cref="HierarchyConfigStore"/>'s variant probe.
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

        var host = StripWww(uri.Host.ToLowerInvariant());
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }

    /// <summary>Returns the config key for a URL, or "unknown" when it isn't an absolute URL.</summary>
    public static string FromUrl(string url) => TryFromUrl(url) ?? "unknown";

    /// <summary>
    /// Drops a leading "www." when the remainder is still a real dotted host
    /// ("www.x.com" → "x.com"); bare "www" hosts and everything else pass through.
    /// </summary>
    internal static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4 && host[4..].Contains('.')
            ? host[4..]
            : host;
}
