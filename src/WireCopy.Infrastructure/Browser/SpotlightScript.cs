// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// JavaScript evaluated in the docked headed browser to mirror the TUI's link-tree
/// selection as a DevTools-style highlight on the live page (workspace dock spotlight).
///
/// <para>
/// Contract with <see cref="DockSpotlight"/>: <see cref="Sync"/> takes
/// <c>{ url, text }</c>, finds the matching anchor, scrolls it to the viewport
/// center, draws/moves a single overlay div, and returns <c>"ok"</c> — or removes
/// any existing overlay and returns <c>"not-found"</c>. A highlight that cannot be
/// made visible (hidden element, anchor absent from the live DOM) is treated as a
/// failure, never left stale or off-screen.
/// </para>
/// </summary>
internal static class SpotlightScript
{
    /// <summary>
    /// Locates the anchor for <c>args.url</c> (disambiguated by <c>args.text</c>),
    /// scrolls it into the viewport center, and positions the spotlight overlay
    /// over it. Returns <c>"ok"</c> or <c>"not-found"</c>.
    /// </summary>
    public const string Sync = """
        (args) => {
            const KEY = '__wcSpotlight';
            const state = window[KEY] || (window[KEY] = {});
            const clear = () => {
                if (state.overlay) { state.overlay.remove(); state.overlay = null; }
                state.el = null;
            };

            const norm = (u) => {
                try {
                    const x = new URL(u, location.href);
                    x.hash = '';
                    let s = x.href;
                    if (s.endsWith('/')) s = s.slice(0, -1);
                    return s;
                } catch { return (u || '').trim(); }
            };

            const target = norm(args.url);
            if (!target) { clear(); return 'not-found'; }

            const anchors = Array.from(document.querySelectorAll('a[href]'))
                .filter(a => norm(a.href) === target);

            // Same story can appear as headline + thumbnail + teaser; prefer the
            // anchor whose text matches the tree label, then any laid-out anchor.
            const wanted = (args.text || '').trim().toLowerCase();
            let el = null;
            if (anchors.length > 1 && wanted) {
                el = anchors.find(a => {
                    const t = (a.innerText || '').trim().toLowerCase();
                    return t && (t.includes(wanted) || wanted.includes(t));
                }) || null;
            }
            if (!el) {
                el = anchors.find(a => {
                    const r = a.getBoundingClientRect();
                    return r.width > 0 && r.height > 0;
                }) || anchors[0] || null;
            }
            if (!el) { clear(); return 'not-found'; }

            el.scrollIntoView({ block: 'center', behavior: 'auto' });

            const vw = window.innerWidth || document.documentElement.clientWidth;
            const vh = window.innerHeight || document.documentElement.clientHeight;
            const r = el.getBoundingClientRect();
            const visible = r.width > 0 && r.height > 0
                && r.bottom > 0 && r.right > 0 && r.top < vh && r.left < vw;
            if (!visible) { clear(); return 'not-found'; }

            if (!state.overlay || !state.overlay.isConnected) {
                const o = document.createElement('div');
                o.id = '__wirecopy-spotlight';
                o.style.cssText =
                    'position:absolute;pointer-events:none;z-index:2147483647;' +
                    'background:rgba(106,168,221,0.33);border:1.5px solid #1a73e8;' +
                    'border-radius:3px;box-sizing:border-box;' +
                    'transition:top 120ms ease-out,left 120ms ease-out,' +
                    'width 120ms ease-out,height 120ms ease-out;';
                document.documentElement.appendChild(o);
                state.overlay = o;
            }

            state.el = el;
            state.place = () => {
                const t = state.el, o = state.overlay;
                if (!t || !o || !t.isConnected) return;
                const b = t.getBoundingClientRect();
                const pad = 2;
                o.style.left = (b.left + window.scrollX - pad) + 'px';
                o.style.top = (b.top + window.scrollY - pad) + 'px';
                o.style.width = (b.width + pad * 2) + 'px';
                o.style.height = (b.height + pad * 2) + 'px';
            };

            // Keep the overlay glued through manual scrolling and lazy-load
            // layout shifts (document-coordinate positioning alone can drift).
            if (!state.listening) {
                state.listening = true;
                const onMove = () => { if (state.place) state.place(); };
                window.addEventListener('scroll', onMove, { passive: true, capture: true });
                window.addEventListener('resize', onMove, { passive: true });
            }

            state.place();
            return 'ok';
        }
        """;

    /// <summary>Removes the overlay (idempotent; safe on pages never spotlighted).</summary>
    public const string Clear = """
        () => {
            const state = window['__wcSpotlight'];
            if (state) {
                if (state.overlay) { state.overlay.remove(); state.overlay = null; }
                state.el = null;
            }
            return 'cleared';
        }
        """;
}
