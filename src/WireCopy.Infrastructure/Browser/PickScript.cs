// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// workspace-6yb7.5: JavaScript evaluated in the LENS tab to let the user
/// answer a wizard question by CLICKING the actual element on the live page.
/// <see cref="Arm"/> installs a capture-phase click trap (links never
/// navigate while armed) plus a hover outline; the C# side polls
/// <see cref="Poll"/> on animation ticks and <see cref="Disarm"/> always
/// removes every listener and overlay. While armed, takeover marking
/// (<c>__wcLastUserInput</c>) is suppressed via a property interceptor so a
/// pick click is wizard input, not a human takeover that pauses prefetch.
/// </summary>
internal static class PickScript
{
    /// <summary>
    /// Arms pick mode. Idempotent — re-arming while armed returns 'already-armed'.
    /// The hover outline follows the link under the cursor; a click on a link is
    /// swallowed (no navigation) and recorded as {href, text, parent} where
    /// parent mirrors LinkExtractor.GetParentSelector's format
    /// (up to 5 ancestors, 'tag.class1.class2#id &gt; …', anchor excluded).
    /// </summary>
    public const string Arm = """
        () => {
            const KEY = '__wcPick';
            if (window[KEY] && window[KEY].active) return 'already-armed';

            const state = { active: true, result: null };
            window[KEY] = state;

            // Suppress takeover marking while armed: pick interaction is wizard
            // input, not a human takeover. Restored verbatim on disarm.
            state.savedInput = window.__wcLastUserInput || 0;
            try {
                Object.defineProperty(window, '__wcLastUserInput', {
                    get: () => state.savedInput,
                    set: () => {},
                    configurable: true,
                });
            } catch { /* non-configurable: takeover pause is merely cosmetic */ }

            const findAnchor = (el) => {
                while (el && el.nodeType === 1) {
                    if (el.tagName === 'A' && el.getAttribute('href')) return el;
                    el = el.parentElement;
                }
                return null;
            };

            const outline = document.createElement('div');
            outline.className = '__wirecopy-pick-outline';
            outline.style.cssText =
                'position:absolute;pointer-events:none;z-index:2147483647;display:none;' +
                'border:3px solid #2196f3;border-radius:3px;box-sizing:border-box;' +
                'background:rgba(33,150,243,0.15);';
            document.documentElement.appendChild(outline);
            state.outline = outline;

            const move = (ev) => {
                const a = findAnchor(ev.target);
                if (!a) { outline.style.display = 'none'; return; }
                const r = a.getBoundingClientRect();
                outline.style.display = 'block';
                outline.style.left = (r.left + window.scrollX - 3) + 'px';
                outline.style.top = (r.top + window.scrollY - 3) + 'px';
                outline.style.width = (r.width + 6) + 'px';
                outline.style.height = (r.height + 6) + 'px';
            };

            const click = (ev) => {
                const a = findAnchor(ev.target);
                if (!a) return;
                ev.preventDefault();
                ev.stopImmediatePropagation();

                const parts = [];
                let el = a.parentElement;
                for (let depth = 0; el && el.nodeType === 1 && el !== document.documentElement && depth < 5; depth++) {
                    let part = el.tagName.toLowerCase();
                    const classes = Array.from(el.classList).slice(0, 2);
                    if (classes.length) part += '.' + classes.join('.');
                    if (el.id) part += '#' + el.id;
                    parts.unshift(part);
                    el = el.parentElement;
                }

                state.result = {
                    href: a.href,
                    text: (a.innerText || a.textContent || '').trim().slice(0, 300),
                    parent: parts.join(' > '),
                };
            };

            state.handlers = { move, click };
            window.addEventListener('pointermove', move, { capture: true, passive: true });
            window.addEventListener('click', click, { capture: true });
            return 'armed';
        }
        """;

    /// <summary>
    /// Returns the recorded pick as a JSON string ('' while nothing picked) and
    /// clears it, so each poll observes at most one pick.
    /// </summary>
    public const string Poll = """
        () => {
            const state = window['__wcPick'];
            if (!state || !state.result) return '';
            const r = state.result;
            state.result = null;
            return JSON.stringify(r);
        }
        """;

    /// <summary>Removes listeners, outline, and the takeover-suppression patch (idempotent).</summary>
    public const string Disarm = """
        () => {
            const state = window['__wcPick'];
            if (!state) return 'disarmed';
            try {
                if (state.handlers) {
                    window.removeEventListener('pointermove', state.handlers.move, { capture: true });
                    window.removeEventListener('click', state.handlers.click, { capture: true });
                }
                if (state.outline) state.outline.remove();
                const saved = state.savedInput || 0;
                try {
                    Object.defineProperty(window, '__wcLastUserInput', {
                        value: saved, writable: true, configurable: true,
                    });
                } catch { }
            } finally {
                delete window['__wcPick'];
            }
            return 'disarmed';
        }
        """;
}
