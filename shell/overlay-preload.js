// Overlay pane bridge (workspace-tj1z.2): the transition overlay is a dumb compositor
// screen — main sends it snapshots + animation params, it acks when armed and reports
// animation completion. Same secure contextBridge shape as preload.js.
const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('wcov', {
  onBegin: cb => ipcRenderer.on('wcov:begin', (_e, p) => cb(p)),
  onGo: cb => ipcRenderer.on('wcov:go', (_e, p) => cb(p)),
  onCrossfade: cb => ipcRenderer.on('wcov:crossfade', (_e, p) => cb(p)),
  onReverse: cb => ipcRenderer.on('wcov:reverse', (_e, p) => cb(p)),
  onAbort: cb => ipcRenderer.on('wcov:abort', (_e, p) => cb(p)),
  armed: gen => ipcRenderer.send('wcov:armed', gen),
  done: (gen, phase) => ipcRenderer.send('wcov:done', { gen, phase })
})
