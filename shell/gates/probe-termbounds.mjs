// Diagnostic probe: does the MAIN process actually resize termView while the launcher
// pane covers it? Reads wc.state().termBounds (main process truth) against term.html's
// own window.innerWidth/innerHeight (renderer truth) at each window size, in both modes.
// Run from shell/: node gates/probe-termbounds.mjs
import http from 'node:http'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, sleep, buildTui } from './lib.mjs'

const FIX_PORT = 8234
const links = Array.from({ length: 30 }, (_, i) => `<li><a href="/s${i}">STORY ${i + 1} headline of reasonable length</a></li>`).join('')
const fixture = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/html' })
  res.end(req.url === '/'
    ? `<!doctype html><html><head><title>TB</title></head><body><h1>TB</h1><ul>${links}</ul></body></html>`
    : '<!doctype html><html><head><title>S</title></head><body><h1>S</h1></body></html>')
})
await new Promise(r => fixture.listen(FIX_PORT, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':88', cdpPort: 9298, screen: '1920x1300x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [{ name: 'TB FIXTURE', url: `http://127.0.0.1:${FIX_PORT}/` },
    ...Array.from({ length: 15 }, (_, i) => ({ name: `SITE ${i + 2}`, url: `http://127.0.0.1:${FIX_PORT}/x${i}` }))]
}, null, 2))
await env.up()
const shell = env.launchShell()

const JS = `(async function(){
  const s = await wc.state();
  return JSON.stringify({inner:[window.innerWidth,window.innerHeight], dims: window.__dims(),
    termBounds:s.termBounds, launcherBounds:s.launcherBounds, content:s.contentBounds,
    launcherShown:s.launcherShown, linkTreeShown:s.linkTreeShown, mode:s.mode, revealed:s.revealed});
})()`

async function run (label, term, wid, sizes) {
  console.log(`\n### ${label}`)
  for (const [w, h] of sizes) {
    env.x('windowsize', wid, String(w), String(h))
    await sleep(3500)
    const r = JSON.parse(await term.eval(JS))
    console.log(`  win ${w}x${h}`)
    console.log(`    main: termBounds=${JSON.stringify(r.termBounds)} launcherBounds=${JSON.stringify(r.launcherBounds)} content=${JSON.stringify(r.content)}`)
    console.log(`    main: launcherShown=${r.launcherShown} linkTreeShown=${r.linkTreeShown} mode=${r.mode} revealed=${r.revealed}`)
    console.log(`    renderer: term inner=${r.inner.join('x')}  pty dims=${r.dims.cols}x${r.dims.rows}`)
  }
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  for (let i = 0; i < 120; i++) { const t = await term.eval('window.__termText?window.__termText():""').catch(() => ''); if (t.trim().length > 20) break; await sleep(400) }
  const wid = env.activate()
  await sleep(1500)
  await run('LAUNCHER PANE UP', term, wid, [[1600, 1200], [1600, 900], [1300, 760]])

  env.x('windowsize', wid, '1600', '1000'); await sleep(1500)
  env.activate(); env.key('Return')
  console.log('\nlink list reached:', (await pollTermText(term, 'STORY', 40000)).ok)
  await sleep(2500)
  await run('LINK TREE PANE UP', term, wid, [[1600, 1200], [1600, 900], [1300, 760]])
} catch (e) {
  console.error('PROBE ERROR:', e); console.error(shell.stderrText.slice(-3000))
} finally { env.down(); fixture.close() }
process.exit(0)
