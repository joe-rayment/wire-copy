// Gate runner for the single-window shell spike.
// Drives the REAL user actions (OS-level keys via xdotool) and asserts USER-VISIBLE OUTCOMES
// (TUI buffer text, page-side key counters, pane geometry, rendered screenshots).
import { execFileSync, execFile } from 'node:child_process'
import { writeFileSync, mkdirSync } from 'node:fs'

const PORT = process.env.SPIKE_CDP_PORT || '9333'
const DISPLAY = process.env.SPIKE_DISPLAY || ':77'
const OUT = new URL('./out/', import.meta.url).pathname
mkdirSync(OUT, { recursive: true })

const sleep = ms => new Promise(r => setTimeout(r, ms))
const results = []
function check (name, cond, detail = '') {
  results.push({ name, pass: !!cond, detail })
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`)
}

// ---------- raw CDP ----------
async function targets () {
  const r = await fetch(`http://127.0.0.1:${PORT}/json`)
  return r.json()
}
async function findTarget (pred, timeoutMs = 20000, label = '') {
  const t0 = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const list = await targets().catch(() => [])
    const hit = list.find(t => t.type === 'page' && pred(t))
    if (hit) return hit
    await sleep(300)
  }
  throw new Error(`target not found: ${label}`)
}
class Cdp {
  constructor (ws) { this.ws = ws; this.id = 0; this.pending = new Map() }
  static async connect (wsUrl) {
    const ws = new WebSocket(wsUrl)
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej })
    const c = new Cdp(ws)
    ws.onmessage = ev => {
      const m = JSON.parse(ev.data)
      if (m.id && c.pending.has(m.id)) {
        const { res, rej } = c.pending.get(m.id)
        c.pending.delete(m.id)
        m.error ? rej(new Error(m.error.message)) : res(m.result)
      }
    }
    return c
  }
  send (method, params = {}) {
    const id = ++this.id
    this.ws.send(JSON.stringify({ id, method, params }))
    return new Promise((res, rej) => this.pending.set(id, { res, rej }))
  }
  async eval (expr) {
    const r = await this.send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true })
    if (r.exceptionDetails) throw new Error('eval failed: ' + JSON.stringify(r.exceptionDetails.exception?.description || r.exceptionDetails.text))
    return r.result.value
  }
  close () { try { this.ws.close() } catch {} }
}
async function attach (urlPart, label = urlPart) {
  const t = await findTarget(t => t.url.includes(urlPart), 25000, label)
  return Cdp.connect(t.webSocketDebuggerUrl)
}

// ---------- OS-level input + screenshots ----------
function x (...args) { return execFileSync('xdotool', args, { env: { ...process.env, DISPLAY } }).toString() }
function activateWindow () {
  x('search', '--sync', '--name', 'WC-SPIKE')
  const wid = x('search', '--name', 'WC-SPIKE').trim().split('\n')[0]
  x('windowactivate', '--sync', wid)
  return wid
}
function type (text) { x('type', '--delay', '95', text) }
function key (k) { x('key', '--delay', '60', k) }
function shot (name) {
  execFileSync('import', ['-display', DISPLAY, '-window', 'root', `${OUT}${name}.png`])
  console.log(`      shot: out/${name}.png`)
}

async function pollTermText (term, substr, timeoutMs = 15000) {
  const t0 = Date.now()
  let txt = ''
  while (Date.now() - t0 < timeoutMs) {
    txt = await term.eval('window.__termText()')
    if (txt.includes(substr)) return { ok: true, txt }
    await sleep(400)
  }
  return { ok: false, txt }
}

// ================= PHASES =================
const dir = new URL('.', import.meta.url).pathname
let failEarly = false
try {
  // ---- A: boot — TUI renders inside our styled pane, full-window (app-primary) ----
  console.log('\n== A: boot + TUI render ==')
  const term = await attach('term.html', 'terminal pane')
  const boot = await pollTermText(term, 'Go to URL', 30000)
  check('A1 TUI launcher rendered in pane', boot.ok, boot.ok ? '"Go to URL" visible' : 'text: ' + boot.txt.slice(-400))
  const boot2 = await pollTermText(term, 'NPR TEXT', 10000)
  check('A2 launcher tiles rendered', boot2.ok)
  const st0 = await term.eval('window.__spike.state()')
  check('A3 boot layout is app-primary (terminal full-width)',
    st0.revealed === false && st0.termBounds.width === st0.contentSize[0],
    JSON.stringify(st0.termBounds))
  const bg = await term.eval('getComputedStyle(document.body).backgroundColor')
  check('A4 pane styling is ours (custom bg)', bg === 'rgb(14, 14, 20)', bg)
  activateWindow()
  await sleep(400)
  shot('a-boot')

  // ---- B: reveal page pane + real site + honest geometry ----
  console.log('\n== B: reveal + live page geometry ==')
  await term.eval('window.__spike.reveal(true)')
  await sleep(600)
  const st1 = await term.eval('window.__spike.state()')
  check('B1 revealed split: reader remains primary',
    st1.revealed && st1.termBounds.width > st1.pageBounds.width,
    `term=${st1.termBounds.width}px page=${st1.pageBounds.width}px`)
  const nav1 = await term.eval('window.__spike.nav("https://text.npr.org/")')
  check('B2 live page loaded', nav1 === 'ok', String(nav1))
  const page = await attach('text.npr.org', 'npr page')
  const geo = await page.eval('({iw: innerWidth, ih: innerHeight, right: document.documentElement.getBoundingClientRect().right, dpr: devicePixelRatio})')
  check('B3 page viewport == pane width (no clip/fake-width)',
    Math.abs(geo.iw - st1.pageBounds.width) <= 2,
    `innerWidth=${geo.iw} paneWidth=${st1.pageBounds.width} dpr=${geo.dpr}`)
  let reflow = { cols: 0 }, ptyd = { cols: 0 }
  for (let i = 0; i < 20; i++) {
    reflow = await term.eval('window.__dims()')
    ptyd = (await term.eval('window.__spike.state()')).ptyDims
    if (reflow.cols < 120 && ptyd.cols === reflow.cols) break
    await sleep(400)
  }
  check('B4 TUI truly reflowed (xterm cols shrank + pty told)',
    reflow.cols > 20 && reflow.cols < 120 && ptyd.cols === reflow.cols,
    `xterm cols=${reflow.cols} pty cols=${ptyd.cols}`)
  await sleep(800)
  shot('b-split-npr')
  page.close()

  // ---- C: focus assault — reader keeps every keystroke ----
  console.log('\n== C: focus assault (reader mode) ==')
  await term.eval(`window.__spike.nav("file://${dir}thief.html")`)
  const thief1 = await attach('thief.html', 'thief1')
  await sleep(1500) // let its focus-stealing interval run
  activateWindow()
  key('Up') // launcher → URL bar (pinned in tmux probe)
  await sleep(300)
  type('wm1')
  const c1 = await pollTermText(term, 'wm1', 6000)
  check('C1 keys land in TUI while thief steals focus', c1.ok)
  const alive1 = !(await term.eval('window.__termText()')).includes('[wirecopy exited')
  check('C1b TUI process still alive under assault', alive1)
  const stolen1 = await thief1.eval('window.__keys.length')
  check('C2 thief page received ZERO keys', stolen1 === 0, `page saw ${stolen1} keys`)
  // steal-on-load (electron#42578): navigate the pane while typing continues
  await term.eval(`window.__spike.nav("file://${dir}thief2.html")`)
  const thief2 = await attach('thief2.html', 'thief2')
  await sleep(1200)
  type('x7')
  const c3 = await pollTermText(term, 'wm1x7', 6000)
  check('C3 keys STILL land in TUI after pane navigation (steal-on-load beaten)', c3.ok)
  const stolen2 = await thief2.eval('window.__keys.length')
  check('C4 thief2 received ZERO keys', stolen2 === 0, `page saw ${stolen2} keys`)
  shot('c-focus-assault')
  thief1.close()

  // ---- D: deliberate browser mode — typing into the page works; Esc hands back ----
  console.log('\n== D: browser mode (deliberate interaction) ==')
  await term.eval('window.__spike.setMode("browser")')
  await sleep(500)
  type('user@example.com')
  await sleep(400)
  const val = await thief2.eval('document.getElementById("inp").value')
  check('D1 browser mode: credentials typing reaches the page input', val === 'user@example.com', `value="${val}"`)
  key('Escape')
  await sleep(500)
  type('k9')
  const d2 = await pollTermText(term, 'wm1x7k9', 6000)
  check('D2 Esc hands focus back to reader; keys flow to TUI again', d2.ok)
  const valAfter = await thief2.eval('document.getElementById("inp").value')
  check('D3 post-Esc keys did NOT leak into the page', valAfter === 'user@example.com', `value="${valAfter}"`)
  shot('d-browser-mode')
  thief2.close()

  // ---- E: .NET drives the embedded pane over CDP (Patchright ConnectOverCDPAsync) ----
  console.log('\n== E: .NET Patchright ConnectOverCDPAsync ==')
  const probeBin = process.env.PROBE_BIN
  if (probeBin) {
    const out = await new Promise(resolve => {
      execFile('/workspace/dotnet', ['exec', probeBin, PORT], { timeout: 120000 },
        (err, stdout, stderr) => resolve({ err, stdout: String(stdout), stderr: String(stderr) }))
    })
    console.log('      probe stdout: ' + out.stdout.trim().split('\n').join(' | '))
    if (out.err) console.log('      probe stderr: ' + out.stderr.slice(-600))
    check('E1 .NET ConnectOverCDPAsync drove the pane (title+eval)', !out.err && out.stdout.includes('MATH=42') && /TITLE=[^|]*NPR/i.test(out.stdout), out.stdout.trim().slice(0, 120))
    await sleep(600)
    shot('e-dotnet-drove-pane')
  } else {
    check('E1 .NET probe', false, 'PROBE_BIN not set')
  }

  // ---- F: real-world sites render unblocked (real engine, real headful window) ----
  console.log('\n== F: real-world sites ==')
  await term.eval('window.__spike.nav("https://www.nytimes.com/")').catch(() => {})
  await sleep(9000)
  const nyt = await attach('nytimes.com', 'nyt').catch(() => null)
  if (nyt) {
    const sniff = await nyt.eval(`({title: document.title, links: document.querySelectorAll('a').length,
      blocked: /just a moment|verify you are human|access denied|unusual activity/i.test(document.body ? document.body.innerText.slice(0,3000) : '')})`).catch(e => ({ title: 'EVALFAIL', links: 0, blocked: true }))
    check('F1 nytimes.com renders (not bot-walled)', sniff.links > 30 && !sniff.blocked, `title="${sniff.title}" links=${sniff.links}`)
    nyt.close()
  } else check('F1 nytimes.com renders', false, 'no target')
  shot('f-nytimes')

  await term.eval('window.__spike.nav("https://accounts.google.com/")').catch(() => {})
  await sleep(7000)
  const goog = await attach('google.com', 'google').catch(() => null)
  if (goog) {
    const g = await goog.eval(`({email: !!document.querySelector('input[type=email], input#identifierId, input[name=identifier], input[autocomplete*=username]')
        || /use your google account|email or phone/i.test(document.body ? document.body.innerText.slice(0,4000) : ''),
      title: document.title,
      insecure: /not be secure|couldn.t sign you in/i.test(document.body ? document.body.innerText.slice(0,4000) : '')})`).catch(e => ({ email: false, title: 'EVALFAIL', insecure: true }))
    check('F2 Google sign-in form renders (no "not secure" wall)', g.email && !g.insecure, `title="${g.title}"`)
    goog.close()
  } else check('F2 google sign-in renders', false, 'no target')
  shot('g-google')

  term.close()
} catch (err) {
  console.error('GATE ERROR:', err)
  failEarly = true
  try { shot('z-error') } catch {}
}

const fails = results.filter(r => !r.pass).length + (failEarly ? 1 : 0)
console.log(`\n=== ${results.filter(r => r.pass).length}/${results.length} checks passed${failEarly ? ' (gate aborted early)' : ''} ===`)
writeFileSync(`${OUT}results.json`, JSON.stringify(results, null, 2))
process.exit(fails)
