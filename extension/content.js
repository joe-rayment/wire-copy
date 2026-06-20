// Wire Copy — content script (workspace-kryh / workspace-iohy).
//
// The in-page half of the bridge. It (1) docks the Wire Copy TUI as a top-layer overlay — an
// extension-owned <iframe> surface, NOT a frame of the target site and NOT a second tab — with the
// real page visible underneath; (2) captures the page's RENDERED DOM on backend request and on SPA
// route changes; (3) drives the underlying page (scroll / highlight / click) per backend commands.
// One tab, one window: the real site is the tab's own top-level document.
(function () {
  if (window.__wireCopyContentLoaded) return;
  window.__wireCopyContentLoaded = true;

  const OVERLAY_ID = "wire-copy-overlay";
  const SPLITTER_ID = "wire-copy-splitter";
  const SPOT_ID = "__wire-copy-spotlight";
  const AUTO_SHOW = true; // dock on load; toggle with the toolbar button / Alt+Shift+W.
  const MIN_PANEL_PX = 320; // never collapse the TUI below a usable width
  const DEFAULT_SPLIT = 0.6; // TUI-majority split when a live page is shown (workspace-blg5.7)

  let sessionId = (crypto.randomUUID ? crypto.randomUUID() : String(Math.random())).replace(/-/g, "");
  let overlay = null;
  let splitter = null;
  let visible = false;
  // Layout state (workspace-blg5.7 — the never-empty law for the extension overlay):
  //   "full"  = the overlay covers the whole tab (launcher / no live page underneath worth showing);
  //   "split" = the overlay docks to `splitFrac` of the width, the live site shows on the right.
  // The backend drives this over /ws/ext ({type:"layout"}) from the same content signal the legacy web
  // pane uses (WebPaneDecision). We start FULL so the launcher (the first screen) is never a thin strip.
  let layoutMode = "full";
  let splitFrac = DEFAULT_SPLIT;

  // ---- backend address (shared with the overlay terminal) -------------------------------------
  function getBackend() {
    return new Promise((resolve) => {
      try {
        chrome.storage.local.get("backend", (r) => resolve((r && r.backend) || "ws://127.0.0.1:5099"));
      } catch {
        resolve("ws://127.0.0.1:5099");
      }
    });
  }

  // ---- overlay (the docked TUI surface) -------------------------------------------------------
  async function mountOverlay() {
    if (overlay) { setVisible(true); return; }
    const backend = await getBackend();
    overlay = document.createElement("iframe");
    overlay.id = OVERLAY_ID;
    overlay.setAttribute("allow", "clipboard-read; clipboard-write");
    const src = chrome.runtime.getURL("overlay.html") +
      `?session=${encodeURIComponent(sessionId)}&backend=${encodeURIComponent(backend)}`;
    overlay.src = src;
    Object.assign(overlay.style, {
      position: "fixed", top: "0", left: "0", height: "100vh",
      border: "0", borderRight: "2px solid #1f6f2a", zIndex: "2147483647",
      boxShadow: "0 0 24px #000", background: "#000", colorScheme: "dark",
    });
    (document.documentElement || document.body).appendChild(overlay);
    applyLayout(); // start full-width; the backend narrows to a split once a live page is shown
    setVisible(true);
  }

  // Width policy. Resizing the iframe fires a `resize` inside overlay.js, which re-fits the xterm and
  // resizes the PTY — so the launcher renders full-width and link/reader views re-flow to the panel.
  function applyLayout() {
    if (!overlay) return;
    const vw = window.innerWidth || document.documentElement.clientWidth || 0;
    if (layoutMode === "split" && vw > 0) {
      // workspace-blg5.7: enforce (vw - 80) as a HARD ceiling even when MIN_PANEL_PX is larger (narrow
      // viewports) — order min(ceiling, max(floor, target)) so the live page always keeps >= 80px.
      const w = Math.min(vw - 80, Math.max(MIN_PANEL_PX, Math.round(vw * splitFrac)));
      overlay.style.width = w + "px";
      ensureSplitter();
      splitter.style.left = w + "px";
      splitter.style.display = visible ? "block" : "none";
    } else {
      overlay.style.width = "100vw";
      if (splitter) splitter.style.display = "none";
    }
    // The xterm inside the iframe MUST re-fit when the iframe width changes — otherwise the launcher/link
    // list/reader keep rendering at the old (wide) column count and get clipped by the narrower panel.
    // Changing an iframe's CSS width does NOT reliably fire a `resize` in the iframe's own window, so
    // notify overlay.js explicitly (workspace-blg5.7). overlay.js debounces + double-fits, so a stale
    // first read self-corrects on the settle pass.
    try { if (overlay.contentWindow) overlay.contentWindow.postMessage({ type: "wirecopy-refit" }, "*"); }
    catch { /* contentWindow not ready yet */ }
  }

  // Draggable divider (workspace-blg5.7). Only present in split mode. While dragging we disable the
  // iframe's pointer events so mousemove reaches THIS document (the cross-origin extension iframe would
  // otherwise swallow them) — same trick the legacy web shell's #splitter uses.
  function ensureSplitter() {
    if (splitter) return;
    splitter = document.createElement("div");
    splitter.id = SPLITTER_ID;
    Object.assign(splitter.style, {
      position: "fixed", top: "0", width: "6px", height: "100vh",
      cursor: "col-resize", zIndex: "2147483647", background: "#1f6f2a",
    });
    splitter.addEventListener("mousedown", startSplitDrag);
    (document.documentElement || document.body).appendChild(splitter);
  }

  function startSplitDrag(e) {
    e.preventDefault();
    if (overlay) overlay.style.pointerEvents = "none";
    const onMove = (ev) => {
      const vw = window.innerWidth || 0;
      if (vw > 0) { splitFrac = Math.min(0.9, Math.max(0.25, ev.clientX / vw)); applyLayout(); }
    };
    const onUp = () => {
      if (overlay) overlay.style.pointerEvents = "";
      document.removeEventListener("mousemove", onMove, true);
      document.removeEventListener("mouseup", onUp, true);
    };
    document.addEventListener("mousemove", onMove, true);
    document.addEventListener("mouseup", onUp, true);
  }

  function setLayout(msg) {
    layoutMode = msg && msg.mode === "split" ? "split" : "full";
    if (msg && typeof msg.ratio === "number" && msg.ratio > 0 && msg.ratio < 1) splitFrac = msg.ratio;
    applyLayout();
    return { ok: true };
  }

  function setVisible(v) {
    visible = v;
    if (overlay) overlay.style.display = v ? "block" : "none";
    if (splitter) splitter.style.display = v && layoutMode === "split" ? "block" : "none";
  }

  function toggleOverlay() {
    if (!overlay) { mountOverlay(); return; }
    setVisible(!visible);
  }

  // Keep the split width tracking the viewport as the window resizes.
  window.addEventListener("resize", () => { if (layoutMode === "split") applyLayout(); });

  // ---- DOM capture ----------------------------------------------------------------------------
  function captureDom() {
    return {
      url: location.href,
      html: document.documentElement ? document.documentElement.outerHTML : "",
      viewport: { w: window.innerWidth || 0, h: window.innerHeight || 0 },
    };
  }

  // ---- page drive (scroll / highlight / click) ------------------------------------------------
  function findAnchor({ selector, url, text }) {
    if (selector) {
      try { const el = document.querySelector(selector); if (el) return el; } catch { /* bad selector */ }
    }
    const anchors = Array.from(document.querySelectorAll("a[href]"));
    if (url) {
      const hit = anchors.find((a) => a.href === url || a.getAttribute("href") === url);
      if (hit) return hit;
    }
    if (text) {
      const needle = text.trim().toLowerCase();
      const hit = anchors.find((a) => (a.textContent || "").trim().toLowerCase().includes(needle));
      if (hit) return hit;
    }
    return null;
  }

  function clearSpotlight() {
    const node = document.getElementById(SPOT_ID);
    if (node) node.remove();
  }

  function spotlight(target) {
    const el = findAnchor(target);
    if (!el) return { ok: false, error: "no matching element" };
    el.scrollIntoView({ behavior: "smooth", block: "center" });
    clearSpotlight();
    const r = el.getBoundingClientRect();
    const box = document.createElement("div");
    box.id = SPOT_ID;
    Object.assign(box.style, {
      position: "fixed", left: r.left - 4 + "px", top: r.top - 4 + "px",
      width: r.width + 8 + "px", height: r.height + 8 + "px",
      border: "3px solid #ff5fa2", borderRadius: "4px", pointerEvents: "none",
      zIndex: "2147483646", boxShadow: "0 0 0 9999px rgba(0,0,0,0.12)",
      transition: "all 0.15s ease-out",
    });
    document.documentElement.appendChild(box);
    return { ok: true };
  }

  function clickTarget({ selector, url, text, x, y }) {
    let el = null;
    if (typeof x === "number" && typeof y === "number") {
      el = document.elementFromPoint(x, y);
    }
    if (!el) el = findAnchor({ selector, url, text });
    if (!el) return { ok: false, error: "no target" };
    el.click();
    return { ok: true };
  }

  function scrollTo({ selector, y }) {
    if (selector) {
      try {
        const el = document.querySelector(selector);
        if (el) { el.scrollIntoView({ behavior: "smooth", block: "center" }); return { ok: true }; }
      } catch { /* bad selector */ }
    }
    if (typeof y === "number") { window.scrollTo({ top: y, behavior: "smooth" }); return { ok: true }; }
    return { ok: false, error: "no scroll target" };
  }

  // ---- command handling (from the background service worker) ----------------------------------
  // driveLog records every page-drive command the backend issued (scrollTo / highlight / click), so the
  // headful verification gate can assert the full drive path is WIRED end-to-end (workspace-blg5.1),
  // not just that the verbs are defined. Capped so it can't grow unbounded on a long session.
  const driveLog = [];
  function note(kind) {
    driveLog.push(kind);
    if (driveLog.length > 50) driveLog.shift();
    // Mirror onto the shared DOM (documentElement attribute) — the content script's window globals live
    // in an ISOLATED world the verification gate's page.evaluate can't read, but the DOM is shared.
    try { document.documentElement.setAttribute("data-wirecopy-drives", driveLog.join(",")); }
    catch { /* document not ready */ }
  }

  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    switch (msg.type) {
      case "toggleOverlay": toggleOverlay(); sendResponse({ ok: true }); return true;
      case "requestDom": sendResponse(captureDom()); return true;
      case "scrollTo": note("scrollTo"); sendResponse(scrollTo(msg)); return true;
      case "highlight": note("highlight"); sendResponse(spotlight(msg)); return true;
      case "clearHighlight": note("clearHighlight"); clearSpotlight(); sendResponse({ ok: true }); return true;
      case "click": note("click"); sendResponse(clickTarget(msg)); return true;
      case "layout": sendResponse(setLayout(msg)); return true;
      default: return false;
    }
  });

  // ---- SPA route-change detection (History API + MutationObserver settle) ----------------------
  function reportNavigated() {
    try { chrome.runtime.sendMessage({ kind: "event", payload: { type: "navigated", url: location.href } }); }
    catch { /* SW asleep */ }
  }
  let lastHref = location.href;
  function onMaybeRouteChange() {
    if (location.href !== lastHref) { lastHref = location.href; reportNavigated(); }
  }
  ["pushState", "replaceState"].forEach((fn) => {
    const orig = history[fn];
    history[fn] = function () { const r = orig.apply(this, arguments); onMaybeRouteChange(); return r; };
  });
  window.addEventListener("popstate", onMaybeRouteChange);
  let settleTimer = null;
  const mo = new MutationObserver(() => {
    if (location.href !== lastHref) onMaybeRouteChange();
    clearTimeout(settleTimer);
    settleTimer = setTimeout(() => { /* DOM settled — backend can re-request on demand */ }, 400);
  });
  try { mo.observe(document.documentElement, { childList: true, subtree: true }); } catch { /* ignore */ }

  // ---- user-interaction signal (host browser is the source of truth) --------------------------
  let lastInteraction = 0;
  function noteInteraction(kind) {
    const now = Date.now();
    if (now - lastInteraction < 500) return; // throttle
    lastInteraction = now;
    try { chrome.runtime.sendMessage({ kind: "event", payload: { type: "userInteraction", kind, url: location.href } }); }
    catch { /* SW asleep */ }
  }
  document.addEventListener("click", () => noteInteraction("click"), true);
  document.addEventListener("keydown", () => noteInteraction("keydown"), true);

  // ---- boot ------------------------------------------------------------------------------------
  function init() {
    chrome.runtime.sendMessage({
      kind: "init", sessionId, url: location.href,
      viewport: { w: window.innerWidth || 0, h: window.innerHeight || 0 },
    });
    if (AUTO_SHOW) mountOverlay();
  }
  // Expose a tiny test surface for the headful verification gate (workspace-a2ml).
  window.__wireCopy = {
    sessionId: () => sessionId,
    captureDom,
    spotlight,
    overlayVisible: () => visible,
    toggleOverlay,
    driveLog: () => driveLog.slice(),
    lastDrive: () => (driveLog.length ? driveLog[driveLog.length - 1] : null),
  };
  init();
})();
