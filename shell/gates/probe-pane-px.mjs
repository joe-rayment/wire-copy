// workspace-vt6o7: ONE probe that re-measures BOTH projected panes against the shared
// px model, so the two can never drift apart again the way their independently-derived
// constants did.
//
// For each of a width x height sweep, docked and undocked, it reads off the LIVE panes:
//   - the pane's real px size and the terminal dims the TUI is working from
//     (=> the measured px-per-cell and px-per-line, which PanePxModel.CellPx/LinePx claim)
//   - chrome = #column height - #grid height, the number ShellPaneChromePx models
//   - the card stride actually drawn (distance between consecutive card tops)
//   - how many card rows the pane DREW, which is what the C# row model must reproduce
// …and prints each beside the model's own prediction, with the error.
//
// Headful under Xvfb (never headless). Run from shell/: node gates/probe-pane-px.mjs
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { Env, attach, Cdp, findTarget, pollTermText, sleep, buildTui } from './lib.mjs'

const FPORT = 8167
const AUTHOR = 'Ada Lovelace'
const story = i =>
  `<li><a href="/a${i}.html">Dispatch ${i}: a headline sized for the responsive grid probe</a>` +
  ` <span class="byline">${AUTHOR}</span> <time datetime="2026-07-19T08:00:00Z">Jul 19</time></li>`
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  if (/^\/a\d+\.html/.test(req.url)) {
    res.end(`<!doctype html><meta charset="utf-8"><title>Story</title><body><article><h1>Story</h1>` +
      `<p>${'Readable body copy. '.repeat(40)}</p></article>`)
    return
  }
  res.end(`<!doctype html><meta charset="utf-8"><title>Column Wire</title><body>
<h1>Column Wire</h1>
<section><h2>Dispatches</h2><ul>${Array.from({ length: 24 }, (_, i) => story(i + 1)).join('\n')}</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))

buildTui('Release')
const env = new Env({ display: ':97', cdpPort: 9307, screen: '2400x1400x24' })
const dataDir = path.join(env.extraEnv.XDG_DATA_HOME, 'WireCopy')
fs.mkdirSync(dataDir, { recursive: true })
fs.writeFileSync(path.join(dataDir, 'bookmarks.json'), JSON.stringify({
  version: 1,
  bookmarks: Array.from({ length: 14 }, (_, i) => ({ name: `SITE ${i + 1}`, url: `http://127.0.0.1:${FPORT}/` }))
}, null, 2))
await env.up()
const shell = env.launchShell()

// The same three numbers off either pane: chrome, stride, rows drawn. #column minus
// #grid IS the chrome by construction — #grid is the only shrinkable child of #column in
// both stylesheets — so this measures the quantity the C# models rather than a proxy.
const MEASURE_JS = `(function(){
  const col=document.getElementById('column'), g=document.getElementById('grid');
  if(!col||!g) return null;
  const cr=col.getBoundingClientRect(), gr=g.getBoundingClientRect();
  const cards=[...g.querySelectorAll('.card')].filter(c=>!c.classList.contains('empty'));
  const tops=[...new Set(cards.map(c=>Math.round(c.getBoundingClientRect().top)))].sort((a,b)=>a-b);
  const heights=[...new Set(cards.map(c=>Math.round(c.getBoundingClientRect().height)))];
  return {
    paneW: window.innerWidth, paneH: window.innerHeight,
    colH: Math.round(cr.height), gridH: Math.round(gr.height),
    chrome: Math.round(cr.height - gr.height),
    rows: tops.length, cards: cards.length,
    stride: tops.length>1 ? tops[1]-tops[0] : null,
    cardH: heights,
    band: cards.length ? Math.round(gr.bottom - Math.max(...cards.map(c=>c.getBoundingClientRect().bottom))) : null
  };
})()`

// The model, mirrored in JS so the probe prints a PREDICTION beside every measurement.
// Kept deliberately literal — if these drift from the C# the probe is lying, which is
// exactly what a single shared module is meant to make obvious.
const CELL = 11.43
const LINE = 21.9
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v))
const model = {
  linktree: {
    chrome: w => clamp(144 + 0.108 * w, 233, 313),
    stride: w => clamp(0.162 * w, 165, 242) + 12,
    min: 177,
    max: 254
  },
  launcher: {
    chrome: (w, hint) => clamp(408 + 0.035 * w, 453, 467) + (hint ? 27 : 0),
    stride: () => 122,
    min: 122,
    max: 170
  }
}
function rowsFor (gridPx, stride, min, max) {
  if (gridPx < min || stride <= 0) return 1
  let r = Math.max(1, Math.round(gridPx / stride))
  while (r > 1 && gridPx / r < min) r--
  while (gridPx / r > max && gridPx / (r + 1) >= min) r++
  return r
}

const rows = []
function report (pane, dims, m, hint) {
  const w = dims.cols * CELL
  const gridPx = (dims.rows + 0.5) * LINE - model[pane].chrome(w, hint)
  const predRows = rowsFor(gridPx, model[pane].stride(w), model[pane].min, model[pane].max)
  const r = {
    pane,
    win: `${m.paneW}x${m.paneH}`,
    cells: `${dims.cols}x${dims.rows}`,
    cellPx: +(m.paneW / dims.cols).toFixed(2),
    linePx: +(m.paneH / dims.rows).toFixed(2),
    chrome: m.chrome,
    chromeModel: Math.round(model[pane].chrome(w, hint)),
    stride: m.stride,
    strideModel: Math.round(model[pane].stride(w)),
    rows: m.rows,
    rowsModel: predRows,
    cardH: m.cardH.join('/'),
    band: m.band
  }
  rows.push(r)
  console.log(
    `  ${pane.padEnd(9)} ${r.win.padEnd(10)} ${r.cells.padEnd(8)} ` +
    `cell ${String(r.cellPx).padEnd(6)}(${CELL})  line ${String(r.linePx).padEnd(6)}(${LINE})  ` +
    `chrome ${String(r.chrome).padEnd(4)}(${r.chromeModel}, err ${r.chrome - r.chromeModel})  ` +
    `rowH ${String(r.stride).padEnd(5)}(design prefers ${r.strideModel})  ` +
    `rows ${r.rows}(${r.rowsModel})  cardH ${r.cardH}  band ${r.band}`)
}

try {
  const term = await attach(env.cdpPort, 'term.html', 'terminal')
  await pollTermText(term, 'Go to URL', 40000)
  const wid = env.activate()
  await sleep(1200)

  const lauT = await findTarget(env.cdpPort, t => t.url.includes('launcher.html'), 20000, 'launcher pane')
  const lau = await Cdp.connect(lauT.webSocketDebuggerUrl)

  console.log('\n=== LAUNCHER (no sidecar; the pane spans the window) ===')
  for (const [w, h] of [[1100, 760], [1360, 900], [1600, 1000], [1900, 1180]]) {
    env.x('windowsize', wid, String(w), String(h))
    await sleep(4000)
    const dims = await term.eval('window.__dims()')
    const m = await lau.eval(MEASURE_JS)
    const st = JSON.parse(await lau.eval('JSON.stringify(window.__launcherState()||{})'))
    if (m) report('launcher', dims, m, !!(st.state && st.state.setupHint))
  }

  // Open a story list so the link-tree pane is up, then measure it at the same sizes —
  // docked (the sidecar is out, so the pane is the left split) and undocked.
  env.key('Return'); await sleep(600)
  await pollTermText(term, 'Dispatch 1', 45000)
  await sleep(4000)
  const ltT = await findTarget(env.cdpPort, t => t.url.includes('linktree.html'), 20000, 'link-tree pane')
  const lt = await Cdp.connect(ltT.webSocketDebuggerUrl)

  for (const docked of [true, false]) {
    if (!docked) { env.key('bar'); await sleep(2500) }
    console.log(`\n=== LINK TREE (sidecar ${docked ? 'docked — the pane is the left split' : 'hidden — full width'}) ===`)
    for (const [w, h] of [[1100, 760], [1360, 900], [1600, 1000], [1900, 1180]]) {
      env.x('windowsize', wid, String(w), String(h))
      await sleep(4000)
      const dims = await term.eval('window.__dims()')
      const m = await lt.eval(MEASURE_JS)
      if (m) report('linktree', dims, m, false)
    }
  }

  console.log('\n=== SUMMARY: model vs live pane ===')
  const err = (a, b) => Math.abs(a - b)
  const chromeErrs = rows.map(r => err(r.chrome, r.chromeModel))
  // NOT an error: strideModel is the height the design PREFERS at that width, while the
  // measured stride is what the rows actually got after sharing the grid (workspace-wp16o).
  // What must hold is that the measured height stays inside the pane's own bounds, which
  // the cardH column shows.
  const strideErrs = rows.filter(r => r.stride != null).map(r => err(r.stride, r.strideModel))
  const rowMisses = rows.filter(r => r.rows !== r.rowsModel)
  console.log(`  chrome error  max ${Math.max(...chromeErrs)}px, mean ${(chromeErrs.reduce((a, b) => a + b, 0) / chromeErrs.length).toFixed(1)}px`)
  console.log(`  rowH vs design-preferred stride: max delta ${Math.max(...strideErrs)}px (expected — rows share the grid)`)
  console.log(`  cell px       measured ${[...new Set(rows.map(r => r.cellPx))].join(', ')}  model ${CELL}`)
  console.log(`  line px       measured ${[...new Set(rows.map(r => r.linePx))].join(', ')}  model ${LINE}`)
  console.log(`  ROW COUNT     ${rows.length - rowMisses.length}/${rows.length} sizes reproduced by the shared model`)
  for (const r of rowMisses) console.log(`     MISS ${r.pane} ${r.win} (${r.cells}): drew ${r.rows}, model said ${r.rowsModel}`)
  console.log(`  dead band     ${rows.map(r => r.band).join(', ')}px`)
} catch (e) {
  console.error('PROBE ERROR:', e)
  console.error(shell.stderrText.slice(-3000))
} finally { env.down(); try { fixture.close() } catch {} }
process.exit(0)
