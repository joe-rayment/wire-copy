# Wire Copy browser extension

A Manifest V3 extension for Chrome, Vivaldi, Brave, Edge and Chromium. Its only job: save the
article you are reading to your Wire Copy Reading List. Click the button and the page's HTML
goes to the desktop app over Chrome native messaging (host `com.wirecopy.reader`) — local stdio
pipes, no network, so it works offline and even when the app is closed.

- Permissions: `activeTab`, `scripting`, `nativeMessaging` only. No host permissions, so the
  install prompt carries no "read and change all your data" warning.
- The `key` in `manifest.json` pins the extension ID to `cmidfakmpcnmaohapmcoigenlcjlahim`
  on every machine; the host manifest's `allowed_origins` relies on it.
- Wire protocol (hello / save, framing, replies): bead `workspace-m7aml` (`bd show workspace-m7aml`).
- Files: `background.js` (service worker: does the save), `popup.*` (saves on open, shows the
  outcome), `welcome.*` (onboarding tab: live connection check, pin the button), `tokens.css`
  (app palette), `icons/` (made by `tools/make-icons.py`, not used at runtime).
- Install: the app exports this folder to `<data dir>/browser-extension/WireCopy-Extension/`
  and registers the native host for each installed browser; in Settings -> Browser extension the
  user turns on Developer mode and loads that unpacked folder. A Web Store listing comes later.
- Try it by hand: `chrome://extensions` -> Developer mode -> Load unpacked -> this folder.
