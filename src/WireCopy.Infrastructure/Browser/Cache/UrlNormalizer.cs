// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;

namespace WireCopy.Infrastructure.Browser.Cache;

/// <summary>
/// Normalizes URLs for consistent cache key generation.
/// </summary>
public static class UrlNormalizer
{
    private static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "fbclid", "gclid", "ref", "source", "mc_cid", "mc_eid"
    };

    /// <summary>
    /// Normalizes a URL for use as a cache key.
    /// Strips fragments, tracking parameters, trailing slashes, and lowercases scheme/host.
    /// </summary>
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        // Rebuild with normalized scheme + host (lowercase)
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Fragment = string.Empty // Strip fragments
        };

        // Filter out tracking query parameters
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var filteredParams = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p =>
                {
                    var key = p.Split('=')[0];
                    return !TrackingParams.Contains(key);
                })
                .ToArray();

            builder.Query = filteredParams.Length > 0
                ? string.Join("&", filteredParams)
                : string.Empty;
        }

        var normalized = builder.Uri.ToString();

        // Strip trailing slash (but not for root paths)
        if (normalized.Length > 1 && normalized.EndsWith('/') &&
            builder.Path != "/")
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }

    /// <summary>
    /// Extracts the origin (scheme + host + port) from a URL for same-origin comparison.
    /// </summary>
    public static string? GetOrigin(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}" +
                   (uri.IsDefaultPort ? string.Empty : $":{uri.Port}");
        }

        return null;
    }

    /// <summary>
    /// Checks whether two URLs share the same origin.
    /// </summary>
    public static bool IsSameOrigin(string url1, string url2)
    {
        var origin1 = GetOrigin(url1);
        var origin2 = GetOrigin(url2);
        return origin1 != null && string.Equals(origin1, origin2, StringComparison.Ordinal);
    }
}
