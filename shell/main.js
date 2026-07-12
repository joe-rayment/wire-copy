// WireCopy Desktop — single-window shell.
// One real headful Chromium window (Electron) hosting:
//   - the UNMODIFIED WireCopy TUI (node-pty → xterm pane)  → PRIMARY, boots full-window
//   - live pages as WebContentsViews (real engine)          → SECONDARY, revealed on demand
// NEVER headless: this process IS a headful browser; on displayless Linux the launcher
// wraps it in Xvfb (see ../run), it does not degrade.
//
// Focus doctrine (three layers — see scripts/electron-shell-spike/README.md for the
// empirical history; electron#42578 focus-steal-after-load is real):
//   1. bounce: page-side focus gain in reader mode → refocus terminal
//   2. enforcement loop: renderer-internal focus() emits no event — cheap poll restores
//   3. forwarding: keys that still land on a page are re-sent to the terminal, never lost
// 'browser' mode is an explicit user gesture (click into the pane); Esc returns to reader.
const { app, BaseWindow, WebContentsView, ipcMain, Menu, session } = require('electron')
const path = require('path')
const os = require('os')
const fs = require('fs')
const channel = require('./channel')

// Shell deps are PER-PLATFORM (this repo dir is shared across machines; Electron and
// node-pty binaries are platform-specific). Prefer deps/<platform>-<arch>/node_modules;
// fall back to a plain ./node_modules for legacy/dev setups.
const DEPS = (() => {
  const scoped = path.join(__dirname, 'deps', `${process.platform}-${process.arch}`, 'node_modules')
  return fs.existsSync(scoped) ? scoped : path.join(__dirname, 'node_modules')
})()
const pty = require(path.join(DEPS, 'node-pty'))

const ROOT = path.resolve(__dirname, '..')
const CDP_PORT = process.env.WIRECOPY_SHELL_CDP_PORT || '9223'
const PAGE_FRACTION = 0.42 // page pane is a feature, not the focus — never dominant

app.commandLine.appendSwitch('remote-debugging-port', CDP_PORT)
// NOTE (Linux): sandbox flags cannot be set here — Electron initializes the zygote/sandbox
// BEFORE this script runs. Containers without a setuid sandbox must pass --no-sandbox
// --disable-dev-shm-usage on the CLI (../run does this when WIRECOPY_SHELL_NO_SANDBOX=1).
// Packaged and normal desktop runs keep the Chromium sandbox ON.

// Present as plain Chrome: same engine, honest version, no Electron token — required
// for Google sign-in and consistent with how the app's sites see a normal browser.
const UA_PLATFORM = process.platform === 'darwin'
  ? 'Macintosh; Intel Mac OS X 10_15_7'
  : process.platform === 'win32' ? 'Windows NT 10.0; Win64; x64' : 'X11; Linux x86_64'
const CHROME_UA = `Mozilla/5.0 (${UA_PLATFORM}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/${process.versions.chrome} Safari/537.36`
app.userAgentFallback = CHROME_UA

// Gates isolate their state: a throwaway userData keeps partitions/cookies per run.
if (process.env.WIRECOPY_SHELL_USERDATA) app.setPath('userData', process.env.WIRECOPY_SHELL_USERDATA)

// One instance only: the TUI owns a shared browser profile + wirecopy.db.
// app.exit (not quit): quit() is async and pre-ready it can leave the process lingering.
if (!app.requestSingleInstanceLock()) {
  app.exit(0)
} else {
  app.on('second-instance', () => {
    if (state.win && !state.win.isDestroyed()) {
      if (state.win.isMinimized()) state.win.restore()
      state.win.focus()
    }
  })
}

const state = {
  win: null,
  termView: null,
  pageView: null,     // the visible "lens" pane
  pages: new Map(),   // tag → WebContentsView ("lens" + hidden fetch/prefetch pages)
  channelServer: null,
  ptyProc: null,
  mode: 'reader',     // 'reader' | 'browser'
  revealed: false,
  ptyDims: { cols: 0, rows: 0 }
}

function layout () {
  const { win, termView, pageView } = state
  if (!win || win.isDestroyed()) return
  const [w, h] = win.getContentSize()
  if (state.revealed) {
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
  const { win, termView } = state
  // NEVER pull OS-level focus. Every reader-refocus path is IN-WINDOW discipline only:
  // if the user switched to another app, the window is unfocused and we stay away —
  // webContents.focus() re-activates the whole app (field-reported focus trap on macOS,
  // reproduced under openbox; gate-focus-os pins this). When the user returns, the
  // window 'focus' handler restores reader focus.
  if (!win || win.isDestroyed() || !win.isFocused()) return
  if (!termView.webContents.isDestroyed()) termView.webContents.focus()
}

function setMode (m) {
  if (state.mode === m) return
  console.error(`[shell] mode → ${m}`)
  state.mode = m
  if (m === 'reader') focusTerm()
  else state.pageView.webContents.focus()
  if (state.channelServer) state.channelServer.broadcast('mode', { mode: m })
  // The terminal pane's focus keeper must stand down in browser mode: inside embedded
  // views document.hasFocus() tracks WINDOW focus, so an un-gated keeper steals
  // webContents focus back from the page every tick.
  if (!state.termView.webContents.isDestroyed()) state.termView.webContents.send('wc:mode', m)
}

// Hidden, tagged pages the .NET side adopts over CDP (fetch/prefetch). They are real
// members of a headful browser window — sized like tabs, just not visible. Never headless.
function createTaggedPage (tag) {
  if (state.pages.has(tag)) return { existed: true }
  const view = new WebContentsView({
    webPreferences: { partition: 'persist:wirecopy', backgroundThrottling: false }
  })
  view.setVisible(false)
  state.win.contentView.addChildView(view, 0) // bottom of the z-order, under the panes
  view.setBounds({ x: 0, y: 0, width: 1280, height: 720 })
  view.webContents.loadURL('about:blank#wc-' + tag).catch(() => {})
  state.pages.set(tag, view)
  return { existed: false }
}

function setPaneVisible (on) {
  state.revealed = !!on
  layout()
  if (!on) focusTerm()
}

// Reroute a key that landed on a page (reader mode) into the terminal — the user's
// keystroke is NEVER lost, no matter who won a focus race at that instant.
const KEYCODE_MAP = { ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', ' ': 'Space' }
function forwardToTerm (input) {
  const wc = state.termView.webContents
  if (wc.isDestroyed()) return
  const keyCode = KEYCODE_MAP[input.key] || input.key
  const modifiers = []
  if (input.shift) modifiers.push('shift')
  if (input.control) modifiers.push('control')
  if (input.alt) modifiers.push('alt')
  if (input.meta) modifiers.push('meta')
  if (input.type === 'keyUp') {
    wc.sendInputEvent({ type: 'keyUp', keyCode, modifiers })
    return
  }
  wc.sendInputEvent({ type: 'keyDown', keyCode, modifiers })
  if (input.key.length === 1) wc.sendInputEvent({ type: 'char', keyCode: input.key, modifiers })
}

function attachFocusDoctrine (pageView) {
  const wc = pageView.webContents
  // Deferred bounce: a click into the pane emits page-focus BEFORE the mouseDown handler
  // flips the mode — an instant bounce would yank focus back and undo the user's
  // deliberate entry (the electron#42922 event-storm trap).
  wc.on('focus', () => setTimeout(() => { if (state.mode === 'reader') focusTerm() }, 30))
  const refocus = () => { if (state.mode === 'reader') focusTerm() }
  wc.on('did-finish-load', refocus)
  wc.on('did-navigate', refocus)
  wc.on('did-frame-navigate', refocus)
  wc.on('before-input-event', (e, input) => {
    if (state.mode === 'reader') { e.preventDefault(); forwardToTerm(input); return }
    // Esc can arrive as keyDown OR rawKeyDown depending on the renderer's dispatch path.
    if ((input.type === 'keyDown' || input.type === 'rawKeyDown') && input.key === 'Escape') {
      e.preventDefault()
      setMode('reader')
    }
  })
  // A CLICK into the pane is a deliberate user gesture → browser mode. (Keyboard-side
  // stealing never carries a real mouseDown, so this cannot be triggered by page JS.)
  wc.on('input-event', (_e, input) => {
    if (state.mode === 'reader' && state.revealed && input.type === 'mouseDown') setMode('browser')
  })
  wc.setWindowOpenHandler(({ url }) => {
    wc.loadURL(url).catch(() => {})
    return { action: 'deny' }
  })
}

function spawnTui () {
  const file = process.env.WIRECOPY_SHELL_DOTNET || path.join(ROOT, 'dotnet')
  const dll = process.env.WIRECOPY_SHELL_TUI_DLL ||
    path.join(ROOT, 'src/WireCopy.API/bin/Release/net10.0/WireCopy.API.dll')
  const { cols, rows } = state.ptyDims
  const env = {
    ...process.env,
    TERM: 'xterm-256color',
    COLORTERM: 'truecolor',
    WIRECOPY_SHELL: '1',
    WIRECOPY_SHELL_CHANNEL: state.channelServer ? state.channelServer.socketPath : ''
  }
  // The pty is ONE stream: node-pty hands the child the slave for stdout AND stderr,
  // so any stderr from the TUI's process tree (a node-driver DeprecationWarning, dotnet
  // diagnostics) would paint INTO the terminal over the TUI's frames — field bug: DEP0169
  // overwrote the status bar. Route fd2 to a log file before exec, the desktop twin of
  // ./run's stderr tee. The path travels by env var so no shell-quoting of the path exists.
  let spawnFile = file
  let spawnArgs = ['exec', dll]
  if (process.platform !== 'win32') {
    const logDir = path.join(ROOT, 'logs')
    fs.mkdirSync(logDir, { recursive: true })
    const ts = new Date().toISOString().replace(/[:.]/g, '-')
    env.WIRECOPY_SHELL_CHILD_STDERR = path.join(logDir, `shell-child-stderr-${ts}.log`)
    spawnFile = '/bin/sh'
    spawnArgs = ['-c', 'exec "$0" "$@" 2>>"$WIRECOPY_SHELL_CHILD_STDERR"', file, 'exec', dll]
  }
  state.ptyProc = pty.spawn(spawnFile, spawnArgs, {
    name: 'xterm-256color',
    cols: cols || 80,
    rows: rows || 24,
    cwd: ROOT,
    env
  })
  state.ptyProc.onData(d => {
    if (!state.termView.webContents.isDestroyed()) state.termView.webContents.send('pty:data', d)
  })
  state.ptyProc.onExit(({ exitCode }) => {
    state.ptyProc = null
    // The TUI is the app: when it exits (user quit / crash), the shell goes with it.
    setTimeout(() => app.quit(), exitCode === 0 ? 0 : 1500)
  })
}

app.whenReady().then(() => {
  Menu.setApplicationMenu(null)
  state.win = new BaseWindow({ width: 1520, height: 940, title: 'Wire Copy', backgroundColor: '#0e0e14' })

  session.fromPartition('persist:wirecopy').setUserAgent(CHROME_UA)
  state.termView = new WebContentsView({
    webPreferences: { preload: path.join(__dirname, 'preload.js') }
  })
  state.pageView = new WebContentsView({
    webPreferences: { partition: 'persist:wirecopy' }
  })
  // Tag the lens pane so the .NET driver can adopt it over CDP by URL.
  state.pageView.webContents.loadURL('about:blank#wc-lens').catch(() => {})
  state.pages.set('lens', state.pageView)

  state.win.contentView.addChildView(state.pageView)
  state.win.contentView.addChildView(state.termView)
  layout()
  state.win.on('resize', layout)
  state.win.on('focus', () => { if (state.mode === 'reader') focusTerm() })
  state.win.on('closed', () => { try { state.ptyProc && state.ptyProc.kill() } catch {} })

  attachFocusDoctrine(state.pageView)
  try {
    state.termView.webContents.on('blur', () => {
      setTimeout(() => { if (state.mode === 'reader') focusTerm() }, 30)
    })
  } catch {}
  setInterval(() => {
    const { win, termView } = state
    if (state.mode === 'reader' && win && !win.isDestroyed() && win.isFocused() &&
        !termView.webContents.isFocused()) focusTerm()
  }, 150)

  // Control channel: up BEFORE the TUI spawns so the child can connect at boot.
  const socketPath = path.join(os.tmpdir(), `wirecopy-shell-${process.pid}.sock`)
  state.channelServer = channel.serve({
    socketPath,
    log: msg => console.error('[shell] ' + msg),
    handlers: {
      hello: () => ({ cdpEndpoint: `http://127.0.0.1:${CDP_PORT}` }),
      setPane: p => { setPaneVisible(!!p.visible); return { visible: state.revealed } },
      setMode: p => { setMode(p.mode === 'browser' ? 'browser' : 'reader'); return { mode: state.mode } },
      createPage: p => createTaggedPage(String(p.tag || ''))
    }
  })

  // Terminal pane UI over a loopback-only static server: term.html's node_modules/*
  // asset URLs resolve against the per-platform DEPS dir (file:// can't parameterize
  // that), and module/wasm loading gets a real origin for future pane variants.
  const http = require('http')
  const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.wasm': 'application/wasm' }
  const termSrv = http.createServer((req, res) => {
    const rel = decodeURIComponent(new URL(req.url, 'http://x/').pathname)
    const fp = rel.startsWith('/node_modules/')
      ? path.join(DEPS, rel.slice('/node_modules/'.length))
      : path.join(__dirname, rel)
    const rootOk = fp.startsWith(DEPS) || fp.startsWith(__dirname)
    if (!rootOk || !fs.existsSync(fp) || fs.statSync(fp).isDirectory()) { res.writeHead(404); res.end(); return }
    res.writeHead(200, { 'content-type': MIME[path.extname(fp)] || 'application/octet-stream' })
    fs.createReadStream(fp).pipe(res)
  })
  termSrv.listen(0, '127.0.0.1', () => {
    state.termView.webContents.loadURL(`http://127.0.0.1:${termSrv.address().port}/term.html`)
  })

  ipcMain.on('term:ready', (_e, dims) => {
    state.ptyDims = dims
    if (state.ptyProc) state.ptyProc.resize(dims.cols, dims.rows)
    else spawnTui()
  })
  ipcMain.on('pty:input', (_e, d) => { if (state.ptyProc) state.ptyProc.write(d) })
  ipcMain.on('term:resize', (_e, dims) => {
    state.ptyDims = dims
    if (state.ptyProc) state.ptyProc.resize(dims.cols, dims.rows)
  })

  // Dev/gate hooks (local IPC via the terminal pane; production control arrives over
  // the WIRECOPY_SHELL_CHANNEL socket in a later phase).
  ipcMain.on('wcdev:reveal', (_e, on) => setPaneVisible(on))
  ipcMain.on('wcdev:mode', (_e, m) => setMode(m === 'browser' ? 'browser' : 'reader'))
  ipcMain.handle('wcdev:state', () => ({
    mode: state.mode,
    revealed: state.revealed,
    ptyDims: state.ptyDims,
    contentSize: state.win.getContentSize(),
    termBounds: state.termView.getBounds(),
    pageBounds: state.pageView.getBounds(),
    termFocused: state.termView.webContents.isFocused(),
    pageFocused: state.pageView.webContents.isFocused()
  }))
  ipcMain.handle('wcdev:nav', async (_e, url) => {
    try { await state.pageView.webContents.loadURL(url) } catch (err) { return String(err) }
    return 'ok'
  })

  focusTerm()
})

app.on('window-all-closed', () => {
  try { if (state.ptyProc) state.ptyProc.kill() } catch {}
  app.quit()
})

app.on('will-quit', () => {
  try { if (state.channelServer) state.channelServer.close() } catch {}
})
