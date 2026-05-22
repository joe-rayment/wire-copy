// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics.CodeAnalysis;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Parses a Google Cloud Storage feed URL down to the bare bucket name
/// (workspace-spue). The Setup-screen bucket prompt asks for the full
/// public URL the user wants their feed to live at — this helper unwraps
/// any of the four forms we accept back into the bucket the rest of the
/// stack expects.
///
/// <para>
/// Forms accepted:
/// </para>
/// <list type="bullet">
/// <item><c>https://storage.googleapis.com/{bucket}/...</c> — path-style</item>
/// <item><c>https://{bucket}.storage.googleapis.com/...</c> — virtual-host-style</item>
/// <item><c>gs://{bucket}/...</c> — gsutil/CLI form</item>
/// <item><c>{bucket}</c> — bare (back-compat with earlier UI)</item>
/// </list>
/// </summary>
internal static class BucketUrlParser
{
    private const string PathStyleHost = "storage.googleapis.com";
    private const string VirtualHostSuffix = ".storage.googleapis.com";

    /// <summary>
    /// Build the canonical public feed URL the prompt should pre-fill /
    /// echo back to the user. <c>null</c> when no bucket has been chosen.
    /// </summary>
    public static string? BuildFeedUrl(string? bucketName)
    {
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            return null;
        }

        return $"https://storage.googleapis.com/{bucketName.Trim()}/feed.xml";
    }

    /// <summary>
    /// Extract the bucket name from any of the four accepted input forms.
    /// Returns <c>null</c> when the input is empty, whitespace, or cannot
    /// be parsed — callers should reject with a validator message rather
    /// than silently falling back.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1865:Use char overload",
        Justification = "String prefix/suffix checks are clearer with multi-char literals.")]
    public static string? ParseBucketName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var s = input.Trim();

        if (s.StartsWith("gs://", StringComparison.OrdinalIgnoreCase))
        {
            return TrimPath(s.Substring("gs://".Length));
        }

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not "http" and not "https")
            {
                // Unknown scheme (mailto:, file:, etc.) — not a feed URL.
                return null;
            }

            var host = uri.Host;
            if (string.Equals(host, PathStyleHost, StringComparison.OrdinalIgnoreCase))
            {
                // https://storage.googleapis.com/{bucket}/...
                var path = uri.AbsolutePath.TrimStart('/');
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                var slash = path.IndexOf('/', StringComparison.Ordinal);
                return Sanitize(slash < 0 ? path : path.Substring(0, slash));
            }

            if (host.EndsWith(VirtualHostSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // https://{bucket}.storage.googleapis.com/...
                var bucket = host.Substring(0, host.Length - VirtualHostSuffix.Length);
                return Sanitize(bucket);
            }

            // Some other absolute URL — we don't know how to derive a bucket from it.
            return null;
        }

        // Bare bucket name (back-compat). Some users still paste just `my-bucket`.
        return Sanitize(s);
    }

    /// <summary>
    /// True when the input looks like one of the URL forms (vs. a bare name).
    /// Used by callers to decide whether to echo the URL or the bare bucket.
    /// </summary>
    public static bool LooksLikeUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var s = input.Trim();
        return s.StartsWith("gs://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TrimPath(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var slash = s.IndexOf('/', StringComparison.Ordinal);
        return Sanitize(slash < 0 ? s : s.Substring(0, slash));
    }

    private static string? Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
