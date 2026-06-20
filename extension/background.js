// Wire Copy — MV3 background service worker (workspace-cptb / workspace-kryh).
//
// Owns the backend <-> extension control channel. For each tab that has the overlay open, it holds a
// websocket to ws://<backend>/ws/ext?session=<id> and relays the documented JSON protocol:
//   backend -> ext: navigate / requestDom / scrollTo / highlight / clearHighlight / click / emulate
//   ext -> backend: ready / domSnapshot / actionResult / navigated / userInteraction
// Page-touching work (DOM capture, scroll, highlight, click) is delegated to the content script in the
// target tab; cross-document navigation is driven here (chrome.tabs.update) and chained to a DOM
// capture once the tab finishes loading. The service worker can fetch/ws localhost from any page
// without mixed-content/CORS limits, which a page-context connection could not.

const DEFAULT_BACKEND = "ws://127.0.0.1:5099";

// tabId -> { sessionId, ws, backend, connected, lastUrl }
const sessions = new Map();

async function getBackend() {
  try {
    const { backend } = await chrome.storage.local.get("backend");
    return backend || DEFAULT_BACKEND;
  } catch {
    return DEFAULT_BACKEND;
  }
}

function wsBase(backend) {
  // Accept http(s):// or ws(s):// in settings; normalise to a ws origin.
  return backend.replace(/^http/i, "ws").replace(/\/+$/, "");
}

// ---- /ws/ext lifecycle -------------------------------------------------------------------------

async function ensureExtSocket(tabId, sessionId, firstReady) {
  let state = sessions.get(tabId);
  if (state && state.sessionId === sessionId && state.ws &&
      (state.ws.readyState === WebSocket.OPEN || state.ws.readyState === WebSocket.CONNECTING)) {
    // Re-announce readiness for the (possibly new) page within the same session.
    if (firstReady) sendToBackend(tabId, firstReady);
    return;
  }

  const backend = await getBackend();
  closeSocket(tabId);

  const url = `${wsBase(backend)}/ws/ext?session=${encodeURIComponent(sessionId)}`;
  const ws = new WebSocket(url);
  state = { sessionId, ws, backend, connected: false, lastUrl: firstReady ? firstReady.url : null };
  sessions.set(tabId, state);

  ws.onopen = () => {
    state.connected = true;
    if (firstReady) sendToBackend(tabId, firstReady);
  };
  ws.onmessage = (ev) => handleBackendCommand(tabId, ev.data);
  ws.onclose = () => {
    state.connected = false;
    // Reconnect only while the tab still has an overlay open (sessions still holds it).
    const cur = sessions.get(tabId);
    if (cur && cur.sessionId === sessionId) {
      setTimeout(() => {
        const again = sessions.get(tabId);
        if (again && again.sessionId === sessionId) ensureExtSocket(tabId, sessionId, makeReady(again.lastUrl));
      }, 1000);
    }
  };
  ws.onerror = () => { /* surfaced via onclose */ };
}

function closeSocket(tabId) {
  const state = sessions.get(tabId);
  if (state && state.ws) {
    // Null ALL handlers before close (workspace-blg5.6): on a top-level navigation the new content
    // script reinit closes+reopens this socket; a message still queued on the OLD socket must not
    // dispatch into the (now stale) handler for this tab.
    try { state.ws.onclose = null; state.ws.onmessage = null; state.ws.onerror = null; state.ws.close(); }
    catch { /* ignore */ }
  }
}

function sendToBackend(tabId, obj) {
  const state = sessions.get(tabId);
  if (state && state.ws && state.ws.readyState === WebSocket.OPEN) {
    state.ws.send(JSON.stringify(obj));
  }
}

function makeReady(url) {
  return { type: "ready", url: url || "", viewport: { w: 0, h: 0 } };
}

// ---- backend -> ext command dispatch -----------------------------------------------------------

async function handleBackendCommand(tabId, raw) {
  let msg;
  try { msg = JSON.parse(raw); } catch { return; }
  const id = msg.id;

  try {
    if (msg.type === "navigate") {
      const snap = await navigateAndCapture(tabId, msg.url);
      sendToBackend(tabId, { type: "domSnapshot", id, ...snap });
      return;
    }

    if (msg.type === "requestDom") {
      const snap = await sendToContent(tabId, { type: "requestDom" });
      sendToBackend(tabId, { type: "domSnapshot", id, ...snap });
      return;
    }

    if (msg.type === "emulate") {
      const ok = await emulate(tabId, !!msg.mobile, msg.width);
      sendToBackend(tabId, { type: "actionResult", id, ok });
      return;
    }

    // scrollTo / highlight / clearHighlight / click — pure content-script actions.
    const res = await sendToContent(tabId, msg);
    sendToBackend(tabId, { type: "actionResult", id, ok: res && res.ok !== false, error: res && res.error });
  } catch (e) {
    sendToBackend(tabId, { type: "actionResult", id, ok: false, error: String(e && e.message || e) });
  }
}

// Drive a top-level navigation, then capture the rendered DOM once the tab settles. Same-origin SPA
// navigations keep the content script alive; cross-document navigations reload it, so we wait for the
// tab to reach "complete" and re-request the DOM from the (possibly fresh) content script.
function navigateAndCapture(tabId, url) {
  // Defensive (workspace-blg5.6): chrome.tabs.update REJECTS a scheme-less URL (e.g. "www.x.com") with
  // an "Invalid url" lastError and silently leaves the tab put. Normalize here so a bookmark or caller
  // that didn't prepend a scheme still navigates, instead of failing invisibly.
  if (url && !/^[a-z][a-z0-9+.-]*:/i.test(url)) url = "https://" + url;
  return new Promise((resolve, reject) => {
    let settled = false;
    const done = (fn, arg) => { if (!settled) { settled = true; cleanup(); fn(arg); } };

    const onUpdated = (updatedTabId, info) => {
      if (updatedTabId === tabId && info.status === "complete") {
        // Give SPA hydration a beat, then capture.
        setTimeout(async () => {
          try { done(resolve, await sendToContent(tabId, { type: "requestDom" })); }
          catch (e) { done(reject, e); }
        }, 600);
      }
    };
    const cleanup = () => {
      chrome.tabs.onUpdated.removeListener(onUpdated);
      clearTimeout(timer);
    };
    const timer = setTimeout(async () => {
      try { done(resolve, await sendToContent(tabId, { type: "requestDom" })); }
      catch (e) { done(reject, e); }
    }, 30000);

    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.tabs.update(tabId, { url }, () => {
      if (chrome.runtime.lastError) {
        // Don't swallow it (workspace-blg5.6): log AND surface so the backend shows a real error in the
        // TUI instead of hanging for the 30s timeout.
        console.warn("Wire Copy: chrome.tabs.update failed for", url, "-", chrome.runtime.lastError.message);
        done(reject, new Error(chrome.runtime.lastError.message));
      }
    });
  });
}

function sendToContent(tabId, message) {
  return new Promise((resolve, reject) => {
    chrome.tabs.sendMessage(tabId, message, (response) => {
      if (chrome.runtime.lastError) {
        // Log (workspace-blg5.6): a silent reject here (content script not injected / page mid-load /
        // script crashed) otherwise leaves the backend with an opaque failure and no diagnostics.
        console.warn("Wire Copy: sendMessage to tab", tabId, message && message.type, "failed -", chrome.runtime.lastError.message);
        reject(new Error(chrome.runtime.lastError.message));
        return;
      }
      resolve(response || {});
    });
  });
}

// ---- mobile emulation (workspace-8d8a, opt-in) -------------------------------------------------
// CDP device-metrics override on the VISIBLE tab. Off by default; requires the optional "debugger"
// permission (which shows Chrome's "is being debugged" banner). Returns false if the user declines
// the permission so the backend cleanly falls back to desktop-width extraction + nav-filtering.

async function emulate(tabId, mobile, width) {
  const has = await chrome.permissions.contains({ permissions: ["debugger"] });
  if (!has) {
    const granted = await chrome.permissions.request({ permissions: ["debugger"] }).catch(() => false);
    if (!granted) return false;
  }
  const target = { tabId };
  try {
    await chrome.debugger.attach(target, "1.3").catch((e) => {
      if (!String(e).includes("already attached")) throw e;
    });
    if (mobile) {
      await chrome.debugger.sendCommand(target, "Emulation.setDeviceMetricsOverride", {
        width: width || 414, height: 896, deviceScaleFactor: 2, mobile: true,
      });
      await chrome.debugger.sendCommand(target, "Emulation.setUserAgentOverride", {
        userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1",
      });
    } else {
      await chrome.debugger.sendCommand(target, "Emulation.clearDeviceMetricsOverride");
    }
    return true;
  } catch {
    return false;
  }
}

// ---- content-script registration + events ------------------------------------------------------

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  const tabId = sender.tab && sender.tab.id;
  if (typeof tabId !== "number") return;

  if (msg.kind === "init") {
    ensureExtSocket(tabId, msg.sessionId, { type: "ready", url: msg.url, viewport: msg.viewport || { w: 0, h: 0 } });
    sendResponse({ ok: true });
    return;
  }
  if (msg.kind === "event") {
    // userInteraction / navigated bubbled up from the page.
    sendToBackend(tabId, msg.payload);
    return;
  }
  if (msg.kind === "close") {
    closeSocket(tabId);
    sessions.delete(tabId);
    return;
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  closeSocket(tabId);
  sessions.delete(tabId);
});

// Toolbar click / keyboard command toggles the overlay in the active tab.
chrome.action.onClicked.addListener((tab) => {
  if (tab.id != null) chrome.tabs.sendMessage(tab.id, { type: "toggleOverlay" }).catch(() => {});
});
chrome.commands.onCommand.addListener((command, tab) => {
  if (command === "toggle-overlay" && tab && tab.id != null) {
    chrome.tabs.sendMessage(tab.id, { type: "toggleOverlay" }).catch(() => {});
  }
});
