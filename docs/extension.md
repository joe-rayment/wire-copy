# Wire Copy extension — user & developer guide

Bead: `workspace-14yw` (P3.3 docs). Epic: `workspace-blg5` (host-browser-as-renderer).

Cross-links: [permission posture](extension-permissions.md) · in-tree
[`extension/README.md`](../extension/README.md) · protocol source of truth
`WireCopy.Web/ExtBridge.cs`.

## What it is, and why

The Wire Copy Chrome extension makes **your own browser the renderer** for Wire Copy's web mode.
The Wire Copy terminal TUI docks as a top-layer overlay inside your tab, and the real website loads
as that tab's **own top-level page** — using your profile, your cookies, your logins.

Why this design (vs. the legacy server-side screencast):

- **One window.** No second tab, no second OS window — the TUI overlay and the page share the tab.
- **Real sessions.** The page is your real browser session, so you're already logged in where you
  normally are.
- **No bot blocks.** Because the page is rendered by your real browser (not a server-side headless
  browser), sites don't see an automation fingerprint and don't trip bot detection.
- **No Docker / Xvfb / Playwright Chromium for web mode.** There is no server-side headless/headful
  browser to provision — your browser does the rendering.

## How to run

### `./run --web --extension`

```bash
./run --web --extension     # or the shorthand: ./run --ext
```

This builds `WireCopy.API` + `WireCopy.Web`, exports `WIRECOPY_BROWSER=extension` (so
`BrowserDependencyInjection.AddTerminalBrowser` registers the extension bridge in place of the
server-side Playwright page loader) and `WIRECOPY_NO_OPEN=1` (the UI is the overlay in your own
browser, not a localhost tab), and starts `WireCopy.Web` on `http://127.0.0.1:5099`
(override with `WIRECOPY_WEB_URL`). No server-side content browser, Xvfb, or Docker is involved.
It then prints the load-unpacked instructions.

> The plain `./run --web` (legacy screencast shell) now **refuses to launch on a real display**
> (macOS, or Linux with `DISPLAY` set), because its headful content browser would pop a separate
> window — the single-window violation tracked by `workspace-3sq1`. It points you here, or at
> `docker compose up wirecopy-web`. Override with `WIRECOPY_FORCE_WEB_LEGACY=1` if you really want it.

### Run it by hand

The backend reads the `WIRECOPY_BROWSER` environment variable directly, so you can also launch
without `./run`:

```bash
export WIRECOPY_BROWSER=extension
export WIRECOPY_TERMINAL_APP="$PWD/src/WireCopy.API/bin/Release/net10.0/WireCopy.API"
dotnet run --project src/WireCopy.Web/WireCopy.Web.csproj -c Release
```

> `WIRECOPY_TERMINAL_APP` must point at the built `WireCopy.API` executable so the host can spawn the
> orchestrator child (which inherits the env var). `./run --web --extension` sets this for you.

Then load the extension unpacked (see next section).

### Load the extension unpacked

1. Open `chrome://extensions`.
2. Enable **Developer mode** (top-right toggle).
3. Click **Load unpacked** and select the [`extension/`](../extension) directory.
4. Open any site in a tab. The Wire Copy overlay docks on the left; type a URL or pick a story.
   - Toggle the overlay with the toolbar button or **Alt+Shift+W**.
   - Backend address override: set `backend` in the extension's `chrome.storage.local`
     (defaults to `ws://127.0.0.1:5099`).

For distribution, build a Chrome Web Store-ready zip with
[`scripts/package-extension.sh`](../scripts/package-extension.sh) (bead `workspace-b480`), which
produces `dist/wire-copy-extension.zip` with `manifest.json` at the archive root.

## Control protocol (summary)

The control channel is a JSON protocol over the `/ws/ext` websocket between the backend (the
`WireCopy.API` child running in extension mode) and the extension's background service worker. **The
single source of truth is `WireCopy.Web/ExtBridge.cs`** (the `ExtSession` doc-comment); the
extension side mirrors it in `extension/background.js` and `extension/content.js`. In brief:

- **backend → ext** (commands, each carries a numeric `id` for correlation):
  `navigate` · `requestDom` · `scrollTo` · `highlight` · `clearHighlight` · `click` · `emulate`
- **ext → backend**:
  `ready` · `domSnapshot` · `navigated` · `actionResult` · `userInteraction`

The background worker holds one `ws://<backend>/ws/ext?session=<id>` socket per tab with the overlay
open, delegates page-touching work (DOM capture, scroll, highlight, click) to the content script,
and drives cross-document navigation itself via `chrome.tabs.update`. The TUI surface in
`overlay.html` / `overlay.js` is a real xterm.js connected to `/ws/terminal`.

For the `emulate` command and the optional `debugger` permission it uses, see
[extension-permissions.md](extension-permissions.md).

## Known Phase-1 limitation: navigation continuity

A cross-document, top-level navigation reloads the page and therefore re-injects the overlay; the
TUI reconnects **fresh** on the new page. Terminal scrollback / history continuity across such
navigations is a follow-up, not yet implemented. **Same-origin SPA route changes keep the overlay
alive** (the content script isn't torn down), so continuity is preserved within a single document.
(See `extension/README.md` → "Known Phase-1 limitation".)

## Deprecation: legacy localhost:5099 screencast web shell

> Nothing is being removed by this document — this section records the **plan**.

The original web mode is a **server-side screencast**: the `WireCopy.API` child drives a
Playwright/Patchright Chromium, the backend relays CDP `Page.startScreencast` JPEG frames to the
browser tab over `/ws/webpane`, and forwards input back. The tab shows an `<img id="screencast">`
fed by that socket.

Components that make up the legacy screencast path:

- `src/WireCopy.Web/WebPaneRelay.cs` — relays the child's CDP screencast frames to the tab and
  forwards input back (`/ws/webpane`).
- `src/WireCopy.Infrastructure/Browser/WebPaneHostBridge.cs` — the child-side bridge for the screencast pane.
- `src/WireCopy.Web/wwwroot/index.html` — the screencast web shell UI (the `#screencast` `<img>`,
  `#web-pane`, and the `/ws/webpane` client).
- The `ensure_deps` Playwright-Chromium bootstrap in `./run` (~270 MB download) that exists to feed
  the server-side browser.

**Deprecation plan.** Once the extension model reaches parity with the screencast shell, the legacy
path is slated for removal:

- Remove the `/ws/webpane` endpoint and `WebPaneRelay.cs`.
- Remove `WebPaneHostBridge.cs` and the server-side Playwright/Patchright web-pane plumbing.
- Replace/retire the screencast UI in `wwwroot/index.html` (the `#screencast` image and webpane
  client) in favor of the extension overlay.
- Drop the web-mode Chromium bootstrap from `./run`'s `ensure_deps`.

Until parity is declared, both modes coexist: extension mode is gated entirely on
`WIRECOPY_BROWSER=extension`, so the native terminal app and the legacy screencast web mode are
unaffected when it is off. **Do not remove any of the above before the parity gate** — this section
is documentation of intent only.

## Related docs

- [extension-permissions.md](extension-permissions.md) — minimal permission posture and the
  `chrome.debugger` decision (`workspace-8d8a`).
- [`extension/README.md`](../extension/README.md) — in-tree extension overview.
- `WireCopy.Web/ExtBridge.cs` — control-protocol source of truth.
