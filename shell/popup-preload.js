// SSO popup-pane opener shim (workspace-y0bi). The popup pane is NOT a script-opened
// window from Chromium's perspective — the shell denies the window.open and hosts the
// URL in its own pane — so window.opener is null and window.close() is ignored there.
// SSO flows need both: bridge them to the shell over IPC, which relays postMessage into
// the opener page (real origin attached) and tears the pane down on close.
// Runs with contextIsolation:false so the shim lands in the PAGE's world; the popup is
// app-managed chrome, node integration stays off.
const { ipcRenderer } = require('electron')
window.opener = {
  closed: false,
  postMessage: data => ipcRenderer.send('wcpopup:post', JSON.stringify(data ?? null))
}
window.close = () => ipcRenderer.send('wcpopup:close')
