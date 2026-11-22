// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// JavaScript evaluated in the docked headed browser to mirror the TUI's link-tree
/// selection as a DevTools-style highlight on the live page (workspace dock spotlight).
///
/// <para>
/// Contract with <see cref="DockSpotlight"/>: <see cref="Sync"/> takes
/// <c>{ url, text }</c>, finds the matching anchor, makes sure it is on screen, moves a
/// single overlay div onto it, and returns <c>"ok"</c> — or removes any existing overlay
/// and returns <c>"not-found"</c>. A highlight that cannot be made visible (hidden
/// element, anchor absent from the live DOM) is treated as a failure, never left stale
/// or off-screen.
/// </para>
///
/// <para>
/// Motion (workspace-ksdww): the highlight TRAVELS from the story it was on to the one
/// selected now, and the page stays where it is while that story is comfortably on
/// screen. Only a story that is off screen, or at the very edge, moves the page — as a
/// glide of the same length and easing as the highlight's own, so the two read as one
/// movement. <c>"ok"</c> is returned as the move starts; it plays out in the page.
/// </para>
/// </summary>
internal static class SpotlightScript
{
    /// <summary>
    /// Locates the anchor for <c>args.url</c> (disambiguated by <c>args.text</c>),
    /// brings it on screen if it is not comfortably there already, and moves the
    /// spotlight overlay onto it. Returns <c>"ok"</c> or <c>"not-found"</c>.
    /// </summary>
    public const string Sync = """
        (args) => {
            const KEY = '__wcSpotlight';
            const state = window[KEY] || (window[KEY] = {});
            const clear = () => {
                state.anim = null;
                if (state.overlay) { state.overlay.remove(); state.overlay = null; }
                state.el = null;
                state.box = null;
            };

        """ + Motion + """
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

            const vw = window.innerWidth || document.documentElement.clientWidth;
            const vh = window.innerHeight || document.documentElement.clientHeight;
            const here = el.getBoundingClientRect();
            if (!(here.width > 0 && here.height > 0)) { clear(); return 'not-found'; }

            // Leave the page alone while the story is comfortably on screen — clear of
            // the top and bottom eighth — so what moves is the highlight, not the page.
            // One too tall for that band rests with its top in the upper part of the
            // screen.
            const margin = vh * 0.12;
            const tall = here.height > vh - margin * 2;
            const resting = tall
                ? here.top >= 0 && here.top <= vh * 0.4
                : here.top >= margin && here.bottom <= vh - margin;
            const block = resting ? 'nearest' : (here.height > vh ? 'start' : 'center');

            // A story outside the band moves the page. Coming from another story, it
            // is brought a third of the way in from the side it came from, which leaves
            // most of the screen ahead for the moves that follow in the same direction
            // while the story just left stays in view (one step back does not move the
            // page again). With nowhere to have come from, it goes to the middle.
            const fresh = !state.overlay || !state.overlay.isConnected;
            const from = fresh || !state.box ? null : state.box;
            const to = boxFor(el);
            let aim = null;
            if (block === 'center' && from && here.height < vh / 3 && Math.abs(to.y - from.y) > 1) {
                aim = to.y > from.y
                    ? { edge: 'top', at: vh / 3 }
                    : { edge: 'bottom', at: vh * 2 / 3 };
            }
            const plan = planScroll(el, block, aim);

            const r = plan.rect;
            const visible = r.width > 0 && r.height > 0
                && r.bottom > 0 && r.right > 0 && r.top < vh && r.left < vw;
            if (!visible) { clear(); return 'not-found'; }

            if (fresh) {
                const o = document.createElement('div');
                o.id = '__wirecopy-spotlight';
                o.style.cssText =
                    'position:absolute;pointer-events:none;z-index:2147483647;' +
                    'background:rgba(106,168,221,0.33);box-sizing:border-box;' +
                    'opacity:0;transition:opacity 160ms ease-out;';
                document.documentElement.appendChild(o);
                state.overlay = o;
                state.box = null;
            }
            state.overlay.style.border = edge + 'px solid #1a73e8';
            state.overlay.style.borderRadius = (edge * 2) + 'px';
            state.overlay.style.boxShadow = '0 0 0 ' + edge + 'px rgba(26,115,232,0.22)';

            state.el = el;
            state.place = () => {
                if (state.anim) return; // the move in flight places the overlay itself
                const b = boxFor(state.el);
                if (b) setBox(b);
            };

            // Resizing the window re-fits the page to the pane (its zoom changes), and
            // the reflow can carry the selected story off screen. Bring it back — at
            // once, not as a glide: the user is dragging a window edge, not moving the
            // selection.
            state.keep = () => {
                const t = state.el;
                if (state.anim || !t || !t.isConnected) return;
                const b = t.getBoundingClientRect();
                const h = window.innerHeight || document.documentElement.clientHeight;
                if (b.height > 0 && (b.top < 0 || b.bottom > h)) {
                    bringIntoView(t, b.height > h ? 'start' : 'center', null);
                }
                state.place();
            };

            // Keep the overlay glued through manual scrolling and lazy-load
            // layout shifts (document-coordinate positioning alone can drift).
            if (!state.listening) {
                state.listening = true;
                const onMove = () => { if (state.place) state.place(); };
                const onResize = () => {
                    onMove();
                    clearTimeout(state.keepTimer);
                    state.keepTimer = setTimeout(() => { if (state.keep) state.keep(); }, 200);
                };
                window.addEventListener('scroll', onMove, { passive: true, capture: true });
                window.addEventListener('resize', onResize, { passive: true });
                if (window.ResizeObserver && document.body) {
                    new ResizeObserver(onMove).observe(document.body);
                }
            }

            // First time on this page there is nowhere to travel from: the highlight
            // comes up where it belongs (and rides along if the page has to move).
            if (!from) setBox(to);
            const travel = from ? Math.hypot(to.x - from.x, to.y - from.y) : 0;
            play({
                el, from, target: el, block, aim, moves: plan.moves,
                duration: durationFor(Math.max(travel, plan.far)),
            });
            if (fresh) {
                void state.overlay.offsetWidth; // commit opacity:0 so the fade runs
                state.overlay.style.opacity = '1';
            }
            return 'ok';
        }
        """;

    /// <summary>
    /// Reader scroll-along (no highlight box): given the source text of the paragraph
    /// the reader's cursor is on, finds the matching live block and brings it to the
    /// viewport center so the panel keeps that paragraph centered as the reader moves.
    ///
    /// <para>
    /// Matches by CONTENT, not a height fraction: normalizes whitespace/case, takes the
    /// paragraph's leading prefix as a needle, and picks the TIGHTEST (shortest) block
    /// whose text starts with it — so the actual paragraph wins over a wrapping
    /// article/section container. Returns <c>"ok"</c>, <c>"not-found"</c> (no live block
    /// matched — leave the page where it is), or <c>"too-short"</c> (anchor too short to
    /// match confidently). Any leftover link-selection overlay is removed first.
    /// </para>
    /// </summary>
    public const string ScrollToAnchor = """
        (args) => {
            const state = window['__wcSpotlight'] || (window['__wcSpotlight'] = {});
            state.anim = null;
            if (state.overlay) { state.overlay.remove(); state.overlay = null; }
            state.el = null;
            state.box = null;

        """ + Motion + """
            const norm = (s) => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const needle = norm(args.text).slice(0, 40);
            if (needle.length < 12) return 'too-short';

            const usable = (el) => {
                const r = el.getBoundingClientRect();
                if (r.width <= 0 || r.height <= 0) return false;
                const st = window.getComputedStyle(el);
                return st.visibility !== 'hidden' && st.display !== 'none';
            };

            let best = null, bestLen = Infinity;
            const blocks = document.querySelectorAll(
                'p,li,blockquote,h1,h2,h3,h4,h5,h6,figcaption,dd,pre');
            for (const el of blocks) {
                if (!usable(el)) continue;
                const t = norm(el.textContent);
                if (t.length >= needle.length && t.startsWith(needle) && t.length < bestLen) {
                    best = el;
                    bestLen = t.length;
                }
            }
            if (!best) return 'not-found';

            // The page glides to the paragraph rather than jumping to it. Rapid reader
            // movement cannot queue a backlog: each new move replaces the one in flight
            // and starts from wherever the page has got to.
            const plan = planScroll(best, 'center');
            play({ el: null, from: null, target: best, block: 'center', aim: null, moves: plan.moves, duration: durationFor(plan.far) });
            return 'ok';
        }
        """;

    /// <summary>Removes the overlay (idempotent; safe on pages never spotlighted).</summary>
    public const string Clear = """
        () => {
            const state = window['__wcSpotlight'];
            if (state) {
                state.anim = null;
                if (state.overlay) { state.overlay.remove(); state.overlay = null; }
                state.el = null;
                state.box = null;
            }
            return 'cleared';
        }
        """;

    /// <summary>
    /// Shared by the scripts above; expects <c>state</c> (the per-document spotlight
    /// state object) in scope. Positions are document coordinates.
    /// </summary>
    private const string Motion = """
            const mix = (a, b, e) => a + (b - a) * e;

            // The overlay is drawn in CSS pixels, and the pane shows the page zoomed
            // out: keep the frame about three device pixels thick whatever the zoom.
            // A link of a few characters (a comment count) would otherwise be a speck:
            // the box never gets smaller than about sixteen device pixels a side.
            const dpr = window.devicePixelRatio || 1;
            const edge = Math.max(1.5, 3 / dpr);
            const pad = Math.max(2, 4 / dpr);
            const least = 16 / dpr;

            const boxFor = (t) => {
                if (!t || !t.isConnected) return null;
                const b = t.getBoundingClientRect();
                const w = Math.max(b.width + pad * 2, least);
                const h = Math.max(b.height + pad * 2, least);
                return {
                    x: b.left + window.scrollX + (b.width - w) / 2,
                    y: b.top + window.scrollY + (b.height - h) / 2,
                    w, h,
                };
            };
            const setBox = (b) => {
                const o = state.overlay;
                if (!o || !b) return;
                state.box = b;
                o.style.left = b.x + 'px';
                o.style.top = b.y + 'px';
                o.style.width = b.w + 'px';
                o.style.height = b.h + 'px';
            };

            // Bring el into view, at once, the browser's own way (nested scrollers,
            // sticky headers and scroll-padding are then its problem, not ours).
            // 'instant' throughout — a site's own "scroll-behavior: smooth" would
            // otherwise turn each of these into an animation of its own.
            // aim, when given, says where on screen the story should come to rest
            // instead: { edge: 'top' | 'bottom', at: px from the top of the screen }.
            const bringIntoView = (el, block, aim) => {
                el.scrollIntoView({ block, inline: 'nearest', behavior: 'instant' });
                const root = document.scrollingElement;
                if (aim && root) {
                    const r = el.getBoundingClientRect();
                    const further = (aim.edge === 'top' ? r.top : r.bottom) - aim.at;
                    const most = root.scrollHeight - root.clientHeight;
                    root.scrollTo({ top: Math.max(0, Math.min(most, root.scrollTop + further)), behavior: 'instant' });
                }
                return el.getBoundingClientRect();
            };

            // Bring el into view, note where everything that scrolled ended up, and put
            // it all back: the result is the list of moves to PLAY instead of jump, plus
            // where el will be afterwards.
            const planScroll = (el, block, aim) => {
                const chain = [];
                for (let n = el.parentElement; n; n = n.parentElement) {
                    chain.push({ node: n, x0: n.scrollLeft, y0: n.scrollTop });
                }
                const rect = bringIntoView(el, block, aim);
                const moves = [];
                let far = 0;
                for (const s of chain) {
                    s.x1 = s.node.scrollLeft;
                    s.y1 = s.node.scrollTop;
                    const d = Math.hypot(s.x1 - s.x0, s.y1 - s.y0);
                    if (d > 0.5) { moves.push(s); far = Math.max(far, d); }
                }
                for (const s of moves) s.node.scrollTo({ left: s.x0, top: s.y0, behavior: 'instant' });
                return { moves, rect, far };
            };

            // A short hop is quick; a move across the page takes longer, never long.
            const durationFor = (distance) =>
                200 + 260 * Math.min(1, distance / Math.max(1, window.innerHeight));

            // Plays one move: the scrollers glide to where planScroll found them and
            // the overlay (when there is one) travels from where it is to its target,
            // which is re-measured every frame so a page still loading cannot leave the
            // overlay behind. A newer move simply replaces this one mid-flight.
            const play = (a) => {
                state.anim = a;
                const t0 = performance.now();
                for (const s of a.moves) { s.lx = s.x0; s.ly = s.y0; }
                const at = (e) => {
                    for (const s of a.moves) {
                        if (s.theirs) continue;
                        // Moved by someone else since our last step — the user's wheel,
                        // the page's own script. It is theirs now; stop steering it.
                        if (Math.abs(s.node.scrollLeft - s.lx) > 2 || Math.abs(s.node.scrollTop - s.ly) > 2) {
                            s.theirs = true;
                            continue;
                        }
                        s.node.scrollTo({ left: mix(s.x0, s.x1, e), top: mix(s.y0, s.y1, e), behavior: 'instant' });
                        s.lx = s.node.scrollLeft;
                        s.ly = s.node.scrollTop;
                    }
                    const t = boxFor(a.el);
                    if (t) {
                        const f = a.from;
                        setBox(f
                            ? { x: mix(f.x, t.x, e), y: mix(f.y, t.y, e), w: mix(f.w, t.w, e), h: mix(f.h, t.h, e) }
                            : t);
                    }
                };
                const land = () => {
                    at(1);
                    state.anim = null;
                    // The page may have shifted while we travelled (images arriving
                    // above the story). Land on the story as it is now — unless the
                    // user took the scroll, in which case the page is where they put it.
                    if (a.target && a.target.isConnected && !a.moves.some(s => s.theirs)) {
                        bringIntoView(a.target, a.block, a.aim);
                    }
                    const t = boxFor(a.el);
                    if (t) setBox(t);
                };
                const frame = (now) => {
                    if (state.anim !== a) return;
                    const p = Math.max(0, Math.min(1, (now - t0) / a.duration));
                    if (p >= 1) { land(); return; }
                    at(1 - Math.pow(1 - p, 3));
                    requestAnimationFrame(frame);
                };
                requestAnimationFrame(frame);

                // A page that is not being painted gets no animation frames: land the
                // move anyway, so the page never rests half-way.
                setTimeout(() => { if (state.anim === a) land(); }, a.duration + 300);
            };

        """;
}
