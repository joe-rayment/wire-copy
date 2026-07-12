const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('wc', {
  onPtyData: cb => ipcRenderer.on('pty:data', (_e, d) => cb(d)),
  onMode: cb => ipcRenderer.on('wc:mode', (_e, m) => cb(m)),
  ready: dims => ipcRenderer.send('term:ready', dims),
  input: d => ipcRenderer.send('pty:input', d),
  resize: dims => ipcRenderer.send('term:resize', dims),
  // dev/gate hooks
  reveal: on => ipcRenderer.send('wcdev:reveal', on),
  setMode: m => ipcRenderer.send('wcdev:mode', m),
  state: () => ipcRenderer.invoke('wcdev:state'),
  nav: url => ipcRenderer.invoke('wcdev:nav', url)
})
