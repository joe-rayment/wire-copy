// Diagnostic probe: WHY does the termView renderer miss window resizes while the
// launcher pane covers it, when it does NOT miss them while the link-tree pane covers it?
// Tests the "renderer is treated as hidden" theory: reads document.visibilityState /
// document.hidden in term.html, and checks whether a stale size CATCHES UP the moment
// the launcher pane is hidden (mode -> browser).
// Run from shell/: node gates/probe-occlusion.mjs
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, sleep, buildTui } from './lib.mjs'

buildTui('Release')
const env = new Env({ display: ':89', cdpPort: 9299, screen: '1920x1300x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: Array.from({ length: 16 }, (_, i) => ({ name: `SITE ${i + 1}`, url: `http://127.0.0.1:9/${i}` }))
}, null, 2))
await env.up()
const shell = env.launchShell()

const SNAP = `(async function(){
  const s = await wc.state();
  return JSON.stringify({inner:[window.innerWidth,window.innerHeight], dims:window.__dims(),
    vis:document.visibilityState, hidden:document.hidden,
    termBounds:s.termBounds, launcherShown:s.launcherShown, mode:s.mode});
})()`

const show = r => `inner=${r.inner.join('x')} pty=${r.dims.cols}x${r.dims.rows} vis=${r.vis} hidden=${r.hidden} termBounds=${r.termBounds.width}x${r.termBounds.height} launcherShown=${r.launcherShown} mode=${r.mode}`

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  for (let i = 0; i < 120; i++) { const t = await term.eval('window.__termText?window.__termText():""').catch(() => ''); if (t.trim().length > 20) break; await sleep(400) }
  const wid = env.activate()
  await sleep(1500)

  console.log('\n1) baseline, launcher pane UP')
  env.x('windowsize', wid, '1600', '1200'); await sleep(3000)
  console.log('   ', show(JSON.parse(await term.eval(SNAP))))

  console.log('\n2) resize to 1600x820 with the launcher pane still UP')
  env.x('windowsize', wid, '1600', '820'); await sleep(3500)
  console.log('   ', show(JSON.parse(await term.eval(SNAP))))

  console.log('\n3) hide the launcher pane (setMode browser) — does the term catch up?')
  await term.eval('wc.setMode("browser")'); await sleep(2500)
  console.log('   ', show(JSON.parse(await term.eval(SNAP))))

  console.log('\n4) back to reader (launcher pane UP again)')
  await term.eval('wc.setMode("reader")'); await sleep(2500)
  console.log('   ', show(JSON.parse(await term.eval(SNAP))))

  console.log('\n5) resize to 1600x1150 with the launcher UP, then hide it again')
  env.x('windowsize', wid, '1600', '1150'); await sleep(3500)
  console.log('    before hide:', show(JSON.parse(await term.eval(SNAP))))
  await term.eval('wc.setMode("browser")'); await sleep(2500)
  console.log('    after  hide:', show(JSON.parse(await term.eval(SNAP))))
} catch (e) {
  console.error('PROBE ERROR:', e); console.error(shell.stderrText.slice(-3000))
} finally { env.down() }
process.exit(0)
