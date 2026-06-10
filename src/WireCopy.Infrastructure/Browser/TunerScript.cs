// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// JavaScript evaluated in the LENS tab to highlight every element a candidate
/// selector matches (workspace-8qyo / workspace-wylw): the selector becomes an
/// input device — AI/heuristics propose, the live page shows, the user confirms.
/// Supports BOTH dialects: XPath (article configs) and CSS (link-list sections).
/// Same overlay pattern as <see cref="SpotlightScript"/>; one call replaces the
/// previous highlight set; <see cref="Clear"/> removes everything.
/// </summary>
internal static class TunerScript
{
    /// <summary>
    /// Highlights all matches of <c>args.selector</c> (<c>args.dialect</c> =
    /// 'xpath' | 'css'), scrolls the first into view, and returns the match
    /// count (capped at 80 highlights for pathological selectors).
    /// </summary>
    public const string Highlight = """
        (args) => {
            const KEY = '__wcTuner';
            const state = window[KEY] || (window[KEY] = { boxes: [] });
            for (const b of state.boxes) { try { b.remove(); } catch {} }
            state.boxes = [];

            let nodes = [];
            try {
                if (args.dialect === 'xpath') {
                    const it = document.evaluate(
                        args.selector, document, null,
                        XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
                    for (let i = 0; i < it.snapshotLength; i++) {
                        const n = it.snapshotItem(i);
                        if (n && n.nodeType === 1) nodes.push(n);
                    }
                } else {
                    nodes = Array.from(document.querySelectorAll(args.selector));
                }
            } catch { return -1; }

            const count = nodes.length;
            nodes = nodes.slice(0, 80);
            let first = null;
            for (const el of nodes) {
                const r = el.getBoundingClientRect();
                if (r.width <= 0 || r.height <= 0) continue;
                if (!first) first = el;
                const o = document.createElement('div');
                o.className = '__wirecopy-tuner-box';
                o.style.cssText =
                    'position:absolute;pointer-events:none;z-index:2147483646;' +
                    'background:rgba(255,171,64,0.25);border:2px solid #f57c00;' +
                    'border-radius:3px;box-sizing:border-box;';
                o.style.left = (r.left + window.scrollX - 2) + 'px';
                o.style.top = (r.top + window.scrollY - 2) + 'px';
                o.style.width = (r.width + 4) + 'px';
                o.style.height = (r.height + 4) + 'px';
                document.documentElement.appendChild(o);
                state.boxes.push(o);
            }

            if (first) first.scrollIntoView({ block: 'center', behavior: 'auto' });
            return count;
        }
        """;

    /// <summary>Removes every tuner highlight (idempotent).</summary>
    public const string Clear = """
        () => {
            const state = window['__wcTuner'];
            if (state) {
                for (const b of state.boxes) { try { b.remove(); } catch {} }
                state.boxes = [];
            }
            return 'cleared';
        }
        """;
}
