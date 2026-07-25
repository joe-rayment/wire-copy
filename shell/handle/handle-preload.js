// Drawer-handle pane bridge (workspace-2dmuu). Unlike the launcher / link-tree panes
// this one TALKS BACK — but only one message, `toggle`, raised by a click on the bar.
// It carries no state of its own and never decides anything: main turns the click into
// the byte '|' on the TUI's stdin, so the TUI stays the single authority on whether the
// drawer is open (a shell-local flip would desync .NET's dock flags). Same secure
// contextBridge shape as launcher-preload.js / linktree-preload.js / overlay-preload.js.
const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('wch', {
  toggle: () => ipcRenderer.send('wch:toggle'),
  onState: cb => ipcRenderer.on('wch:state', (_e, msg) => cb(msg))
})
