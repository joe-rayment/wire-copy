// WireCopy single-window shell — SPIKE.
// One real headful Chromium window (Electron) hosting:
//   - terminal pane (xterm.js) running the UNMODIFIED WireCopy TUI via node-pty  → PRIMARY
//   - page pane (WebContentsView) rendering live sites with the real engine      → SECONDARY
// Focus doctrine: reader-primary. In 'reader' mode the page pane can NEVER hold focus
// or eat keys — keys that land on it are FORWARDED to the terminal (zero loss), and an
// enforcement loop bounces focus back. 'browser' mode is an explicit, deliberate toggle
// (login/interaction); Esc returns to reader. NEVER headless — this IS a headful browser.
const { app, BaseWindow, WebContentsView, ipcMain, Menu } = require('electron')
const path = require('path')
const pty = require('node-pty')

const CDP_PORT = process.env.SPIKE_CDP_PORT || '9333'
app.commandLine.appendSwitch('remote-debugging-port', CDP_PORT)
app.commandLine.appendSwitch('disable-dev-shm-usage') // container /dev/shm is tiny; heavy pages crash renderers without this

// Present as plain Chrome (drop the Electron token) — same engine, honest version.
const CHROME_UA = `Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/${process.versions.chrome} Safari/537.36`
app.userAgentFallback = CHROME_UA

let win, termView, pageView, ptyProc
let mode = 'reader'   // 'reader' | 'browser'
let revealed = false
let lastPtyDims = { cols: 0, rows: 0 }
const PAGE_FRACTION = 0.42 // page pane is a feature, not the focus — never dominant

function layout () {
  if (!win) return
  const [w, h] = win.getContentSize()
  if (revealed) {
    const pageW = Math.round(w * PAGE_FRACTION)
    termView.setBounds({ x: 0, y: 0, width: w - pageW, height: h })
    pageView.setVisible(true)
    pageView.setBounds({ x: w - pageW, y: 0, width: pageW, height: h })
  } else {
    termView.setBounds({ x: 0, y: 0, width: w, height: h })
    pageView.setVisible(false)
  }
}

function focusTerm () {
  if (win && !win.isDestroyed() && !termView.webContents.isDestroyed()) termView.webContents.focus()
}

function setMode (m) {
  mode = m
  if (m === 'reader') focusTerm()
  else pageView.webContents.focus()
}

// Reroute a key that landed on the page (reader mode) into the terminal — the user's
// keystroke is NEVER lost, no matter who won the focus race at that instant.
const KEYCODE_MAP = { ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', ' ': 'Space' }
function forwardToTerm (input) {
  if (termView.webContents.isDestroyed()) return
  const keyCode = KEYCODE_MAP[input.key] || input.key
  const modifiers = []
  if (input.shift) modifiers.push('shift')
  if (input.control) modifiers.push('control')
  if (input.alt) modifiers.push('alt')
  if (input.meta) modifiers.push('meta')
  if (input.type === 'keyUp') {
    termView.webContents.sendInputEvent({ type: 'keyUp', keyCode, modifiers })
    return
  }
  termView.webContents.sendInputEvent({ type: 'keyDown', keyCode, modifiers })
  if (input.key.length === 1) termView.webContents.sendInputEvent({ type: 'char', keyCode: input.key, modifiers })
}

app.whenReady().then(() => {
  Menu.setApplicationMenu(null) // our window, our chrome — no default menu bar
  win = new BaseWindow({ width: 1520, height: 940, title: 'WC-SPIKE', backgroundColor: '#0e0e14' })

  termView = new WebContentsView({
    webPreferences: { preload: path.join(__dirname, 'preload.js') }
  })
  pageView = new WebContentsView({
    webPreferences: { partition: 'persist:wcspike' }
  })
  pageView.webContents.session.setUserAgent(CHROME_UA)

  win.contentView.addChildView(pageView)
  win.contentView.addChildView(termView)
  layout()
  win.on('resize', layout)

  // ---- FOCUS POLICY (the historical killer; enforced in-process, three layers) ----
  // 1) bounce: any page-side focus gain in reader mode returns focus to the terminal
  pageView.webContents.on('focus', () => { if (mode === 'reader') focusTerm() })
  const refocus = () => { if (mode === 'reader') focusTerm() }
  pageView.webContents.on('did-finish-load', refocus)       // electron#42578: views steal focus after load
  pageView.webContents.on('did-navigate', refocus)
  pageView.webContents.on('did-frame-navigate', refocus)
  win.on('focus', refocus)
  try { termView.webContents.on('blur', () => { if (mode === 'reader') focusTerm() }) } catch {}
  // 2) enforcement loop: renderer-internal focus() calls emit no event we can hook — poll cheaply
  setInterval(() => {
    if (mode === 'reader' && win && !win.isDestroyed() && win.isFocused() && !termView.webContents.isFocused()) focusTerm()
  }, 150)
  // 3) forwarding: keys that still land on the page get rerouted to the terminal, not dropped
  pageView.webContents.on('before-input-event', (e, input) => {
    if (mode === 'reader') { e.preventDefault(); forwardToTerm(input); return }
    if (input.type === 'keyDown' && input.key === 'Escape') { e.preventDefault(); setMode('reader') }
  })
  // Pages calling window.open stay inside the pane (production: policy-routed).
  pageView.webContents.setWindowOpenHandler(({ url }) => {
    pageView.webContents.loadURL(url).catch(() => {})
    return { action: 'deny' }
  })

  // ---- The real WireCopy TUI, byte-for-byte, under a PTY ----
  const termHtml = process.env.SPIKE_TERM_HTML || 'term.html'
  if (process.env.SPIKE_TERM_HTTP === '1') {
    // ESM + WASM need a real origin (file:// blocks module CORS). Loopback-only static server.
    const http = require('http')
    const fs = require('fs')
    const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.wasm': 'application/wasm' }
    const srv = http.createServer((req, res) => {
      const rel = decodeURIComponent(new URL(req.url, 'http://x/').pathname)
      const fp = path.join(__dirname, rel)
      if (!fp.startsWith(__dirname) || !fs.existsSync(fp) || fs.statSync(fp).isDirectory()) { res.writeHead(404); res.end(); return }
      res.writeHead(200, { 'content-type': MIME[path.extname(fp)] || 'application/octet-stream' })
      fs.createReadStream(fp).pipe(res)
    })
    srv.listen(0, '127.0.0.1', () => {
      termView.webContents.loadURL(`http://127.0.0.1:${srv.address().port}/${termHtml}`)
    })
  } else {
    termView.webContents.loadFile(termHtml)
  }
  ipcMain.on('term:ready', (_e, { cols, rows }) => {
    if (ptyProc) { ptyProc.resize(cols, rows); lastPtyDims = { cols, rows }; return }
    const file = process.env.SPIKE_PTY_FILE || '/workspace/dotnet'
    const args = process.env.SPIKE_PTY_ARGS
      ? JSON.parse(process.env.SPIKE_PTY_ARGS)
      : ['exec', '/workspace/src/WireCopy.API/bin/Debug/net10.0/WireCopy.API.dll']
    ptyProc = pty.spawn(file, args, {
      name: 'xterm-256color',
      cols, rows,
      cwd: process.env.SPIKE_PTY_CWD || '/workspace',
      env: { ...process.env, DOTNET_ROOT: '/workspace/.dotnet', TERM: 'xterm-256color', COLORTERM: 'truecolor' }
    })
    lastPtyDims = { cols, rows }
    ptyProc.onData(d => { if (!termView.webContents.isDestroyed()) termView.webContents.send('pty:data', d) })
    ptyProc.onExit(({ exitCode }) => { if (!termView.webContents.isDestroyed()) termView.webContents.send('pty:exit', exitCode) })
  })
  ipcMain.on('pty:input', (_e, d) => { if (ptyProc) ptyProc.write(d) })
  ipcMain.on('term:resize', (_e, { cols, rows }) => {
    if (ptyProc) { ptyProc.resize(cols, rows); lastPtyDims = { cols, rows } }
  })

  // ---- Spike control surface (gate drives via CDP → term view → IPC) ----
  ipcMain.on('spike:reveal', (_e, on) => { revealed = !!on; layout() })
  ipcMain.on('spike:mode', (_e, m) => setMode(m === 'browser' ? 'browser' : 'reader'))
  ipcMain.handle('spike:state', () => ({
    mode,
    revealed,
    ptyDims: lastPtyDims,
    contentSize: win.getContentSize(),
    termBounds: termView.getBounds(),
    pageBounds: pageView.getBounds(),
    termFocused: termView.webContents.isFocused()
  }))
  ipcMain.handle('spike:nav', async (_e, url) => {
    try { await pageView.webContents.loadURL(url) } catch (err) { return String(err) }
    return 'ok'
  })

  focusTerm()
})

app.on('window-all-closed', () => {
  try { if (ptyProc) ptyProc.kill() } catch {}
  app.quit()
})
