// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>JS for applying/removing Set-of-Marks badges on the live page.</summary>
internal static class ScreenshotMarkScript
{
    /// <summary>Container element id — applied badges live under one root for easy removal.</summary>
    public const string RootId = "__wc_som";

    /// <summary>
    /// Applies numbered badges for the supplied marks (arg: array of {i, url}).
    /// Anchors resolve by their fully-resolved <c>a.href</c>; off-screen or
    /// zero-size anchors are skipped. Returns the number of badges drawn.
    /// </summary>
    public const string Apply = """
        (marks) => {
            const prev = document.getElementById('__wc_som');
            if (prev) prev.remove();
            const norm = (u) => {
                u = String(u).replace(/#.*$/, '');
                return u.endsWith('/') ? u.slice(0, -1) : u;
            };
            const byHref = new Map();
            for (const a of document.querySelectorAll('a[href]')) {
                const key = norm(a.href);
                if (!byHref.has(key)) byHref.set(key, a);
            }
            const root = document.createElement('div');
            root.id = '__wc_som';
            root.style.cssText = 'position:absolute;left:0;top:0;z-index:2147483647;pointer-events:none;';
            let n = 0;
            for (const m of marks) {
                const a = byHref.get(norm(m.url));
                if (!a) continue;
                const r = a.getBoundingClientRect();
                if (r.width < 1 || r.height < 1) continue;
                const b = document.createElement('div');
                b.textContent = String(m.i);
                b.style.cssText = 'position:absolute;font:bold 11px monospace;color:#fff;' +
                    'background:#c2185b;padding:0 3px;border-radius:3px;line-height:14px;';
                b.style.left = Math.max(0, r.left + window.scrollX - 2) + 'px';
                b.style.top = Math.max(0, r.top + window.scrollY - 7) + 'px';
                root.appendChild(b);
                n++;
            }
            document.body.appendChild(root);
            return n;
        }
        """;

    /// <summary>Removes every badge (idempotent).</summary>
    public const string Remove = """
        () => {
            const e = document.getElementById('__wc_som');
            if (e) e.remove();
            return true;
        }
        """;
}
