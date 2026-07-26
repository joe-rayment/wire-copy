// Diagnostic probe: WHERE does the window-resize chain break for the launcher pane?
// Chain: win.resize -> place() -> termView.setBounds -> term.html ResizeObserver ->
//        fit.fit() -> wc.resize -> ptyProc.resize -> TUI re-render -> pane state push.
// Reads, at each size and in BOTH launcher mode and link-tree mode:
//   termView bounds (main process), term.html window.innerW/H, term.html __dims() (cols/rows).
// Headful under Xvfb. Run from shell/: node gates/probe-resize-chain.mjs
import http from 'node:http'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, sleep, buildTui } from './lib.mjs'

const FIX_PORT = 8233
const links = Array.from({ length: 30 }, (_, i) =>
  `<li><a href="/s${i}">STORY NUMBER ${i + 1} - a headline of reasonable length</a></li>`).join('\n')
const fixture = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/html' })
  if (req.url === '/') res.end(`<!doctype html><html><head><title>CHAIN FIXTURE</title></head><body><h1>Chain Fixture</h1><ul>${links}</ul></body></html>`)
  else res.end('<!doctype html><html><head><title>S</title></head><body><h1>Story</h1></body></html>')
})
await new Promise(r => fixture.listen(FIX_PORT, '127.0.0.1', r))

buildTui('Release')

const env = new Env({ display: ':87', cdpPort: 9297, screen: '1920x1300x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [{ name: 'CHAIN FIXTURE', url: `http://127.0.0.1:${FIX_PORT}/` },
    ...Array.from({ length: 15 }, (_, i) => ({ name: `SITE ${i + 2}`, url: `http://127.0.0.1:${FIX_PORT}/x${i}` }))]
}, null, 2))

await env.up()
const shell = env.launchShell()

const TERM_JS = `JSON.stringify({inner:[window.innerWidth,window.innerHeight],
  dims: window.__dims?window.__dims():null,
  body:[document.body.clientWidth,document.body.clientHeight]})`

async function sample (label, term, lau, wid, heights) {
  console.log(`\n### ${label}`)
  for (const [w, h] of heights) {
    env.x('windowsize', wid, String(w), String(h))
    await sleep(3500)
    const t = JSON.parse(await term.eval(TERM_JS))
    let pane = null
    try {
      pane = await lau.eval(`(function(){const m=window.__launcherState?window.__launcherState():(window.__linkTreeState?window.__linkTreeState():null);
        const s=(m&&m.state)||{};const g=document.getElementById('grid');
        const cards=[...g.children].filter(c=>!/\\bempty\\b/.test(c.className));
        return {inner:[window.innerWidth,window.innerHeight], items:(s.slots||s.items||[]).length,
          rows:new Set(cards.map(c=>Math.round(c.getBoundingClientRect().top))).size};})()`)
    } catch {}
    console.log(`  win ${w}x${h} -> term inner ${t.inner.join('x')}  body ${t.body.join('x')}  pty dims ${t.dims ? t.dims.cols + 'x' + t.dims.rows : '?'}  | pane inner ${pane ? pane.inner.join('x') : '?'} items=${pane ? pane.items : '?'} rows=${pane ? pane.rows : '?'}`)
  }
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  for (let i = 0; i < 120; i++) { const t = await term.eval('window.__termText?window.__termText():""').catch(() => ''); if (t.trim().length > 20) break; await sleep(400) }
  const lau = await attach(env.cdpPort, 'launcher.html', 'launcher')
  const wid = env.activate()
  await sleep(1500)

  await sample('LAUNCHER PANE VISIBLE (covers termView)', term, lau, wid, [[1600, 1200], [1600, 900], [1300, 760], [1600, 1150]])

  // Go to the link tree.
  env.x('windowsize', wid, '1600', '1000'); await sleep(1500)
  env.activate(); env.key('Return')
  const ok = await pollTermText(term, 'STORY NUMBER', 40000)
  console.log('\nlink list reached:', ok.ok)
  await sleep(2500)
  const lt = await attach(env.cdpPort, 'linktree.html', 'linktree')
  await sample('LINK TREE PANE VISIBLE (covers termView)', term, lt, wid, [[1600, 1200], [1600, 900], [1300, 760], [1600, 1150]])
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-3000))
} finally { env.down(); fixture.close() }
process.exit(0)
