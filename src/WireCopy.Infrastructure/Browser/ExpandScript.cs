// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-2cz4 — JavaScript evaluated on the LENS tab to expand content a
/// site collapsed at the mobile viewport ("read more" regions). The TUI's link
/// extraction sees the full DOM, so collapsed stories appear in the list but
/// their anchors have no layout — <see cref="SpotlightScript.Sync"/> then skips
/// them and the visible link between list and page breaks.
///
/// <para>Safety contract: NEVER click an element that navigates (a real
/// <c>href</c> — a "read more" that IS the article link must stay untouched);
/// clicks are capped; everything is advisory and exception-safe.</para>
/// </summary>
internal static class ExpandScript
{
    /// <summary>
    /// Proactive pass after a follow-navigation: opens every closed
    /// <c>&lt;details&gt;</c> and clicks visible, NON-NAVIGATING expanders whose
    /// accessible text reads like "read/show/load more". Returns the number of
    /// expansions performed (capped).
    /// </summary>
    public const string ExpandAll = """
        () => {
            const MAX_CLICKS = 5;
            let acted = 0;

            for (const d of document.querySelectorAll('details:not([open])')) {
                d.open = true; acted++;
            }

            const moreish = (el) => {
                const txt = ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || ''))
                    .trim().toLowerCase();
                return txt.length < 80 &&
                    /\b((read|show|see|view|load)\s+more|more\s+(stories|headlines|articles)|continue\s+reading)\b/.test(txt);
            };
            const navigates = (el) => {
                const a = el.closest('a[href]');
                if (!a) return false;
                const href = a.getAttribute('href') || '';
                return href !== '' && href !== '#' && !href.startsWith('#') && !/^javascript:/i.test(href);
            };

            let clicks = 0;
            for (const el of document.querySelectorAll('button, [role="button"], summary, a, [aria-expanded="false"]')) {
                if (clicks >= MAX_CLICKS) break;
                const r = el.getBoundingClientRect();
                if (r.width <= 0 || r.height <= 0) continue;
                if (!moreish(el) || navigates(el)) continue;
                try { el.click(); clicks++; acted++; } catch {}
            }

            return acted;
        }
        """;

    /// <summary>
    /// Targeted rescue for one hidden spotlight target <c>{ url, text }</c>:
    /// finds the anchor (same URL normalization as the spotlight), and when it
    /// has no layout walks its ancestors opening closed <c>details</c> and
    /// clicking the collapse toggle that controls the hidden region
    /// (aria-controls back-reference or a nearby aria-expanded=false control —
    /// never a navigating link). Returns 'visible' (nothing to do),
    /// 'expanded' (retry the spotlight), or 'not-found'.
    /// </summary>
    public const string RevealTarget = """
        (args) => {
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
            if (!target) return 'not-found';
            const anchors = Array.from(document.querySelectorAll('a[href]'))
                .filter(a => norm(a.href) === target);
            if (anchors.length === 0) return 'not-found';
            const laidOut = (el) => {
                const r = el.getBoundingClientRect();
                return r.width > 0 && r.height > 0;
            };
            if (anchors.some(laidOut)) return 'visible';

            const navigates = (el) => {
                const a = el.closest('a[href]');
                if (!a) return false;
                const href = a.getAttribute('href') || '';
                return href !== '' && href !== '#' && !href.startsWith('#') && !/^javascript:/i.test(href);
            };

            let acted = 0;
            const MAX = 3;
            for (const a of anchors) {
                for (let el = a; el && el !== document.body && acted < MAX; el = el.parentElement) {
                    if (el.tagName === 'DETAILS' && !el.open) {
                        el.open = true; acted++;
                        continue;
                    }

                    if (el.id) {
                        const ctl = document.querySelector(
                            '[aria-expanded="false"][aria-controls~="' + CSS.escape(el.id) + '"]');
                        if (ctl && !navigates(ctl)) {
                            try { ctl.click(); acted++; } catch {}
                            continue;
                        }
                    }

                    const ctl2 = el.querySelector(':scope > [aria-expanded="false"], :scope > * > [aria-expanded="false"]');
                    if (ctl2 && !navigates(ctl2)) {
                        try { ctl2.click(); acted++; } catch {}
                    }
                }

                if (acted > 0) break;
            }

            return acted > 0 ? 'expanded' : 'not-found';
        }
        """;
}
