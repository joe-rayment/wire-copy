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
const { app, BaseWindow, WebContentsView, ipcMain, Menu, screen, session } = require('electron')
const { buildAppMenuTemplate } = require('./menu')
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

// Packaged app (workspace-mwer): the self-contained .NET API ships under resources/api
// (its own apphost — no dotnet on the machine needed), and writable state (logs, the
// TUI's working dir) moves to userData; the repo layout only exists in dev.
const PACKAGED = app.isPackaged
const API_DIR = PACKAGED ? path.join(process.resourcesPath, 'api') : null
function logDir () {
  const dir = PACKAGED ? path.join(app.getPath('userData'), 'logs') : path.join(ROOT, 'logs')
  fs.mkdirSync(dir, { recursive: true })
  return dir
}

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
  popupView: null,    // SSO/auth popup pane (workspace-y0bi) — at most one, over the page pane
  pages: new Map(),   // tag → WebContentsView ("lens" + hidden fetch/prefetch pages)
  channelServer: null,
  ptyProc: null,
  mode: 'reader',     // 'reader' | 'browser'
  revealed: false,
  paneShown: false,   // physical visibility (lags `revealed` during the collapse slide)
  ptyDims: { cols: 0, rows: 0 }
}

// ---- persisted user prefs (window bounds, terminal type scale) ----
const prefsFile = name => path.join(app.getPath('userData'), name)
function readJson (file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')) } catch { return null }
}
function writeJson (file, obj) {
  try { fs.writeFileSync(file, JSON.stringify(obj)) } catch {}
}

// Window bounds: default = 85% of the work area, centered; a remembered size/position
// is restored clamped to a display that still exists (external monitor unplugged, etc).
function initialBounds () {
  const saved = readJson(prefsFile('window-bounds.json'))
  if (saved && [saved.x, saved.y, saved.width, saved.height].every(Number.isFinite)) {
    const wa = screen.getDisplayMatching(saved).workArea
    const width = Math.min(saved.width, wa.width)
    const height = Math.min(saved.height, wa.height)
    return {
      x: Math.min(Math.max(saved.x, wa.x), wa.x + wa.width - width),
      y: Math.min(Math.max(saved.y, wa.y), wa.y + wa.height - height),
      width,
      height
    }
  }
  const wa = screen.getPrimaryDisplay().workArea
  const width = Math.round(wa.width * 0.85)
  const height = Math.round(wa.height * 0.85)
  return { x: wa.x + Math.round((wa.width - width) / 2), y: wa.y + Math.round((wa.height - height) / 2), width, height }
}

let saveBoundsTimer = null
function rememberBounds () {
  const { win } = state
  if (!win || win.isDestroyed() || win.isMinimized() || win.isFullScreen()) return
  clearTimeout(saveBoundsTimer)
  saveBoundsTimer = setTimeout(() => {
    if (win.isDestroyed()) return
    writeJson(prefsFile('window-bounds.json'), win.getBounds())
  }, 400)
}

// Terminal type scale (field: "everything is a little smaller"): Cmd/Ctrl +/-/0 zoom,
// persisted; the initial value rides term.html's query string so the first fit is right.
const FONT_DEFAULT = 16
const FONT_MIN = 8
const FONT_MAX = 32
let termFontSize = (() => {
  const saved = readJson(prefsFile('term-prefs.json'))
  const px = saved ? Number(saved.fontSize) : NaN
  // Anything outside the legal zoom range (including the Number(null)===0 trap on a
  // fresh profile) means "no valid preference" — take the default, don't clamp.
  return px >= FONT_MIN && px <= FONT_MAX ? px : FONT_DEFAULT
})()
function setTermFont (px) {
  const next = Math.min(FONT_MAX, Math.max(FONT_MIN, px))
  if (next === termFontSize) return
  termFontSize = next
  writeJson(prefsFile('term-prefs.json'), { fontSize: termFontSize })
  if (!state.termView.webContents.isDestroyed()) state.termView.webContents.send('wc:font', termFontSize)
}
function handleZoomKey (input) {
  if (input.type !== 'keyDown') return false
  const mod = process.platform === 'darwin' ? input.meta : input.control
  if (!mod || input.alt || input.shift) return false
  if (input.key === '=' || input.key === '+') { setTermFont(termFontSize + 1); return true }
  if (input.key === '-') { setTermFont(termFontSize - 1); return true }
  if (input.key === '0') { setTermFont(FONT_DEFAULT); return true }
  return false
}

function layout () {
  // INSTANT placement — boot and window resizes. Reveal/collapse transitions go
  // through animatePane instead (workspace-va2s); a resize mid-flight snaps here.
  const { win, termView, pageView } = state
  if (!win || win.isDestroyed()) return
  stopPaneAnim()
  const [w, h] = win.getContentSize()
  if (state.revealed) {
    const pageW = Math.round(w * PAGE_FRACTION)
    termView.setBounds({ x: 0, y: 0, width: w - pageW, height: h })
    pageView.setVisible(true)
    pageView.setBounds({ x: w - pageW, y: 0, width: pageW, height: h })
    state.paneShown = true
  } else {
    termView.setBounds({ x: 0, y: 0, width: w, height: h })
    pageView.setVisible(false)
    state.paneShown = false
  }
  positionPopup()
}

// Pane reveal/collapse animation (workspace-va2s): a macOS-minimize-style eased slide.
// Two hard rules: the TERMINAL reflows exactly ONCE per transition (animating its
// width would storm the pty with resizes and thrash the TUI), and the page view
// animates POSITION only, at final size (animating its width would reflow the live
// site every frame). Reveal: the terminal narrows up front — the slide draws the eye
// past that single reflow — and the pane glides in from the right edge (ease-out).
// Collapse: the pane glides out (ease-in), and only THEN does the terminal expand.
// Reversible mid-flight; window resizes snap via layout().
const PANE_ANIM_MS = 240
const PANE_ANIM_DISABLED = process.env.WIRECOPY_SHELL_NO_ANIM === '1'
let paneAnim = null

function stopPaneAnim () {
  if (paneAnim) {
    clearInterval(paneAnim.timer)
    paneAnim = null
  }
}

function animatePane (show) {
  const { win, termView, pageView } = state
  if (!win || win.isDestroyed()) return
  const [w, h] = win.getContentSize()
  const pageW = Math.round(w * PAGE_FRACTION)
  const dockedX = w - pageW
  const wasMidFlight = !!paneAnim
  stopPaneAnim()
  if (show) {
    termView.setBounds({ x: 0, y: 0, width: dockedX, height: h })
    // A fresh reveal starts at the right edge; reversing a mid-flight collapse
    // resumes from wherever the pane currently sits.
    const resume = wasMidFlight || state.paneShown
    const startX = resume ? Math.max(dockedX, Math.min(w, pageView.getBounds().x)) : w
    pageView.setBounds({ x: startX, y: 0, width: pageW, height: h })
    pageView.setVisible(true)
    state.paneShown = true
  }
  const fromX = pageView.getBounds().x
  const toX = show ? dockedX : w
  const ease = show ? (t => 1 - Math.pow(1 - t, 3)) : (t => t * t * t)
  const t0 = Date.now()
  const timer = setInterval(() => {
    if (win.isDestroyed()) { stopPaneAnim(); return }
    const t = Math.min(1, (Date.now() - t0) / PANE_ANIM_MS)
    pageView.setBounds({ x: Math.round(fromX + (toX - fromX) * ease(t)), y: 0, width: pageW, height: h })
    if (t >= 1) {
      stopPaneAnim()
      if (!show) {
        pageView.setVisible(false)
        state.paneShown = false
        termView.setBounds({ x: 0, y: 0, width: w, height: h })
      }
      positionPopup()
    }
  }, 16)
  paneAnim = { timer, show }
}

// The SSO popup pane sits centered over the PAGE pane (never over the reader) at a
// provider-friendly size, clamped to the pane. Recomputed on every layout change.
function positionPopup () {
  const { popupView, pageView, win } = state
  if (!popupView || !win || win.isDestroyed()) return
  const pb = pageView.getBounds()
  const width = Math.min(500, Math.max(320, Math.round(pb.width * 0.92)))
  const height = Math.min(640, Math.max(400, Math.round(pb.height * 0.85)))
  popupView.setBounds({
    x: pb.x + Math.round((pb.width - width) / 2),
    y: pb.y + Math.round((pb.height - height) / 2),
    width,
    height
  })
}

function closePopupPane () {
  const popup = state.popupView
  if (!popup) return
  state.popupView = null
  try { state.win.contentView.removeChildView(popup) } catch {}
  try { if (!popup.webContents.isDestroyed()) popup.webContents.close() } catch {}
  // The opener page keeps the keyboard — SSO hand-back lands where the user was.
  if (state.mode === 'browser' && !state.pageView.webContents.isDestroyed()) {
    state.pageView.webContents.focus()
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
  const next = !!on
  // The .NET side reconciles pane state on every navigation — a repeated same-state
  // call must NOT retrigger (or snap) an animation in flight.
  if (state.revealed === next) return
  state.revealed = next
  if (!next) closePopupPane() // a popup must never float orphaned over the full reader
  if (PANE_ANIM_DISABLED) layout()
  else animatePane(next)
  if (!next) focusTerm()
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
    // It peels one layer at a time: an open SSO popup closes first; reader mode second.
    if ((input.type === 'keyDown' || input.type === 'rawKeyDown') && input.key === 'Escape') {
      e.preventDefault()
      if (state.popupView) closePopupPane()
      else setMode('reader')
    }
  })
  // A CLICK into the pane is a deliberate user gesture → browser mode. (Keyboard-side
  // stealing never carries a real mouseDown, so this cannot be triggered by page JS.)
  wc.on('input-event', (_e, input) => {
    if (state.mode === 'reader' && state.revealed && input.type === 'mouseDown') setMode('browser')
  })
  // window.open routing (workspace-y0bi): a REAL popup (window.open with features —
  // the SSO shape: Google/Apple sign-in) gets an owned TEMP PANE over the page pane;
  // popup-preload.js shims window.opener.postMessage/window.close over IPC (relayed
  // into this opener page with the popup's real origin). Everything else (_blank
  // links) stays same-pane. NOTE: the createWindow-override route is a dead end here —
  // returning a WebContentsView's contents from it WEDGES Electron 43's main loop
  // (spike-proven: setImmediate/timers stop right after the callback returns).
  wc.setWindowOpenHandler(details => {
    if (details.disposition === 'new-window') {
      openPopupPane(details.url)
      return { action: 'deny' }
    }

    wc.loadURL(details.url).catch(() => {})
    return { action: 'deny' }
  })
}

function openPopupPane (url) {
  closePopupPane() // one at a time — a fresh SSO popup replaces a stale one
  const popup = new WebContentsView({
    webPreferences: {
      partition: 'persist:wirecopy',
      preload: path.join(__dirname, 'popup-preload.js'),
      contextIsolation: false,
      sandbox: false,
      nodeIntegration: false
    }
  })
  state.popupView = popup
  state.win.contentView.addChildView(popup) // topmost
  positionPopup()
  popup.webContents.loadURL(url).catch(() => {})
  setTimeout(() => { try { popup.webContents.focus() } catch {} }, 80)
  popup.webContents.once('destroyed', () => {
    if (state.popupView === popup) closePopupPane()
  })
  popup.webContents.on('before-input-event', (e, input) => {
    if ((input.type === 'keyDown' || input.type === 'rawKeyDown') && input.key === 'Escape') {
      e.preventDefault()
      closePopupPane()
    }
  })
  // The popup itself never spawns further windows — nested opens stay in place.
  popup.webContents.setWindowOpenHandler(({ url: nested }) => {
    popup.webContents.loadURL(nested).catch(() => {})
    return { action: 'deny' }
  })
  console.error('[shell] SSO popup pane opened: ' + url)
}

function spawnTui () {
  // Dev: the repo's ./dotnet wrapper + Release dll. Packaged: the bundled self-contained
  // apphost under resources/api — no SDK/runtime on the machine, no repo layout.
  let file
  let baseArgs
  if (PACKAGED) {
    file = path.join(API_DIR, process.platform === 'win32' ? 'WireCopy.API.exe' : 'WireCopy.API')
    baseArgs = []
  } else {
    file = process.env.WIRECOPY_SHELL_DOTNET || path.join(ROOT, 'dotnet')
    baseArgs = ['exec', process.env.WIRECOPY_SHELL_TUI_DLL ||
      path.join(ROOT, 'src/WireCopy.API/bin/Release/net10.0/WireCopy.API.dll')]
  }
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
  let spawnArgs = baseArgs
  if (process.platform !== 'win32') {
    const ts = new Date().toISOString().replace(/[:.]/g, '-')
    env.WIRECOPY_SHELL_CHILD_STDERR = path.join(logDir(), `shell-child-stderr-${ts}.log`)
    spawnFile = '/bin/sh'
    spawnArgs = ['-c', 'exec "$0" "$@" 2>>"$WIRECOPY_SHELL_CHILD_STDERR"', file, ...baseArgs]
  }
  state.ptyProc = pty.spawn(spawnFile, spawnArgs, {
    name: 'xterm-256color',
    cols: cols || 80,
    rows: rows || 24,
    cwd: PACKAGED ? app.getPath('userData') : ROOT,
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
  // macOS keeps a REAL app menu (About/Quit + Edit clipboard roles + Window) — without
  // an Edit menu Cmd+C/V never reach a page input, and the default menu says "Electron".
  // Linux/Windows shows no menu bar at all. Template built by a pure, in-env-tested
  // helper (workspace-k60z / gate-d8-menu.mjs) rather than confirmed by a Mac eyeball.
  const menuTemplate = buildAppMenuTemplate(process.platform)
  Menu.setApplicationMenu(menuTemplate ? Menu.buildFromTemplate(menuTemplate) : null)

  state.win = new BaseWindow({ ...initialBounds(), title: 'Wire Copy', backgroundColor: '#000000' })

  const wcSession = session.fromPartition('persist:wirecopy')
  wcSession.setUserAgent(CHROME_UA)
  // Cert-error parity (workspace-83tf): terminal mode launches its browser with
  // Playwright IgnoreHTTPSErrors=true, so cert-invalid sites/subresources (field log:
  // net_error -201 CERT_DATE_INVALID) load where the shell's strict partition failed.
  // Mirror that tolerance SCOPED to the wirecopy partition — every app pane/page lives
  // there; the default session (and any other partition) stays strict.
  wcSession.setCertificateVerifyProc((_req, cb) => cb(0))
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
  state.win.on('resize', () => { layout(); rememberBounds() })
  state.win.on('move', rememberBounds)
  state.win.on('focus', () => { if (state.mode === 'reader') focusTerm() })
  state.win.on('closed', () => { try { state.ptyProc && state.ptyProc.kill() } catch {} })

  attachFocusDoctrine(state.pageView)
  // Type-scale zoom is a shell concern: intercept Cmd/Ctrl +/-/0 on the terminal pane
  // before xterm sees them (the TUI binds no such chords — Ctrl+D/U/P/L only).
  state.termView.webContents.on('before-input-event', (e, input) => {
    if (handleZoomKey(input)) e.preventDefault()
  })
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
  const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.wasm': 'application/wasm', '.ttf': 'font/ttf', '.woff2': 'font/woff2' }
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
    // Initial type scale + platform ride the query string so the FIRST fit computes at
    // the right cell size (no boot-time double resize); live zoom arrives over wc:font.
    state.termView.webContents.loadURL(
      `http://127.0.0.1:${termSrv.address().port}/term.html?font=${termFontSize}&plat=${process.platform}`)
  })

  // SSO popup opener bridge (workspace-y0bi): the popup's shimmed opener.postMessage
  // arrives here and is REPLAYED into the opener page as a real MessageEvent carrying
  // the popup's actual origin; the shimmed window.close() tears the pane down. Both
  // are sender-guarded to the live popup only.
  ipcMain.on('wcpopup:post', (e, json) => {
    if (!state.popupView || e.sender !== state.popupView.webContents) return
    let origin = 'null'
    try { origin = new URL(e.sender.getURL()).origin } catch {}
    state.pageView.webContents.executeJavaScript(
      `window.dispatchEvent(new MessageEvent('message', { data: ${json}, origin: ${JSON.stringify(origin)} }))`
    ).catch(() => {})
  })
  ipcMain.on('wcpopup:close', e => {
    if (state.popupView && e.sender === state.popupView.webContents) closePopupPane()
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
    winBounds: state.win.getBounds(),
    termFontSize,
    popupOpen: !!state.popupView,
    popupBounds: state.popupView ? state.popupView.getBounds() : null,
    paneAnimating: !!paneAnim,
    paneShown: state.paneShown,
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
  // Gate seam (workspace-83tf): load a URL in a throwaway view on the DEFAULT session —
  // proves cert tolerance stays scoped to the wirecopy partition (control must reject).
  ipcMain.handle('wcdev:probeCertDefaultSession', async (_e, url) => {
    const v = new WebContentsView()
    try { await v.webContents.loadURL(url); return 'loaded' } catch (err) { return String(err) } finally { v.webContents.close() }
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
