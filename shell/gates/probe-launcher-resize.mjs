// Diagnostic probe: does the LAUNCHER pane re-render when the window resizes?
// Separates "the pty never resized" from "the TUI resized but never re-pushed state".
// Reads the xterm dims (window.__dims) AND the pushed launcher paging at each size.
// Headful under Xvfb. Run from shell/: node gates/probe-launcher-resize.mjs
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, sleep, buildTui } from './lib.mjs'

buildTui('Release')

const env = new Env({ display: ':86', cdpPort: 9296, screen: '1920x1300x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: Array.from({ length: 16 }, (_, i) => ({ name: `SITE ${i + 1}`, url: `http://127.0.0.1:9/${i}` }))
}, null, 2))

await env.up()
const shell = env.launchShell()

const LAU_JS = `(function(){
  const g=document.getElementById('grid'), col=document.getElementById('column'), f=document.querySelector('#footer');
  const m=window.__launcherState(); const s=(m&&m.state)||{};
  const cards=[...g.children].filter(c=>!/\\bempty\\b/.test(c.className));
  const rows=new Set(cards.map(c=>Math.round(c.getBoundingClientRect().top))).size;
  const gb=g.getBoundingClientRect(), fb=f.getBoundingClientRect();
  return {winH:window.innerHeight, winW:window.innerWidth,
    paging:s.paging, columns:s.columns, slots:(s.slots||[]).length,
    rows, cardH: cards.length?Math.round(cards[0].getBoundingClientRect().height):0,
    gridTop:Math.round(gb.top), gridH:Math.round(gb.height),
    colH:Math.round(col.getBoundingClientRect().height),
    footerTop:Math.round(fb.top), footerBottom:Math.round(fb.bottom),
    deadBand:Math.round(fb.top-(cards.length?Math.max(...cards.map(c=>c.getBoundingClientRect().bottom)):gb.top)),
    overflowsBy: Math.round(Math.max(0, col.getBoundingClientRect().height - window.innerHeight))};
})()`

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  for (let i = 0; i < 120; i++) { const t = await term.eval('window.__termText?window.__termText():""').catch(() => ''); if (t.trim().length > 20) break; await sleep(400) }
  const lau = await attach(env.cdpPort, 'launcher.html', 'launcher')
  const wid = env.activate()
  await sleep(1500)

  for (const [w, h] of [[1600, 1200], [1600, 900], [1600, 1200], [1400, 760], [1600, 1050]]) {
    env.x('windowsize', wid, String(w), String(h))
    await sleep(4000)
    const dims = await term.eval('window.__dims?JSON.stringify(window.__dims()):"n/a"')
    const m = await lau.eval(LAU_JS)
    console.log(`\nrequested ${w}x${h}`)
    console.log(`  xterm dims        ${dims}`)
    console.log(`  pane inner        ${m.winW}x${m.winH}`)
    console.log(`  pushed paging     ${JSON.stringify(m.paging)}  columns=${m.columns} slots=${m.slots}`)
    console.log(`  rendered          ${m.rows} rows x ${m.cardH}px   grid top=${m.gridTop} h=${m.gridH}  column h=${m.colH}`)
    console.log(`  footer            ${m.footerTop}..${m.footerBottom}`)
    console.log(`  DEAD BAND         ${m.deadBand}px      column overflows window by ${m.overflowsBy}px`)
    env.shotWindow(`lresize-${w}x${h}`, wid)
  }
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-3000))
} finally { env.down() }
process.exit(0)
