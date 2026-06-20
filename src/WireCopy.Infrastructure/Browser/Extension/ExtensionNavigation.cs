// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.Extension;

/// <summary>
/// Pure navigation decisions for the host-browser-as-renderer model (workspace-blg5 / workspace-blg5.1).
/// Kept free of I/O so the "click in place vs. reload the tab" rule can be unit-tested in isolation.
/// </summary>
public static class ExtensionNavigation
{
    /// <summary>
    /// True when <paramref name="targetUrl"/> is a SAME-DOCUMENT fragment of <paramref name="currentUrl"/>
    /// — i.e. it differs only by its <c>#fragment</c> (same scheme/host/path/query, and the target carries
    /// a non-empty fragment). For these links the right "drive the browser underneath" action is to CLICK
    /// the anchor on the already-loaded page (an in-page jump the real browser handles natively), not to
    /// re-navigate the tab — which would reload the whole document just to scroll to a section and restart
    /// the overlay.
    /// </summary>
    public static bool IsSameDocumentFragment(string? currentUrl, string? targetUrl)
    {
        if (string.IsNullOrEmpty(currentUrl) || string.IsNullOrEmpty(targetUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var cur)
            || !Uri.TryCreate(targetUrl, UriKind.Absolute, out var tgt))
        {
            return false;
        }

        // The target must actually point at a fragment; "#" alone (empty fragment) is not a jump.
        var fragment = tgt.Fragment;
        if (string.IsNullOrEmpty(fragment) || fragment == "#")
        {
            return false;
        }

        return string.Equals(cur.Scheme, tgt.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(cur.Authority, tgt.Authority, StringComparison.OrdinalIgnoreCase)
            && string.Equals(cur.AbsolutePath, tgt.AbsolutePath, StringComparison.Ordinal)
            && string.Equals(cur.Query, tgt.Query, StringComparison.Ordinal);
    }
}
