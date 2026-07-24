// Link-tree pane bridge (workspace-vingi): the story list is a display-only screen —
// main pushes it the TUI's link-tree state and live type-scale changes; it renders, it
// never talks back (no ack, no focus). Same secure contextBridge shape as
// launcher-preload.js / overlay-preload.js.
const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('wclt', {
  onState: cb => ipcRenderer.on('wc:linktree', (_e, msg) => cb(msg)),
  onFont: cb => ipcRenderer.on('wc:font', (_e, px) => cb(px))
})
