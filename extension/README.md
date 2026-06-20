# Wire Copy — Chrome extension (host-browser-as-renderer)

This MV3 extension makes **your own browser the renderer** for Wire Copy. The Wire Copy terminal TUI
docks as a top-layer overlay in your tab; the real website loads as that tab's own top-level page
(your profile, your cookies, your logins). There is **no second tab or window**, no server-side
headless/headful browser, no Xvfb, no Docker, and no bot detection — because the page is rendered by
your real browser.

See the epic (`workspace-blg5`) for the full architecture and rationale.

## Pieces

| File | Role |
|---|---|
| `manifest.json` | MV3 manifest (service worker, content script, overlay surface, optional `debugger`). |
| `background.js` | Owns the `ws://<backend>/ws/ext` control channel; relays the JSON protocol; drives navigation. |
| `content.js` | Docks the overlay, captures the rendered DOM, drives the page (scroll/highlight/click), SPA hooks. |
| `overlay.html` / `overlay.js` | The docked TUI surface: real xterm.js connected to `/ws/terminal`. |
| `vendor/` | Bundled `xterm.js`, `xterm.css`, `addon-fit.js`. |

## Control protocol

Documented as the single source of truth in `WireCopy.Web/ExtBridge.cs`
(`ExtSession`). In brief:

- **backend → ext**: `navigate` · `requestDom` · `scrollTo` · `highlight` · `clearHighlight` · `click` · `emulate`
- **ext → backend**: `ready` · `domSnapshot` · `navigated` · `actionResult` · `userInteraction`

## Run it (dev)

1. Start the Wire Copy backend in extension mode:

   ```bash
   ./run --web --extension      # starts WireCopy.Web on http://127.0.0.1:5099, API child uses WIRECOPY_BROWSER=extension
   ```

   (Or set `WIRECOPY_BROWSER=extension` and run `WireCopy.Web` directly.)

2. Load the extension unpacked:
   - Open `chrome://extensions`, enable **Developer mode**.
   - **Load unpacked** → select this `extension/` directory.

3. Open any site in a tab. The Wire Copy overlay docks on the left; type a URL or pick a story.
   - Toggle the overlay with the toolbar button or **Alt+Shift+W**.
   - Backend address override: set `backend` in the extension's `chrome.storage.local`
     (defaults to `ws://127.0.0.1:5099`).

## Mobile-layout extraction (`workspace-8d8a`)

Default extraction runs at the tab's **desktop** width and relies on Wire Copy's existing
nav-vs-story classification. Optional **mobile emulation** (CDP `Emulation.setDeviceMetricsOverride`
at 414px + a mobile UA) is available on demand via the `emulate` command; it requires the optional
`debugger` permission (which shows Chrome's "is being debugged" banner), so it is **opt-in**.

## Known Phase-1 limitation

A cross-document top-level navigation reloads the page and therefore re-injects the overlay; the TUI
reconnects fresh on the new page (terminal scrollback/history continuity across navigations is a
follow-up). Same-origin SPA route changes keep the overlay alive.

## Packaging

`scripts/package-extension.sh` produces `dist/wire-copy-extension.zip` for distribution / the Chrome
Web Store. See `workspace-b480`.
