// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// The ONE way to derive the per-domain article-layout key from a URL
/// (workspace-8qyo). The tuner saves and the pipeline loads with the same
/// normalization (lower-case host, no www.) — a mismatch here silently
/// orphans saved configs.
/// </summary>
internal static class ArticleLayoutDomains
{
    /// <summary>Lower-cased host without a leading <c>www.</c>, or null for non-URLs.</summary>
    public static string? FromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
