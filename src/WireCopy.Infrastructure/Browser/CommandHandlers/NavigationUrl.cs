// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-khpe.3: shared normalize-and-validate step for user-typed
/// navigation targets (the ':open' command argument and the launcher's 'o'
/// URL bar). Bare input gets an https:// scheme, then the result must parse as
/// a well-formed absolute http/https URL — otherwise navigation is rejected
/// with a clear message instead of failing later as an opaque load error.
/// </summary>
internal static class NavigationUrl
{
    /// <summary>
    /// Normalizes <paramref name="input"/> into an absolute http(s) URL.
    /// Returns false with a human-readable <paramref name="error"/> when the
    /// input is blank, contains characters that don't form a valid URL (spaces,
    /// control characters, an empty host), or carries a non-http(s) scheme.
    /// </summary>
    public static bool TryNormalize(string? input, out string url, out string error)
    {
        url = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter a URL to open";
            return false;
        }

        var candidate = input.Trim();
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = $"Not a valid URL: {input.Trim()}";
            return false;
        }

        url = candidate;
        return true;
    }
}
