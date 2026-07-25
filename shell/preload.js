const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('wc', {
  onPtyData: cb => ipcRenderer.on('pty:data', (_e, d) => cb(d)),
  onMode: cb => ipcRenderer.on('wc:mode', (_e, m) => cb(m)),
  onFont: cb => ipcRenderer.on('wc:font', (_e, px) => cb(px)),
  ready: dims => ipcRenderer.send('term:ready', dims),
  input: d => ipcRenderer.send('pty:input', d),
  resize: dims => ipcRenderer.send('term:resize', dims),
  // dev/gate hooks
  reveal: on => ipcRenderer.send('wcdev:reveal', on),
  setMode: m => ipcRenderer.send('wcdev:mode', m),
  state: () => ipcRenderer.invoke('wcdev:state'),
  nav: url => ipcRenderer.invoke('wcdev:nav', url),
  // Gate-only: evaluate inside THE lens view object. Matching a CDP target by URL
  // is ambiguous — hidden prefetch pages share the lens's origin — so a gate that
  // wants the VISIBLE page must address the view, not a URL.
  lensEval: expr => ipcRenderer.invoke('wcdev:lensEval', expr),
  probeCertDefaultSession: url => ipcRenderer.invoke('wcdev:probeCertDefaultSession', url)
})
