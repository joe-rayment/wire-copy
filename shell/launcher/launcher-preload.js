// Launcher pane bridge (workspace-wak1s): the launcher is a display-only screen — main
// pushes it the TUI's launcher state and live type-scale changes; it renders, it never
// talks back (no ack, no focus). Same secure contextBridge shape as overlay-preload.js.
const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('wclau', {
  onState: cb => ipcRenderer.on('wc:launcher', (_e, msg) => cb(msg)),
  onFont: cb => ipcRenderer.on('wc:font', (_e, px) => cb(px))
})
