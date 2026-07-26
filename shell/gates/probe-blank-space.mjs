// Diagnostic probe (not a gate): launches the REAL shell headful under Xvfb and MEASURES
// the dead band the user reports at the bottom of the launcher pane and the link list.
// Reports, in px, exactly where the space goes: window height, #column height, the last
// card row's bottom edge, the footer's top edge, and the gap between them.
// Run from shell/: node gates/probe-blank-space.mjs
import http from 'node:http'
import { mkdirSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { Env, attach, findTarget, pollTermText, sleep, buildTui } from './lib.mjs'

const SCREEN = process.env.PROBE_SCREEN || '1680x1050x24'
const FIX_PORT = 8231

// Fixture site with a link list, so the link-tree pane has real rows.
const links = Array.from({ length: 15 }, (_, i) => `<li><a href="/story${i + 1}">STORY NUMBER ${i + 1} — a headline of reasonable length for a card</a></li>`).join('\n')
const fixture = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'text/html' })
  if (req.url === '/') {
    res.end(`<!doctype html><html><head><title>PROBE FIXTURE</title></head><body>
      <h1>Probe Fixture Front Page</h1><ul>${links}</ul></body></html>`)
  } else {
    res.end(`<!doctype html><html><head><title>Story ${req.url}</title></head><body>
      <h1>Story ${req.url}</h1><p>${'Body text. '.repeat(200)}</p></body></html>`)
  }
})
await new Promise(r => fixture.listen(FIX_PORT, '127.0.0.1', r))

buildTui('Release')

const env = new Env({ display: ':84', cdpPort: 9294, screen: SCREEN })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
mkdirSync(dataDir, { recursive: true })
writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: [
    { name: 'PROBE FIXTURE', url: `http://127.0.0.1:${FIX_PORT}/` },
    ...['second', 'third', 'fourth', 'fifth', 'sixth', 'seventh', 'eighth', 'ninth']
      .map(p => ({ name: `${p.toUpperCase()} SITE`, url: `http://127.0.0.1:${FIX_PORT}/${p}` }))
  ]
}, null, 2))

await env.up()
const shell = env.launchShell()

// Geometry read INSIDE the pane DOM. Works for both panes: both have #column,
// #grid and #footer.
const GEOM_JS = `(function(){
  const r = el => { if(!el) return null; const b=el.getBoundingClientRect();
    return {top:Math.round(b.top),bottom:Math.round(b.bottom),h:Math.round(b.height),w:Math.round(b.width)}; };
  const q = s => document.querySelector(s);
  const cards=[...document.querySelectorAll('#grid > *')].map(c=>{
    const b=c.getBoundingClientRect();
    return {cls:c.className, top:Math.round(b.top), bottom:Math.round(b.bottom), h:Math.round(b.height), w:Math.round(b.width),
      text:(c.textContent||'').trim().slice(0,40)};
  });
  const real = cards.filter(c=>!/\\bempty\\b/.test(c.cls));
  const lastBottom = real.length ? Math.max(...real.map(c=>c.bottom)) : null;
  const g=document.getElementById('grid');
  const col=document.getElementById('column');
  const cs = g ? getComputedStyle(g) : null;
  const colcs = col ? getComputedStyle(col) : null;
  return {
    innerH: window.innerHeight, innerW: window.innerWidth,
    docH: document.documentElement.scrollHeight,
    bodyH: document.body.scrollHeight,
    zoom: document.documentElement.style.zoom || '',
    stage: r(q('#stage')), column: r(col),
    columnScrollH: col ? Math.round(col.scrollHeight) : null,
    columnOverflowY: colcs ? colcs.overflowY : null,
    columnDisplay: colcs ? colcs.display : null,
    columnJustify: colcs ? colcs.justifyContent : null,
    columnPadBottom: colcs ? colcs.paddingBottom : null,
    grid: r(g), gridScrollH: g ? Math.round(g.scrollHeight) : null,
    gridOverflowY: cs ? cs.overflowY : null,
    gridCols: cs ? cs.gridTemplateColumns : null,
    gridRows: cs ? cs.gridTemplateRows : null,
    gridAlignContent: cs ? cs.alignContent : null,
    gridFlex: cs ? cs.flex : null,
    gridMinH: cs ? cs.minHeight : null,
    footer: r(q('#footer')),
    masthead: r(q('#masthead')),
    cardCount: cards.length, realCards: real.length,
    firstCard: cards[0] || null, lastRealBottom: lastBottom,
    gapLastCardToFooter: (lastBottom!=null && q('#footer')) ? Math.round(q('#footer').getBoundingClientRect().top - lastBottom) : null,
    gapFooterToWindow: q('#footer') ? Math.round(window.innerHeight - q('#footer').getBoundingClientRect().bottom) : null,
    cards
  };
})()`

function report (label, g) {
  console.log(`\n===== ${label} =====`)
  if (!g) { console.log('  (no geometry)'); return }
  const { cards, ...rest } = g
  for (const [k, v] of Object.entries(rest)) {
    console.log(`  ${k.padEnd(20)} ${typeof v === 'object' && v ? JSON.stringify(v) : v}`)
  }
  console.log('  cards:')
  for (const c of cards) console.log(`    y=${String(c.top).padStart(5)}..${String(c.bottom).padStart(5)} h=${String(c.h).padStart(4)} w=${String(c.w).padStart(4)} [${c.cls}] ${c.text}`)
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  await pollTermField(term)

  const wid = env.activate()
  // Report the real content bounds the shell gave the panes.
  const bounds = await term.eval(`(window.wcdev && wcdev.state) ? JSON.stringify(wcdev.state()) : 'no wcdev'`).catch(e => 'err: ' + e.message)
  console.log('\nshell state:', String(bounds).slice(0, 1200))

  // ---------- LAUNCHER ----------
  const lau = await attach(env.cdpPort, 'launcher.html', 'launcher pane')
  await sleep(1500)
  const lgeom = await lau.eval(GEOM_JS)
  report('LAUNCHER PANE', lgeom)
  const lstate = await lau.eval(`JSON.stringify((window.__launcherState()||{}).state||{})`)
  console.log('  pushed state:', String(lstate).slice(0, 900))
  env.shotWindow('probe-launcher', wid)

  // ---------- LINK TREE ----------
  // Open card 1 (the fixture front page) -> the TUI renders its link list.
  env.key('Return')
  const ok = await pollTermText(term, 'STORY NUMBER', 30000)
  console.log('\nlink list reached:', ok.ok)
  await sleep(2500)
  let lt = null
  try { lt = await attach(env.cdpPort, 'linktree.html', 'linktree pane') } catch (e) { console.log('linktree attach failed:', e.message) }
  if (lt) {
    const tgeom = await lt.eval(GEOM_JS)
    report('LINK TREE PANE', tgeom)
    const tstate = await lt.eval(`JSON.stringify((window.__linkTreeState?window.__linkTreeState():null)||{})`).catch(() => 'n/a')
    console.log('  pushed state:', String(tstate).slice(0, 1200))
  }
  env.shotWindow('probe-linktree', wid)
  await sleep(300)
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error('--- shell stderr tail ---')
  console.error(shell.stderrText.slice(-4000))
} finally {
  env.down()
  fixture.close()
}

async function pollTermField (term) {
  const t0 = Date.now()
  while (Date.now() - t0 < 40000) {
    const t = await term.eval('window.__termText ? window.__termText() : ""').catch(() => '')
    if (t && t.trim().length > 20) return
    await sleep(400)
  }
}
process.exit(0)
