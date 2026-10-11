// Guards: clicking the Wire Copy toolbar button on an article, with the browser offline and the app
//         closed, puts it in the Reading List, and the app later opens it in the reader.
// Fails if: the native host stops answering, ignores the saved site layout, touches the network, or a
//         Reading List item opens anywhere but the reader (e.g. an empty link tree).
// Run: node shell/gates/gate-browser-extension.mjs chromium   (then: SKIP_BUILD=1 ... vivaldi)
//
// Gate (workspace-m7aml.4): headful end-to-end proof of the "Save to Wire Copy" browser extension.
//
// A REAL headful Chromium / Vivaldi (never headless, never --load-extension) on its own Xvfb, with a
// temp XDG_CONFIG_HOME / XDG_DATA_HOME (the real ~/.config and ~/.local/share are never touched).
// The extension is installed through the real chrome://extensions page (Developer mode + Load
// unpacked + the GTK file dialog), driven with xdotool. An article is then saved with the browser
// FULLY OFFLINE (proxy killed) and the Wire Copy app CLOSED; the host runs under an LD_PRELOAD shim
// that fails and logs every non-loopback connect. Finally the app is started and the saved article
// is read from the Reading List.
//
// Outcome checks are programmatic (CDP DOM text, sqlite, files); screenshots are for a human to read.
import { Env, buildTui, sleep, attach, pollTermText, check, summary, targets, Cdp, ROOT } from './lib.mjs'
import { execFileSync, spawn } from 'node:child_process'
import { DatabaseSync } from 'node:sqlite'
import { cpSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'

const kind = process.argv[2]
const PORTS = {
  chromium: { display: ':77', browser: 9377, app: 9378, proxy: 8777 },
  vivaldi: { display: ':79', browser: 9379, app: 9380, proxy: 8779 }
}
if (!PORTS[kind]) { console.error('usage: node gate-browser-extension.mjs chromium|vivaldi'); process.exit(2) }
const P = PORTS[kind]
const EXT_ID = 'cmidfakmpcnmaohapmcoigenlcjlahim'
const EXT_URL = `chrome-extension://${EXT_ID}/`
const GATE_DIR = path.join(ROOT, 'scripts', 'extension-gate')
const GAZETTE = 'http://gazette.wirecopy.test/2026/10/11/harbour-lights-return'
const WIKI = 'https://en.wikipedia.org/wiki/Lighthouse'
const EXTENSIONS_URL = kind === 'vivaldi' ? 'vivaldi://extensions' : 'chrome://extensions'

// ---------------------------------------------------------------- setup (step 1)
const base = mkdtempSync('/tmp/m7aml-e2e-')
const data = path.join(base, 'data')
const config = path.join(base, 'config')
const cache = path.join(base, 'cache')
const electron = path.join(base, 'electron')
for (const d of [data, config, cache, electron]) mkdirSync(d, { recursive: true })
const wcData = path.join(data, 'WireCopy')
const extDir = path.join(wcData, 'browser-extension')
const noinetLog = path.join(data, 'noinet.log')
const pidFile = path.join(base, 'browser.pid')
let step = 0
const NN = () => String(++step).padStart(2, '0')
const shot = (env, what) => env.shot(`ext-${kind}-${NN()}-${what}`)

const env = new Env({
  display: P.display,
  cdpPort: P.app,
  isolateData: false,
  env: { XDG_DATA_HOME: data, XDG_CONFIG_HOME: config, WIRECOPY_SHELL_USERDATA: electron }
})
const browserEnv = { ...process.env, XDG_DATA_HOME: data, XDG_CONFIG_HOME: config, XDG_CACHE_HOME: cache, NOINET_LOG: noinetLog, BROWSER_PID_FILE: pidFile }
let proxy = null
let appProc = null

// Kill every process of THIS run (browser, app, dotnet API, native host): anything whose environment
// carries our private XDG_DATA_HOME. Never touches another run's processes (no pkill -f by name).
function killRunProcs (sig = 'SIGTERM') {
  for (const pid of readdirSync('/proc').filter(n => /^\d+$/.test(n))) {
    if (Number(pid) === process.pid) continue
    try {
      const environ = readFileSync(`/proc/${pid}/environ`, 'latin1')
      if (environ.includes(`XDG_DATA_HOME=${data}\0`)) process.kill(Number(pid), sig)
    } catch {}
  }
}
function runProcs (pred) {
  const out = []
  for (const pid of readdirSync('/proc').filter(n => /^\d+$/.test(n))) {
    try {
      const environ = readFileSync(`/proc/${pid}/environ`, 'latin1')
      if (!environ.includes(`XDG_DATA_HOME=${data}\0`)) continue
      const cmd = readFileSync(`/proc/${pid}/cmdline`, 'latin1').replace(/\0/g, ' ')
      if (pred(cmd)) out.push({ pid: Number(pid), cmd })
    } catch {}
  }
  return out
}
async function killApp () {
  try { appProc?.kill() } catch {}
  killRunProcs('SIGTERM')
  await sleep(1500)
  // The browser is also in this set; only the app + API are meant to die at this point, so the
  // caller restarts nothing — see quitApp() for the app-only variant.
}
async function quitApp () {
  try { appProc?.kill() } catch {}
  for (const p of runProcs(c => /WireCopy\.API|electron/.test(c) && !/chrome|vivaldi/.test(c))) { try { process.kill(p.pid, 'SIGTERM') } catch {} }
  await sleep(2000)
  for (const p of runProcs(c => /WireCopy\.API|electron/.test(c) && !/chrome|vivaldi/.test(c))) { try { process.kill(p.pid, 'SIGKILL') } catch {} }
  await sleep(500)
}

// ---------------------------------------------------------------- browser CDP helpers
const bport = P.browser
const btargets = () => targets(bport).catch(() => [])
async function waitTarget (pred, ms = 20000, label = '') {
  const t0 = Date.now()
  while (Date.now() - t0 < ms) {
    const hit = (await btargets()).find(t => t.type !== 'service_worker' && pred(t))
    if (hit) return hit
    await sleep(300)
  }
  throw new Error('target not found: ' + label)
}
async function withPage (t, fn) {
  const c = await Cdp.connect(t.webSocketDebuggerUrl)
  try { return await fn(c) } finally { c.close() }
}
const activateTab = id => fetch(`http://127.0.0.1:${bport}/json/activate/${id}`).then(r => r.text())
const newTab = async url => (await fetch(`http://127.0.0.1:${bport}/json/new?${url}`, { method: 'PUT' })).json()
// Loaded = the tab's title (from the /json list, which survives cross-process navigations) matches.
async function waitLoaded (t, titleRe, ms = 40000) {
  const t0 = Date.now()
  while (Date.now() - t0 < ms) {
    const cur = (await btargets()).find(x => x.id === t.id)
    if (cur && titleRe.test(cur.title || '')) return cur.title
    await sleep(500)
  }
  return ''
}

// Screen position of a page: a REAL xdotool pointer move produces a DOM mousemove whose
// screenX/Y - clientX/Y is exactly the page's origin on the screen (works for Chromium and Vivaldi
// regardless of window decorations, side panels, ...).
async function pageOrigin (c, probes = [[700, 520], [900, 500], [500, 600]]) {
  await c.eval(`window.__mm = null; addEventListener('mousemove', e => { window.__mm = [e.screenX - e.clientX, e.screenY - e.clientY] }, true); 1`)
  for (const [x, y] of probes) {
    env.mouse(x, y); env.mouse(x + 2, y + 2)
    await sleep(300)
    const mm = await c.eval('window.__mm')
    if (mm) return { x: mm[0], y: mm[1] }
  }
  return null
}
// Screen centre of the element `jsExpr` (evaluates to an element or null) inside the page `c`.
async function elementScreenPoint (c, jsExpr, origin) {
  const r = await c.eval(`(() => { const el = (${jsExpr}); if (!el) return null; const b = el.getBoundingClientRect(); return { x: b.x + b.width / 2, y: b.y + b.height / 2, w: b.width, h: b.height } })()`)
  if (!r || r.w === 0) return null
  return { x: Math.round(origin.x + r.x), y: Math.round(origin.y + r.y) }
}
const EXT_MGR = "document.querySelector('extensions-manager').shadowRoot"
const extToolbar = `${EXT_MGR}.querySelector('extensions-toolbar').shadowRoot`

// Locate the toolbar button of the extension by the icon's green (RGB ~0,215,0) in a root screenshot.
function findGreenButton (region = { x0: 600, x1: 1400, y0: 30, y1: 100 }) {
  const png = path.join(base, 'toolbar.png')
  execFileSync('import', ['-display', P.display, '-window', 'root', png])
  const py = `
import sys
from PIL import Image
im = Image.open(sys.argv[1]).convert('RGB')
x0, x1, y0, y1 = map(int, sys.argv[2:6])
xs = []; ys = []
for y in range(y0, y1):
    for x in range(x0, x1):
        r, g, b = im.getpixel((x, y))
        if r < 50 and g > 190 and b < 50:
            xs.append(x); ys.append(y)
print(len(xs), (sum(xs) // len(xs)) if xs else -1, (sum(ys) // len(ys)) if ys else -1)
`
  const out = execFileSync('python3', ['-W', 'ignore', '-c', py, png, region.x0, region.x1, region.y0, region.y1].map(String)).toString().trim().split(' ').map(Number)
  return out[0] >= 8 ? { x: out[1], y: out[2], n: out[0] } : null
}

// Watches for the native-host process that the browser spawns while a save is in flight, and
// records whether noinet.so is mapped into it (best effort: the host lives for a few hundred ms).
function watchHost () {
  const seen = { pids: new Set(), withShim: false, cmd: '' }
  let stop = false
  const loop = (async () => {
    while (!stop) {
      for (const pid of readdirSync('/proc').filter(n => /^\d+$/.test(n))) {
        try {
          const cmd = readFileSync(`/proc/${pid}/cmdline`, 'latin1').replace(/\0/g, ' ')
          if (!/native-messaging-host|chrome-extension:\/\//.test(cmd) || /import|xdotool/.test(cmd)) continue
          if (!seen.pids.has(pid)) { seen.pids.add(pid); seen.cmd = cmd.slice(0, 120) }
          if (readFileSync(`/proc/${pid}/maps`, 'latin1').includes('noinet.so')) seen.withShim = true
        } catch {}
      }
      await sleep(40)
    }
  })()
  return { seen, stop: async () => { stop = true; await loop } }
}

// ---------------------------------------------------------------- the run
let exitCode = 1
try {
  await env.up()

  // 1 ---------------------------------------------------------------- setup
  mkdirSync(path.join(wcData, 'layouts'), { recursive: true })
  cpSync(path.join(GATE_DIR, 'fixture', 'layouts', 'gazette.wirecopy.test.json'), path.join(wcData, 'layouts', 'gazette.wirecopy.test.json'))
  if (!process.env.SKIP_BUILD) buildTui('Release')
  check(`1 [${kind}] setup: temp XDG dirs + seeded gazette layout`, existsSync(path.join(wcData, 'layouts', 'gazette.wirecopy.test.json')), base)

  // 2 ---------------------------------------------------------------- proxy + browser
  proxy = spawn('python3', [path.join(GATE_DIR, 'proxy.py'), String(P.proxy)], { stdio: ['ignore', 'pipe', 'pipe'] })
  proxy.stdout.on('data', () => {}); proxy.stderr.on('data', () => {})
  await sleep(800)
  const sb = spawn('bash', [path.join(GATE_DIR, 'start-browser.sh'), kind, P.display, String(P.browser), String(P.proxy), 'about:blank'],
    { env: browserEnv, stdio: ['ignore', 'pipe', 'pipe'] })
  let sbOut = ''
  sb.stdout.on('data', d => { sbOut += d }); sb.stderr.on('data', d => { sbOut += d })
  const sbCode = await new Promise(res => sb.on('exit', res))
  check(`2 [${kind}] proxy + headful browser up (start-browser.sh)`, sbCode === 0 && existsSync(pidFile), sbOut.trim().split('\n').pop())
  if (sbCode !== 0) throw new Error('browser did not start: ' + sbOut)
  const browserPid = Number(readFileSync(pidFile, 'utf8').trim())
  shot(env, 'browser-started')

  // 3 ---------------------------------------------------------------- boot the real app once, then close it
  appProc = env.launchShell()
  const term0 = await attach(P.app, 'term.html', 'terminal pane')
  const up = await pollTermText(term0, 'Go to URL', 60000)
  await sleep(2500) // let the host registration + extension unpack finish
  const nmh = path.join(config, kind, 'NativeMessagingHosts', 'com.wirecopy.reader.json')
  const hostBin = path.join(extDir, 'wirecopy-native-host')
  const extManifest = path.join(extDir, 'WireCopy-Extension', 'manifest.json')
  check(`3 [${kind}] app boot registers the host: manifest, wirecopy-native-host, unpacked extension`,
    up.ok && existsSync(nmh) && existsSync(hostBin) && existsSync(extManifest),
    `manifest=${existsSync(nmh)} host=${existsSync(hostBin)} ext=${existsSync(extManifest)} termOk=${up.ok}`)
  shot(env, 'app-booted')
  term0.close()
  await quitApp()
  check(`3b [${kind}] the app is CLOSED for the rest of the save`, runProcs(c => /WireCopy\.API|electron/.test(c) && !/chrome|vivaldi/.test(c)).length === 0)

  // 4 ---------------------------------------------------------------- install through chrome://extensions
  const extPage = await newTab(EXTENSIONS_URL)
  await activateTab(extPage.id)
  await sleep(2500)
  const extT = await waitTarget(t => /:\/\/extensions/.test(t.url), 15000, 'extensions page')
  const extC = await Cdp.connect(extT.webSocketDebuggerUrl)
  await activateTab(extT.id)
  await sleep(800)
  shot(env, 'extensions-page')
  const origin = await pageOrigin(extC)
  if (!origin) throw new Error('could not calibrate the extensions page origin')
  console.log('      page origin on screen', JSON.stringify(origin))
  // Developer mode toggle
  const devOn = () => extC.eval(`${extToolbar}.querySelector('#devMode').checked`)
  if (!(await devOn())) {
    const p = await elementScreenPoint(extC, `${extToolbar}.querySelector('#devMode')`, origin)
    env.click(p.x, p.y)
    for (let i = 0; i < 20 && !(await devOn()); i++) await sleep(250)
  }
  check(`4a [${kind}] Developer mode switched on by a real click`, await devOn())
  await sleep(600)
  const load = await elementScreenPoint(extC, `${extToolbar}.querySelector('#loadUnpacked')`, origin)
  if (!load) throw new Error('Load unpacked button not visible')
  shot(env, 'devmode-on')
  env.click(load.x, load.y)

  // The GTK file chooser. openbox stacks it BEHIND the browser after clicks (screenshots lie), so we
  // re-activate it before every input. "Recent" is the default view and ignores Return: click Home first.
  let wid = ''
  for (let i = 0; i < 60 && !wid; i++) {
    try { wid = env.x('search', '--name', 'Select the extension directory').trim().split('\n')[0] } catch {}
    if (!wid) await sleep(500)
  }
  check(`4b [${kind}] the "Select the extension directory" dialog opens`, Boolean(wid), wid)
  if (!wid) throw new Error('GTK dialog did not appear')
  const geom = Object.fromEntries(env.x('getwindowgeometry', '--shell', wid).trim().split('\n').map(l => l.split('=')))
  const act = () => { try { env.x('windowactivate', '--sync', wid) } catch {} }
  act(); await sleep(500)
  env.click(Number(geom.X) + 54, Number(geom.Y) + 59) // sidebar "Home"
  await sleep(700); act(); await sleep(400)
  env.key('ctrl+l'); await sleep(300)
  env.key('ctrl+a'); await sleep(200)
  env.type(path.join(extDir, 'WireCopy-Extension') + '/'); await sleep(600)
  shot(env, 'file-dialog-path')
  // Click the dialog's Open button, as a user does. The first click only closes the path-completion
  // popup, so click again while the dialog is still up. (Return in the path box is not reliable:
  // Vivaldi's dialog reports "File selection was canceled." for it.) Open sits 52/25 px in from the
  // dialog's bottom-right corner.
  const dialogUp = () => { try { return env.x('search', '--name', 'Select the extension directory').trim().length > 0 } catch { return false } }
  for (let i = 0; i < 4 && dialogUp(); i++) {
    act(); await sleep(300)
    env.click(Number(geom.X) + Number(geom.WIDTH) - 52, Number(geom.Y) + Number(geom.HEIGHT) - 25)
    await sleep(1500)
  }
  const itemIds = () => extC.eval(`(() => { try { return [...${EXT_MGR}.querySelector('extensions-item-list').shadowRoot.querySelectorAll('extensions-item')].map(i => i.id) } catch { return [] } })()`).catch(() => [])
  let ids = []
  for (let i = 0; i < 40 && !ids.includes(EXT_ID); i++) { await sleep(500); ids = await itemIds() }
  check(`4c [${kind}] extension installed through Load unpacked (extensions-item ${EXT_ID})`, ids.includes(EXT_ID), JSON.stringify(ids))
  shot(env, 'extension-installed')
  if (!ids.includes(EXT_ID)) throw new Error('extension did not install')

  // 5 ---------------------------------------------------------------- welcome page says connected
  let welcome = null
  let connectedOk = false
  let h1 = ''
  const t5 = Date.now()
  while (Date.now() - t5 < 25000 && !connectedOk) {
    welcome = (await btargets()).find(t => t.url.startsWith(EXT_URL + 'welcome.html'))
    if (welcome) {
      try {
        const r = await withPage(welcome, c => c.eval(`(() => { const s = document.getElementById('connected'); return { hidden: s.hidden, h1: s.querySelector('h1').textContent.trim() } })()`))
        h1 = r.h1
        connectedOk = !r.hidden && r.h1 === 'Wire Copy is connected'
      } catch {}
    }
    if (!connectedOk) await sleep(700)
  }
  check(`5 [${kind}] welcome page: #connected visible, h1 "Wire Copy is connected"`, connectedOk, `welcome=${Boolean(welcome)} h1=${h1}`)
  if (welcome) { await activateTab(welcome.id); await sleep(800) }
  shot(env, 'welcome-connected')
  const connFile = path.join(extDir, 'connections.json')
  let conn = {}
  try { conn = JSON.parse(readFileSync(connFile, 'utf8')) } catch {}
  check(`5b [${kind}] connections.json has a key for this browser`, Object.keys(conn).length > 0, JSON.stringify(conn).slice(0, 160))

  // 6 ---------------------------------------------------------------- locate the toolbar button
  let button = null
  if (kind === 'chromium') {
    const detail = await newTab(`chrome://extensions/?id=${EXT_ID}`)
    await activateTab(detail.id); await sleep(2000)
    const dt = await waitTarget(t => t.id === detail.id || t.url.includes(`extensions/?id=${EXT_ID}`), 10000, 'detail view')
    await withPage(dt, async c => {
      const o = await pageOrigin(c)
      const pinSel = `${EXT_MGR}.querySelector('extensions-detail-view').shadowRoot.querySelector('#pin-to-toolbar')`
      // The toggle sits below the fold of the detail view: bring it on screen first, as a user scrolls.
      await c.eval(`${pinSel}?.scrollIntoView({ block: 'center' })`)
      await sleep(600)
      const p = await elementScreenPoint(c, pinSel, o)
      shot(env, 'detail-before-pin')
      if (!p) throw new Error('pin toggle not found')
      env.click(p.x, p.y)
      await sleep(1200)
      const pinned = await c.eval(`${pinSel}.checked`)
      check(`6a [chromium] "Pin to toolbar" toggle is on after a real click`, pinned === true, String(pinned))
    })
    await sleep(500)
    button = findGreenButton()
  } else {
    const ui = await waitTarget(t => /window\.html|main\.html|components\/main/.test(t.url) || t.url.includes('vivaldi-webui'), 8000, 'vivaldi ui').catch(() => null)
    if (ui) {
      button = await withPage(ui, async c => {
        const expr = `(document.querySelector('[title="Save to Wire Copy Reading List"]') || [...document.querySelectorAll('[class*="Extension"] button, button[class*="Extension"], .ExtensionAction')].find(e => /Wire Copy/i.test(e.title || e.getAttribute('aria-label') || '')))`
        const o = await pageOrigin(c, [[700, 40], [900, 45], [500, 35], [1000, 60]])
        if (!o) return null
        return elementScreenPoint(c, expr, o)
      })
    }
    if (!button) button = findGreenButton()
  }
  check(`6 [${kind}] toolbar button located on screen`, Boolean(button), JSON.stringify(button))
  shot(env, 'toolbar-located')
  if (!button) throw new Error('toolbar button not found')

  // 7 ---------------------------------------------------------------- article tabs, then OFFLINE
  const gTab = await newTab(GAZETTE)
  const gTitle = await waitLoaded(gTab, /Harbour/i)
  const wTab = await newTab(WIKI)
  const wTitle = await waitLoaded(wTab, /Lighthouse/i, 60000)
  await sleep(3000) // let the real page settle before the network goes away
  check(`7a [${kind}] gazette fixture + real Wikipedia article loaded online`, Boolean(gTitle && wTitle), `${gTitle} | ${wTitle}`)
  proxy.kill('SIGKILL')
  await sleep(800)
  let offline = ''
  try { offline = execFileSync('node', [path.join(GATE_DIR, 'cdp.mjs'), String(P.browser), 'offline-check', 'gazette'], { timeout: 30000 }).toString().trim() } catch (e) { offline = 'ERR ' + e.message }
  check(`7b [${kind}] proxy killed: browser is fully offline`, offline === 'offline', offline)
  await activateTab(gTab.id); await sleep(800)
  shot(env, 'gazette-offline')

  // 8 ---------------------------------------------------------------- click the toolbar button, per article
  async function saveViaToolbar (tab, label, wantWordsNonEmpty = true) {
    // close any popup left over from the previous save so the click opens a fresh one
    for (const pt of (await btargets()).filter(t => t.url.startsWith(EXT_URL + 'popup.html'))) await withPage(pt, c => c.eval('window.close()').catch(() => {})).catch(() => {})
    await activateTab(tab.id); await sleep(1000)
    // re-locate: pinned-button x can shift; for chromium the colour search is cheap
    if (kind === 'chromium') button = findGreenButton() || button
    const w = watchHost()
    env.click(button.x, button.y)
    let st = null
    const t0 = Date.now()
    while (Date.now() - t0 < 30000) {
      const pt = (await btargets()).find(t => t.url.startsWith(EXT_URL + 'popup.html'))
      if (pt) {
        try {
          st = await withPage(pt, c => c.eval(`(() => { const s = document.getElementById('saved'); return { hidden: s.hidden, heading: document.getElementById('saved-heading').textContent.trim(), words: document.getElementById('saved-words').textContent.trim(), title: document.getElementById('saved-title').textContent.trim(), err: document.getElementById('error').hidden ? '' : document.getElementById('error-message').textContent.trim(), cant: document.getElementById('cant-save').hidden ? '' : document.getElementById('cant-save-reason').textContent.trim() } })()`))
          if (!st.hidden || st.err || st.cant) break
        } catch {}
      }
      await sleep(250)
    }
    const ok = st && !st.hidden && st.heading === 'Saved to Reading List' && (!wantWordsNonEmpty || st.words.length > 0)
    check(`8 [${kind}] popup after clicking the toolbar button on ${label}: #saved visible, "Saved to Reading List", words non-empty`, ok, JSON.stringify(st))
    shot(env, `popup-saved-${label}`)
    await w.stop()
    return { st, host: w.seen }
  }
  const gSave = await saveViaToolbar(gTab, 'gazette')
  const wSave = await saveViaToolbar(wTab, 'wikipedia')
  const hostSeen = gSave.host.pids.size ? gSave.host : wSave.host
  console.log(`      host process sighted: ${hostSeen.pids.size ? hostSeen.cmd : 'no (too short-lived to sample)'}; noinet.so mapped: ${hostSeen.withShim}`)

  // 9 ---------------------------------------------------------------- outcome asserts
  const db = new DatabaseSync(path.join(wcData, 'wirecopy.db'), { readOnly: true })
  const rows = db.prepare('select Url, Title from CollectionItems').all()
  db.close()
  console.log('      CollectionItems:', JSON.stringify(rows).slice(0, 400))
  const hasUrl = u => rows.some(r => String(r.Url).replace(/\/$/, '') === u.replace(/\/$/, ''))
  check(`9a [${kind}] DB: CollectionItems holds the gazette URL`, hasUrl(GAZETTE), GAZETTE)
  check(`9b [${kind}] DB: CollectionItems holds the Wikipedia URL`, hasUrl(WIKI), WIKI)
  const cacheDir = path.join(wcData, 'article-cache')
  let idx = {}
  try { idx = JSON.parse(readFileSync(path.join(cacheDir, 'index.json'), 'utf8')) } catch {}
  const entries = Array.isArray(idx) ? idx : (idx.entries ? (Array.isArray(idx.entries) ? idx.entries : Object.entries(idx.entries).map(([k, v]) => ({ key: k, ...v }))) : Object.entries(idx).map(([k, v]) => ({ key: k, ...(typeof v === 'object' ? v : {}) })))
  const gEntry = entries.find(e => JSON.stringify(e).includes('harbour-lights-return'))
  check(`9c [${kind}] article-cache/index.json has an entry for the gazette URL`, Boolean(gEntry), gEntry ? JSON.stringify(gEntry).slice(0, 200) : `entries=${entries.length}`)
  let artText = ''
  if (gEntry) {
    const rel = gEntry.ArticleFilePath || gEntry.articleFilePath || gEntry.ArticleFile || gEntry.articleFile || gEntry.file
    const abs = rel ? (path.isAbsolute(rel) ? rel : path.join(cacheDir, rel)) : ''
    try { artText = readFileSync(abs, 'utf8') } catch (e) { console.log('      could not read article file', abs, e.message) }
  }
  check(`9d [${kind}] cached gazette article contains "Skerrow Point" and not "Ten things to do this weekend" (saved site layout used)`,
    artText.includes('Skerrow Point') && !artText.includes('Ten things to do this weekend'), `${artText.length} chars`)
  const noinet = existsSync(noinetLog) ? readFileSync(noinetLog, 'utf8').split('\n').filter(Boolean) : []
  const hostBlocks = noinet.filter(l => /exe=\S*(dotnet|WireCopy)/i.test(l) && /verdict=block/.test(l))
  const hostAny = noinet.filter(l => /exe=\S*(dotnet|WireCopy)/i.test(l))
  check(`9e [${kind}] noinet.log: ZERO blocked non-loopback connects from the host (dotnet/WireCopy)`, hostBlocks.length === 0,
    `${noinet.length} log lines, ${hostAny.length} from host, ${hostBlocks.length} blocked`)
  check(`9f [${kind}] (best effort) the host process was sighted with noinet.so mapped`, hostSeen.withShim, hostSeen.pids.size ? hostSeen.cmd : 'host not sampled')

  // 10 --------------------------------------------------------------- relaunch the app, read the saved article
  // The browser is no longer needed; closing it also keeps its tab titles out of window lookups.
  try { process.kill(browserPid, 'SIGTERM') } catch {}
  killRunProcs('SIGTERM')
  await sleep(2500)
  killRunProcs('SIGKILL')
  appProc = env.launchShell()
  const term = await attach(P.app, 'term.html', 'terminal pane')
  const launcherUp = await pollTermText(term, 'Go to URL', 60000)
  await sleep(1500)
  env.activate(); await sleep(800)
  const press = async (k, wait, pred, tries = 5) => {
    let r = { ok: false, txt: '' }
    for (let i = 0; i < tries && !r.ok; i++) { env.key(k); r = await pollTermText(term, wait, 5000); if (pred && r.ok && !pred(r.txt)) r.ok = false }
    return r
  }
  // Reading List tile: digit 2 (retry; xdotool into the terminal sometimes needs a second try)
  let rl = { ok: false, txt: '' }
  // A digit only SELECTS a launcher tile; Enter opens it. If a stray key opened something else, Esc back.
  for (let i = 0; i < 5 && !rl.ok; i++) {
    env.key('2'); await sleep(700); env.key('Return')
    rl = await pollTermText(term, 'Harbour', 6000)
    if (!rl.ok) { for (let j = 0; j < 4 && !/Go to URL/.test(await term.eval('window.__termText()')); j++) { env.key('Escape'); await sleep(900) } }
  }
  check(`10a [${kind}] app (started AFTER the save): Reading List shows the saved "Harbour" article`, launcherUp.ok && rl.ok, rl.ok ? '' : rl.txt.slice(0, 300).replace(/\n+/g, ' | '))
  shot(env, 'readinglist')
  // select the Harbour row (the Wikipedia item was saved later, so it is on top)
  for (let i = 0; i < 12; i++) {
    const txt = await term.eval('window.__termText()')
    if (txt.split('\n').some(l => l.includes('▌') && l.includes('Harbour'))) break
    env.key('Down'); await sleep(500)
  }
  const selTxt = await term.eval('window.__termText()')
  check(`10b [${kind}] the selection marker is on the Harbour row`, selTxt.split('\n').some(l => l.includes('▌') && l.includes('Harbour')))
  env.key('Return')
  const reader = await pollTermText(term, 'Skerrow Point', 30000)
  await sleep(1000)
  const readerTxt = await term.eval('window.__termText()')
  check(`10c [${kind}] reader opens the saved article: shows "Skerrow Point", not "Ten things to do"`,
    reader.ok && readerTxt.includes('Skerrow Point') && !readerTxt.includes('Ten things to do'), readerTxt.slice(0, 160).replace(/\n+/g, ' | '))
  shot(env, 'reader-text')
  // back to the launcher, then Settings -> Browser extension
  for (let i = 0; i < 6; i++) {
    const t = await term.eval('window.__termText()')
    if (/Go to URL/.test(t) && !/Skerrow Point/.test(t)) break
    env.key('Escape'); await sleep(900)
  }
  let st = { ok: false, txt: '' }
  for (let i = 0; i < 5 && !st.ok; i++) { env.key('c'); st = await pollTermText(term, 'Browser extension', 5000) }
  check(`10d [${kind}] Settings opens and lists "Browser extension"`, st.ok)
  env.key('Up'); await sleep(1000)
  env.key('Return')
  const card = await pollTermText(term, 'Save articles from your browser', 10000)
  await sleep(1500)
  const cardTxt = await term.eval('window.__termText()')
  const browserName = kind === 'chromium' ? /Chromium|Chrome/i : /Vivaldi/i
  const rowLine = cardTxt.split('\n').find(l => browserName.test(l) && l.includes('connected'))
  check(`10e [${kind}] settings card: this browser's row says "✓ connected" and "[Enter] Manage"`,
    card.ok && Boolean(rowLine) && rowLine.includes('✓ connected') && cardTxt.includes('[Enter] Manage'),
    rowLine ? rowLine.trim() : cardTxt.replace(/\n\s*\n/g, '\n').slice(0, 400))
  shot(env, 'settings-card')
  term.close()
} catch (e) {
  console.log('GATE ERROR:', e.stack || e.message)
  try { shot(env, 'error-state') } catch {}
} finally {
  try { proxy?.kill('SIGKILL') } catch {}
  try { appProc?.kill() } catch {}
  killRunProcs('SIGTERM')
  await sleep(1500)
  killRunProcs('SIGKILL')
  env.down()
  const fails = summary()
  console.log(`(artifacts: ${base})`)
  exitCode = fails ? 1 : 0
}
process.exit(exitCode)
