// Shared harness for shell gates: fresh Xvfb + openbox + the shell, raw CDP, OS-level keys.
// Headful always (never headless); xdotool injects REAL X keys so focus routing is honest.
import { execFileSync, spawn } from 'node:child_process'
import { mkdirSync, mkdtempSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

export const SHELL_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
export const ROOT = path.resolve(SHELL_DIR, '..')
// Per-platform deps (shared repo dir across machines — see ../run): gates launch the
// Electron that matches THIS host.
export const DEPS_DIR = path.join(SHELL_DIR, 'deps', `${process.platform}-${process.arch}`)
export const OUT = path.join(SHELL_DIR, 'gates', 'out')
mkdirSync(OUT, { recursive: true })

export const sleep = ms => new Promise(r => setTimeout(r, ms))

const results = []
export function check (name, cond, detail = '') {
  results.push({ name, pass: !!cond, detail })
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`)
}
export function summary () {
  const fails = results.filter(r => !r.pass).length
  console.log(`\n=== ${results.length - fails}/${results.length} checks passed ===`)
  return fails
}

export function buildTui (config = 'Release') {
  execFileSync(path.join(ROOT, 'dotnet'),
    ['build', 'src/WireCopy.API/WireCopy.API.csproj', '-c', config, '--nologo', '-v', 'q'],
    { cwd: ROOT, stdio: 'inherit', timeout: 300000 })
}

export class Env {
  constructor ({ display, cdpPort, env = {}, isolateData = true }) {
    this.display = display
    this.cdpPort = String(cdpPort)
    // Fresh per-run state: app data (XDG) + Electron userData (browser partition), so
    // caches/scroll-restore/cookies from prior runs can't leak into asserts.
    this.extraEnv = { ...env }
    if (isolateData) {
      // Unique throwaway dir per run under /tmp — no pre-delete needed (avoid-rm-rf policy).
      const base = mkdtempSync(path.join(os.tmpdir(), `wc-gate-${this.cdpPort}-`))
      mkdirSync(path.join(base, 'xdg'), { recursive: true })
      mkdirSync(path.join(base, 'electron'), { recursive: true })
      this.extraEnv.XDG_DATA_HOME = path.join(base, 'xdg')
      this.extraEnv.WIRECOPY_SHELL_USERDATA = path.join(base, 'electron')
    }
    this.procs = []
  }

  async up () {
    try { execFileSync('bash', ['-c', `pgrep -f "[X]vfb ${this.display} " | xargs -r kill`]) } catch {}
    this.xvfb = spawn('Xvfb', [this.display, '-screen', '0', '1600x1000x24'], { stdio: 'ignore' })
    this.procs.push(this.xvfb)
    await sleep(1000)
    this.wm = spawn('openbox', [], { stdio: 'ignore', env: { ...process.env, DISPLAY: this.display } })
    this.procs.push(this.wm)
    await sleep(700)
  }

  launchShell (extra = {}, extraArgv = []) {
    // Sandbox flags must be argv (Linux sandbox init precedes main.js) — container-only.
    const p = spawn(path.join(DEPS_DIR, 'node_modules', '.bin', 'electron'),
      ['.', '--no-sandbox', '--disable-dev-shm-usage', ...extraArgv], {
      cwd: SHELL_DIR,
      stdio: ['ignore', 'pipe', 'pipe'],
      env: {
        ...process.env,
        DISPLAY: this.display,
        ELECTRON_DISABLE_SECURITY_WARNINGS: '1',
        WIRECOPY_SHELL_NO_SANDBOX: '1',
        WIRECOPY_SHELL_CDP_PORT: this.cdpPort,
        ...this.extraEnv,
        ...extra
      }
    })
    p.stderrText = ''
    p.stderr.on('data', d => { p.stderrText += d.toString('utf8') })
    p.stdout.on('data', () => {})
    this.procs.push(p)
    return p
  }

  down () {
    for (const p of this.procs.reverse()) { try { p.kill() } catch {} }
  }

  // --- OS-level input ---
  x (...args) { return execFileSync('xdotool', args, { env: { ...process.env, DISPLAY: this.display } }).toString() }
  activate (title = 'Wire Copy') {
    this.x('search', '--sync', '--name', title)
    const wid = this.x('search', '--name', title).trim().split('\n')[0]
    this.x('windowactivate', '--sync', wid)
    return wid
  }
  type (text) { this.x('type', '--delay', '95', text) }
  key (k) { this.x('key', '--delay', '60', k) }
  shot (name) {
    execFileSync('import', ['-display', this.display, '-window', 'root', path.join(OUT, name + '.png')])
    console.log(`      shot: gates/out/${name}.png`)
  }
}

// --- raw CDP ---
export async function targets (port) {
  const r = await fetch(`http://127.0.0.1:${port}/json`)
  return r.json()
}
export async function findTarget (port, pred, timeoutMs = 25000, label = '') {
  const t0 = Date.now()
  while (Date.now() - t0 < timeoutMs) {
    const list = await targets(port).catch(() => [])
    const hit = list.find(t => t.type === 'page' && pred(t))
    if (hit) return hit
    await sleep(300)
  }
  throw new Error(`target not found: ${label}`)
}
export class Cdp {
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
    if (r.exceptionDetails) throw new Error('eval failed: ' + (r.exceptionDetails.exception?.description || r.exceptionDetails.text))
    return r.result.value
  }
  close () { try { this.ws.close() } catch {} }
}
export async function attach (port, urlPart, label = urlPart) {
  const t = await findTarget(port, t => t.url.includes(urlPart), 25000, label)
  return Cdp.connect(t.webSocketDebuggerUrl)
}
// Matches on whitespace-NORMALIZED text: the TUI wraps lines at pane width, so a
// phrase can straddle a line break — content asserts must not depend on layout.
export async function pollTermText (term, substr, timeoutMs = 20000) {
  const t0 = Date.now()
  const want = substr.replace(/\s+/g, ' ')
  let txt = ''
  while (Date.now() - t0 < timeoutMs) {
    txt = await term.eval('window.__termText()').catch(() => '')
    if (txt.replace(/\s+/g, ' ').includes(want)) return { ok: true, txt }
    await sleep(400)
  }
  return { ok: false, txt }
}
