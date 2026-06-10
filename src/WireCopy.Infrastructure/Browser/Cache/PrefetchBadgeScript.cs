// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.Cache;

/// <summary>
/// JavaScript evaluated in the PREFETCH tab so a user who brings the shared
/// browser up mid-cache instantly understands the flipping pages
/// (workspace-1rfd): the tab title carries the caching progress and a small
/// non-interactive corner badge labels the page itself. Same injection pattern
/// as <see cref="SpotlightScript"/>; idempotent; cleared when prefetch goes idle.
/// </summary>
internal static class PrefetchBadgeScript
{
    /// <summary>
    /// Applies the badge + title prefix. Args: <c>{ done, total }</c>.
    /// </summary>
    public const string Apply = """
        (args) => {
            const label = `⧖ caching ${args.done}/${args.total} · WireCopy`;
            try {
                if (!document.title.startsWith('⧖')) {
                    document.title = label + ' — ' + document.title;
                } else {
                    document.title = label + ' — ' + document.title.split(' — ').slice(1).join(' — ');
                }
            } catch { /* sandboxed pages may freeze title */ }

            const KEY = '__wcPrefetchBadge';
            let el = window[KEY];
            if (!el || !el.isConnected) {
                el = document.createElement('div');
                el.id = '__wirecopy-prefetch-badge';
                el.style.cssText =
                    'position:fixed;top:10px;right:10px;z-index:2147483647;' +
                    'pointer-events:none;background:rgba(26,115,232,0.92);color:#fff;' +
                    'font:600 12px system-ui,sans-serif;padding:6px 10px;border-radius:6px;' +
                    'box-shadow:0 2px 8px rgba(0,0,0,0.25);';
                document.documentElement.appendChild(el);
                window[KEY] = el;
            }
            el.textContent = `WireCopy is caching this page (${args.done}/${args.total})`;
            return 'ok';
        }
        """;

    /// <summary>Removes the badge (idempotent; title left as-is on the final page).</summary>
    public const string Clear = """
        () => {
            const el = window['__wcPrefetchBadge'];
            if (el) { el.remove(); window['__wcPrefetchBadge'] = null; }
            return 'cleared';
        }
        """;
}
