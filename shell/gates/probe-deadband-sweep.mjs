// Diagnostic sweep (not a gate): drives the REAL window through a range of heights and
// reports, for BOTH panes, how much of the pane is dead space below the last card row.
// Headful under Xvfb. Run from shell/: node gates/probe-deadband-sweep.mjs
import http from 'node:http'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, pollTermText, sleep, buildTui } from './lib.mjs'

const FIX_PORT = 8232
const links = Array.from({ length: 30 }, (_, i) =>
  `<li><a href="/story${i + 1}">STORY NUMBER ${i + 1} - a headline of reasonable length</a></li>`).join('\n')
const fixture = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/html' })
  if (req.url === '/') res.end(`<!doctype html><html><head><title>SWEEP FIXTURE</title></head><body><h1>Sweep Fixture</h1><ul>${links}</ul></body></html>`)
  else res.end(`<!doctype html><html><head><title>S</title></head><body><h1>Story</h1><p>${'Body. '.repeat(200)}</p></body></html>`)
})
await new Promise(r => fixture.listen(FIX_PORT, '127.0.0.1', r))

buildTui('Release')

const env = new Env({ display: ':85', cdpPort: 9295, screen: '1920x1300x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [
    { name: 'SWEEP FIXTURE', url: `http://127.0.0.1:${FIX_PORT}/` },
    ...Array.from({ length: 15 }, (_, i) => ({ name: `SITE ${i + 2}`, url: `http://127.0.0.1:${FIX_PORT}/s${i}` }))
  ]
}, null, 2))

await env.up()
const shell = env.launchShell()

const MEASURE = `(function(){
  const q=s=>document.querySelector(s);
  const g=document.getElementById('grid'), col=document.getElementById('column'), f=q('#footer');
  if(!g||!col||!f) return null;
  const cards=[...g.children].filter(c=>!/\\bempty\\b/.test(c.className));
  const gb=g.getBoundingClientRect(), fb=f.getBoundingClientRect();
  const lastBottom = cards.length?Math.max(...cards.map(c=>c.getBoundingClientRect().bottom)):gb.top;
  const rows = new Set(cards.map(c=>Math.round(c.getBoundingClientRect().top))).size;
  return {
    winH: window.innerHeight, winW: window.innerWidth,
    gridTop: Math.round(gb.top), gridH: Math.round(gb.height),
    footerTop: Math.round(fb.top), footerBottom: Math.round(fb.bottom),
    cards: cards.length, rows,
    cardH: cards.length?Math.round(cards[0].getBoundingClientRect().height):0,
    lastCardBottom: Math.round(lastBottom),
    deadBandPx: Math.round(fb.top - lastBottom),
    belowFooterPx: Math.round(window.innerHeight - fb.bottom),
    chromePx: Math.round(col.getBoundingClientRect().height - gb.height),
    colH: Math.round(col.getBoundingClientRect().height)
  };
})()`

const rowsFor = p => p ? `${p.rows}r x ${p.cardH}px` : '-'

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  for (let i = 0; i < 100; i++) { const t = await term.eval('window.__termText?window.__termText():""').catch(() => ''); if (t.trim().length > 20) break; await sleep(400) }
  const wid = env.activate()

  const lau = await attach(env.cdpPort, 'launcher.html', 'launcher')
  await sleep(1200)

  console.log('\n### LAUNCHER — dead band vs window height')
  console.log('win h x w | grid top/h | rows | last card | footer top | DEAD BAND | below footer')
  for (const h of [760, 820, 880, 940, 1000, 1060, 1120, 1180]) {
    env.x('windowsize', wid, '1600', String(h))
    await sleep(1400)
    const m = await lau.eval(MEASURE)
    if (!m) { console.log(`  ${h}: no measurement`); continue }
    console.log(`  ${String(m.winH).padStart(4)}x${String(m.winW).padStart(4)} | ${String(m.gridTop).padStart(4)}/${String(m.gridH).padStart(4)} | ${rowsFor(m).padEnd(12)} | ${String(m.lastCardBottom).padStart(4)} | ${String(m.footerTop).padStart(4)} | ${String(m.deadBandPx).padStart(4)}px | ${String(m.belowFooterPx).padStart(3)}px  chrome=${m.chromePx}`)
  }

  // Reach the link tree.
  env.x('windowsize', wid, '1600', '1000')
  await sleep(1200)
  env.activate()
  env.key('Return')
  const ok = await pollTermText(term, 'STORY NUMBER', 40000)
  console.log('\nlink list reached:', ok.ok)
  await sleep(2500)
  const lt = await attach(env.cdpPort, 'linktree.html', 'linktree')

  console.log('\n### LINK TREE (sidecar docked) — dead band vs window height')
  console.log('win h x w | grid top/h | rows | last card | footer top | DEAD BAND | below footer')
  for (const h of [760, 820, 880, 940, 1000, 1060, 1120, 1180]) {
    env.x('windowsize', wid, '1600', String(h))
    await sleep(1600)
    const m = await lt.eval(MEASURE)
    if (!m) { console.log(`  ${h}: no measurement`); continue }
    console.log(`  ${String(m.winH).padStart(4)}x${String(m.winW).padStart(4)} | ${String(m.gridTop).padStart(4)}/${String(m.gridH).padStart(4)} | ${rowsFor(m).padEnd(12)} | ${String(m.lastCardBottom).padStart(4)} | ${String(m.footerTop).padStart(4)} | ${String(m.deadBandPx).padStart(4)}px | ${String(m.belowFooterPx).padStart(3)}px  chrome=${m.chromePx}`)
  }

  // Undocked link tree (full width) — press | to hide the live page.
  env.activate()
  env.key('bar')
  await sleep(2000)
  console.log('\n### LINK TREE (sidecar hidden, full width) — dead band vs window height')
  for (const h of [820, 940, 1060, 1180]) {
    env.x('windowsize', wid, '1600', String(h))
    await sleep(1600)
    const m = await lt.eval(MEASURE)
    if (!m) { console.log(`  ${h}: no measurement`); continue }
    console.log(`  ${String(m.winH).padStart(4)}x${String(m.winW).padStart(4)} | ${String(m.gridTop).padStart(4)}/${String(m.gridH).padStart(4)} | ${rowsFor(m).padEnd(12)} | ${String(m.lastCardBottom).padStart(4)} | ${String(m.footerTop).padStart(4)} | ${String(m.deadBandPx).padStart(4)}px | ${String(m.belowFooterPx).padStart(3)}px  chrome=${m.chromePx}`)
  }
  env.x('windowsize', wid, '1600', '1000')
  await sleep(1500)
  env.shotWindow('sweep-linktree-1000', wid)
} catch (e) {
  console.error('SWEEP ERROR:', e)
  console.error(shell.stderrText.slice(-3000))
} finally {
  env.down(); fixture.close()
}
process.exit(0)
