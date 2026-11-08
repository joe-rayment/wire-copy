// How long is the screen blank between Enter on a site and its story list painting —
// and what is on screen meanwhile? Boots headful, opens ONE real site from the launcher
// (the user's action: Enter on a tile), samples the terminal text, the link-tree pane's
// state and window shots on a timeline, then prints the app log lines from that window.
//
// Run from shell/: node gates/probe-load-timing.mjs [url] [--no-build] [--reuse <xdg>]
import fs from 'node:fs'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const argv = process.argv.slice(2)
const url = argv.find(a => /^https?:/.test(a)) || 'https://www.theguardian.com/international'
const reuse = argv.includes('--reuse') ? argv[argv.indexOf('--reuse') + 1] : null
const tag = argv.includes('--tag') ? argv[argv.indexOf('--tag') + 1] : 'load'
if (!argv.includes('--no-build')) buildTui('Release')

const env = new Env({
  display: ':93',
  cdpPort: 9313,
  screen: '1500x950x24',
  isolateData: !reuse,
  env: reuse ? { XDG_DATA_HOME: reuse, WIRECOPY_SHELL_USERDATA: path.join(reuse, '..', 'electron') } : {}
})
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
fs.mkdirSync(dataDir, { recursive: true })
fs.writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1, bookmarks: [{ name: 'PROBE SITE', url }]
}, null, 2))
console.log('data dir:', env.extraEnv.XDG_DATA_HOME, ' url:', url)

const logDir = path.join(ROOT, 'logs')
const appLog = () => {
  const f = fs.readdirSync(logDir).filter(x => /^wirecopy-.*\.log$/.test(x)).sort()
  return f.length ? path.join(logDir, f[f.length - 1]) : null
}
const readFrom = (f, off) => {
  const size = fs.statSync(f).size
  if (size <= off) return ''
  const fd = fs.openSync(f, 'r')
  const buf = Buffer.alloc(size - off)
  fs.readSync(fd, buf, 0, buf.length, off)
  fs.closeSync(fd)
  return buf.toString('utf8')
}

if (process.env.XVFB_PRELOAD) {
  // Bare container: plain Xvfb dies without the xkbcomp shim (bd memory xvfb-needs-shim).
  env.up = async function () {
    this.xvfb = spawn('Xvfb', [this.display, '-screen', '0', this.screen, '-xkbdir', process.env.XVFB_XKBDIR],
      { stdio: 'ignore', env: { ...process.env, LD_PRELOAD: process.env.XVFB_PRELOAD } })
    this.procs.push(this.xvfb)
    await sleep(1200)
    this.wm = spawn('openbox', [], { stdio: 'ignore', env: { ...process.env, DISPLAY: this.display } })
    this.procs.push(this.wm)
    await sleep(700)
  }
}
await env.up()
const shell = env.launchShell()
try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  const launcher = await pollTermText(term, 'Go to URL', 40000)
  console.log('launcher painted:', launcher.ok)
  const wid = env.activate()
  await sleep(2500)
  const f = appLog()
  const off = f ? fs.statSync(f).size : 0

  let lt = null
  const ltState = async () => {
    try {
      if (!lt) {
        const t = await findTarget(env.cdpPort, x => x.url.includes('linktree.html'), 300, 'link-tree pane')
        lt = await Cdp.connect(t.webSocketDebuggerUrl)
      }
      return JSON.parse(await lt.eval('JSON.stringify(window.__linkTreeState ? (window.__linkTreeState()||{}) : {})'))
    } catch { return null }
  }

  const t0 = Date.now()
  env.key('Return')
  const shots = new Set([1, 3, 6, 10, 15, 20, 30])
  let lastLine = ''
  let painted = null
  while (Date.now() - t0 < 90000) {
    const s = (Date.now() - t0) / 1000
    const txt = (await term.eval('window.__termText()').catch(() => '')).replace(/\s+/g, ' ')
    const st = await ltState()
    const items = st && st.state && st.state.items ? st.state.items.length : 0
    const line = `items=${items} term="${txt.slice(0, 150)}"`
    if (line !== lastLine) { console.log(`  t+${s.toFixed(1)}s ${line}`); lastLine = line }
    for (const k of [...shots]) if (s >= k) { env.shotWindow(`${tag}-t${k}`, wid); shots.delete(k) }
    if (items > 0 && painted == null) { painted = s; console.log(`  >>> link tree populated at t+${s.toFixed(1)}s`) }
    if (painted != null && s - painted > 2) break
    await sleep(250)
  }
  env.shotWindow(`${tag}-final`, wid)
  console.log(`\nRESULT  Enter -> story list: ${painted == null ? 'NEVER (90s)' : painted.toFixed(1) + 's'}`)
  if (f) {
    const fresh = readFrom(f, off).split('\n').filter(Boolean)
    console.log(`\n--- app log during load (${fresh.length} lines) ---`)
    for (const l of fresh) console.log('  ' + l.slice(0, 260))
  }
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-2000))
} finally { env.down() }
process.exit(0)
