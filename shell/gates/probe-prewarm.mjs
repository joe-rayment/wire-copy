// workspace-yiary: with the CDP attach moved off the first keypress, does the first
// navigation still freeze the input loop — and does anything pop up that should not?
//
// Boots, waits for the launcher, presses Enter on a bookmark and IMMEDIATELY presses
// another key, then checks three things this bead's acceptance names:
//   1. the second key registers (the loop was not eating input),
//   2. the app's own [loop-timing] for that navigation branch,
//   3. no extra top-level window appeared — the prewarm must be pane-neutral.
//
// Headful under Xvfb. Run from shell/: node gates/probe-prewarm.mjs
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui, ROOT } from './lib.mjs'

const FPORT = 8171
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (/^\/story\d+\.html/.test(req.url)) {
    res.end('<!doctype html><meta charset="utf-8"><title>Prewarm Story</title><body><article>' +
      '<h1>Prewarm story headline</h1><p>' + 'Body copy. '.repeat(200) + '</p></article>')
    return
  }
  res.end('<!doctype html><meta charset="utf-8"><title>Prewarm Wire</title><body><h1>Prewarm Wire</h1>' +
    '<section><h2>Front Desk</h2><ul>' +
    Array.from({ length: 6 }, (_, i) =>
      `<li><a href="/story${i + 1}.html">PREWARM STORY ${i + 1} — a headline long enough to wrap</a></li>`).join('\n') +
    '</ul></section>')
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':92', cdpPort: 9312, screen: '1500x950x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
fs.mkdirSync(dataDir, { recursive: true })
fs.writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [{ name: 'PREWARM WIRE', url: `http://127.0.0.1:${FPORT}/` }]
}, null, 2))

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

await env.up()
const shell = env.launchShell()

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  const launcher = await pollTermText(term, 'Go to URL', 40000)
  console.log('launcher painted:', launcher.ok)
  env.activate()
  await sleep(1500)

  // Windows before the first navigation. The prewarm attaches Playwright over CDP to the
  // window that is ALREADY open; if it ever launched a browser instead, a second
  // top-level window would appear, and that is the thing Program.cs's warmup ban is about.
  const windowsBefore = env.x('search', '--onlyvisible', '--name', '.+').trim().split('\n').filter(Boolean)
  const f = appLog()
  const off = fs.statSync(f).size

  // The user's sequence: Enter on the bookmark, then a second key straight away. The
  // second key is a plain selection move — NOT '?' or Escape, both of which were tried
  // and both of which change where you are, so the drive ends up somewhere the check
  // cannot read (Escape walked back to the launcher and the story list was never on
  // screen to be found).
  env.key('Return')
  await sleep(120)
  env.key('Down')

  const reached = await pollTermText(term, 'PREWARM STORY 1', 45000)
  console.log('the navigation itself completed:', reached.ok)
  await sleep(3500)

  // Did the key land? The link-tree pane publishes the selection the TUI is holding.
  let moved = null
  try {
    const t = await findTarget(env.cdpPort, x => x.url.includes('linktree.html'), 15000, 'link-tree pane')
    const lt = await Cdp.connect(t.webSocketDebuggerUrl)
    const st = JSON.parse(await lt.eval('JSON.stringify(window.__linkTreeState()||{})')).state || {}
    const items = st.items || []
    moved = { index: st.selectedIndex, title: (items[st.selectedIndex] || {}).title }
    lt.close()
  } catch (e) { moved = { err: String(e) } }
  console.log(`  the second key landed: selection is ${JSON.stringify(moved)}`)

  const fresh = readFrom(f, off)
  const branches = [...fresh.matchAll(/branch held the loop (\d+)ms \(input=true\)/g)].map(m => Number(m[1]))
  const prewarm = [...fresh.matchAll(/(prewarm|Browser prewarmed|AttachToShell)/gi)].length
  const windowsAfter = env.x('search', '--onlyvisible', '--name', '.+').trim().split('\n').filter(Boolean)

  console.log(`\n  [loop-timing] input branches during this navigation: ${JSON.stringify(branches)}ms`)
  console.log(`  worst branch: ${branches.length ? Math.max(...branches) : 'n/a'}ms   (acceptance: under 400ms)`)
  console.log(`  top-level windows before / after: ${windowsBefore.length} / ${windowsAfter.length}` +
    `  (acceptance: no extra window)`)
  console.log(`  prewarm mentions in this window of the log: ${prewarm}`)
  // Only THIS boot's lines — the log file is shared across runs.
  const pw = fresh.split('\n').filter(l => /prewarm/i.test(l))
  console.log('  prewarm log lines (this boot):\n' + (pw.length ? pw.map(l => '    ' + l.trim()).join('\n') : '    (none)'))
  const screen = await term.eval('window.__termText ? window.__termText() : ""')
  console.log('  terminal now: ' + screen.replace(/\s+/g, ' ').slice(0, 160))
  env.shotWindow('prewarm-final', env.activate())
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-2000))
} finally { env.down(); try { fixture.close() } catch {} }
process.exit(0)
