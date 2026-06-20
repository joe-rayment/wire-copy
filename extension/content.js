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

  const PANEL_W = 480;
  const OVERLAY_ID = "wire-copy-overlay";
  const SPOT_ID = "__wire-copy-spotlight";
  const AUTO_SHOW = true; // dock on load; toggle with the toolbar button / Alt+Shift+W.

  let sessionId = (crypto.randomUUID ? crypto.randomUUID() : String(Math.random())).replace(/-/g, "");
  let overlay = null;
  let visible = false;

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
      position: "fixed", top: "0", left: "0", width: PANEL_W + "px", height: "100vh",
      border: "0", borderRight: "2px solid #1f6f2a", zIndex: "2147483647",
      boxShadow: "0 0 24px #000", background: "#000", colorScheme: "dark",
    });
    (document.documentElement || document.body).appendChild(overlay);
    setVisible(true);
  }

  function setVisible(v) {
    visible = v;
    if (overlay) overlay.style.display = v ? "block" : "none";
  }

  function toggleOverlay() {
    if (!overlay) { mountOverlay(); return; }
    setVisible(!visible);
  }

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
  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    switch (msg.type) {
      case "toggleOverlay": toggleOverlay(); sendResponse({ ok: true }); return true;
      case "requestDom": sendResponse(captureDom()); return true;
      case "scrollTo": sendResponse(scrollTo(msg)); return true;
      case "highlight": sendResponse(spotlight(msg)); return true;
      case "clearHighlight": clearSpotlight(); sendResponse({ ok: true }); return true;
      case "click": sendResponse(clickTarget(msg)); return true;
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
  };
  init();
})();
