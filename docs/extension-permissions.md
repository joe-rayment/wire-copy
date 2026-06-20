# Wire Copy extension — permission posture

Bead: `workspace-lt13` (P3.2 permissions hardening). Epic: `workspace-blg5`.

This document explains every permission the Wire Copy Chrome extension requests, **why** it is
needed, and the **privacy implication**. It is grounded in the actual
[`extension/manifest.json`](../extension/manifest.json) — the entries below are quoted verbatim.

## Minimal-posture summary

- **No broad host access.** The extension does **not** request `<all_urls>` in `host_permissions`.
  `host_permissions` is scoped to **localhost / 127.0.0.1 only** — the local Wire Copy backend.
- **Page access is content-script + `activeTab`**, not a host grant. The content script is declared
  with an `<all_urls>` *match* (so the overlay can dock on any site you choose to use it on), but it
  only ever reads/drives the page you actively invoke it on, and it never has standing host
  permission to silently read arbitrary origins in the background.
- **`debugger` is optional, opt-in, and on-demand.** It lives in `optional_permissions`, not
  `permissions`, and is requested only when you turn on mobile emulation. Granting it triggers
  Chrome's persistent "… is being debugged" banner.
- **Page DOM goes to the LOCAL backend only.** The captured DOM is sent over a websocket to
  `ws://127.0.0.1:5099` (the Wire Copy backend on your own machine). It is **never** sent to a
  remote server.

## `permissions` (always granted at install)

```json
"permissions": ["scripting", "tabs", "activeTab", "storage"],
```

| Permission | Why it's needed | Privacy implication |
|---|---|---|
| `scripting` | Lets the background service worker inject/run the overlay and DOM-capture logic in the active tab (MV3's `chrome.scripting` API). This is what docks the TUI and reads the rendered page. | Code execution is limited to tabs where the overlay is engaged. No standing access to page content in tabs you haven't activated the overlay on. |
| `tabs` | The background worker drives top-level navigation (`chrome.tabs.update`) and waits for the tab to finish loading before re-capturing the DOM (`chrome.tabs.onUpdated`), and routes messages to the correct tab (`chrome.tabs.sendMessage`). | Grants access to tab metadata (e.g. URLs) for tabs the worker manages. Used to correlate a tab with its backend session and to navigate on command — not to enumerate or snoop on unrelated browsing. |
| `activeTab` | Grants temporary, user-gesture-scoped access to the **currently active tab** when you click the toolbar button or press the toggle shortcut. This is the narrow, intentional grant that pairs with the content script, instead of a broad host permission. | Access is scoped to the tab you explicitly act on, and only after a user gesture. It is the privacy-preserving alternative to requesting `<all_urls>` host access. |
| `storage` | Persists a single setting in `chrome.storage.local`: the `backend` override (defaults to `ws://127.0.0.1:5099`). See `background.js` → `getBackend()`. | Stores only the backend address. No browsing history, page content, or personal data is persisted. |

## `optional_permissions` (requested on demand only)

```json
"optional_permissions": ["debugger"],
```

| Permission | Why it's needed | Privacy implication |
|---|---|---|
| `debugger` | Required **only** for opt-in **mobile-layout emulation**. When you ask for a mobile view, the worker attaches the Chrome DevTools Protocol to the visible tab and issues `Emulation.setDeviceMetricsOverride` (414×896, mobile UA) — see `background.js` → `emulate()`. | This is a powerful permission, so it is **not** granted at install. It is requested at the moment you enable mobile emulation (`chrome.permissions.request`), and **Chrome shows a persistent "… is being debugged" banner** while it is attached. If you decline, the extension cleanly falls back to desktop-width extraction (`emulate()` returns `false`). |

### chrome.debugger decision (`workspace-8d8a`)

The default extraction path uses **no debugger**: Wire Copy renders the page at the tab's normal
**desktop** width and relies on its existing nav-vs-story classification to separate chrome from
content. **Mobile CDP emulation is strictly opt-in** — it is the only thing that pulls in the
`debugger` permission, it is requested on demand, and it visibly flags the tab as being debugged.
Choosing desktop-by-default keeps the standard experience free of the debugger permission and its
banner.

## `host_permissions` (scoped to the local backend only)

```json
"host_permissions": [
  "http://localhost/*",
  "http://127.0.0.1/*",
  "ws://localhost/*",
  "ws://127.0.0.1/*"
],
```

| Host pattern | Why it's needed | Privacy implication |
|---|---|---|
| `http://localhost/*` | Allows the service worker to reach the Wire Copy backend's HTTP endpoints over `localhost`. | Loopback only — cannot reach any remote site. |
| `http://127.0.0.1/*` | Same as above, addressed by IP (the default backend is `127.0.0.1:5099`). | Loopback only. |
| `ws://localhost/*` | Allows the worker to open the control-channel websocket (`/ws/ext`) and the terminal websocket (`/ws/terminal`) to the backend over `localhost`. | Loopback only. |
| `ws://127.0.0.1/*` | Same as above, by IP — this is what the default backend (`ws://127.0.0.1:5099`) matches. | Loopback only. The captured page DOM travels over **this** socket and nowhere else. |

**Critical point:** every `host_permission` is a loopback address. The extension has **no** host
permission for any external origin. It cannot, by host grant, read or contact any site on the
internet. The only network destination it talks to is the Wire Copy backend running on your own
machine.

## Data-flow guarantee

The content script reads the rendered page DOM and hands it to the background service worker, which
forwards it over the `/ws/ext` websocket to the **local** backend (`ws://127.0.0.1:5099` by
default, overridable only to another address you set yourself in `chrome.storage.local`). The DOM
is **never** transmitted to a remote/third-party server by the extension. The backend on your
machine is the sole recipient.

## Related

- [`docs/extension.md`](extension.md) — user + developer guide for the extension model.
- [`extension/README.md`](../extension/README.md) — in-tree extension overview.
- `WireCopy.Web/ExtBridge.cs` — source of truth for the `/ws/ext` control protocol.
