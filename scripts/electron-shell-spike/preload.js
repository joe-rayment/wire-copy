const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('wc', {
  onPtyData: cb => ipcRenderer.on('pty:data', (_e, d) => cb(d)),
  onPtyExit: cb => ipcRenderer.on('pty:exit', (_e, c) => cb(c)),
  ready: dims => ipcRenderer.send('term:ready', dims),
  input: d => ipcRenderer.send('pty:input', d),
  resize: dims => ipcRenderer.send('term:resize', dims),
  reveal: on => ipcRenderer.send('spike:reveal', on),
  setMode: m => ipcRenderer.send('spike:mode', m),
  state: () => ipcRenderer.invoke('spike:state'),
  nav: url => ipcRenderer.invoke('spike:nav', url)
})
