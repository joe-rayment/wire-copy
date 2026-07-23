// D4 gate (workspace-zq6y): the launcher selected-tile highlight must span the full
// tile width — repro matrix 3 window widths x deviceScaleFactor {1,2}, per the bead.
// Three layers per point, all on the REAL selected Reading List tile (selection moved
// by an OS-level Right keypress):
//   CELL truth  — xterm buffer API: count of cells painted with the selection bg on the
//                 title row must equal the tile's rule width (the ─ separator two rows
//                 below, an independent draw path).
//   PIXEL truth — the green bar's right edge must reach the rule's right edge (≤1 cell),
//                 and the tile's top padding row must be SEAM-FREE solid fill (catches
//                 per-cell rounding gaps at devicePixelRatio 2 — the retina stripe class).
//   Screenshots — written per point for the human read.
// Run from shell/: node gates/gate-d4-highlight.mjs
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import { Env, OUT, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

const PAD_L = 16
const PAD_T = 16

// Crop one pixel row from a window capture and return [{x, r, g, b}].
function pixelRow (env, wid, y, width, dsf, tag) {
  const png = path.join(OUT, `d4-row-${tag}.png`)
  execFileSync('import', ['-display', env.display, '-window', wid, png])
  const txt = execFileSync('convert', [png, '-crop', `${Math.round(width * dsf)}x1+0+${Math.round(y * dsf)}`, 'txt:-']).toString()
  const px = []
  for (const line of txt.split('\n')) {
    const m = line.match(/^(\d+),\d+: \((\d+),(\d+),(\d+)/)
    if (m) px.push({ x: +m[1], r: +m[2], g: +m[3], b: +m[4] })
  }
  return px
}
const near = (a, b, tol) => Math.abs(a.r - b.r) <= tol && Math.abs(a.g - b.g) <= tol && Math.abs(a.b - b.b) <= tol

async function measurePoint (env, term, label, dsf) {
  const dims = await term.eval('window.__dims()')
  const cell = await term.eval('(() => { const d = term._core._renderService.dimensions.css.cell; return { w: d.width, h: d.height } })()')
  // Search the LIVE VIEWPORT only: __termText spans the whole buffer including
  // scrollback, and a pre-resize frame can scroll a stale (wider) copy of the
  // launcher into scrollback — findIndex would then measure that dead row
  // (bit with the 5-row card stride, workspace-7t0a.2). allLines keeps buffer
  // indexing so getLine(r) below still addresses the same row.
  const allLines = (await term.eval('window.__termText()')).split('\n')
  const base = Math.max(0, allLines.length - dims.rows)
  const lines = allLines
  const rVp = allLines.slice(base).findIndex(l => l.includes('READING LIST'))
  const r = rVp < 0 ? -1 : base + rVp
  check(`${label}: READING LIST row found`, r > 0, `row=${r} cols=${dims.cols}`)
  if (r <= 0) return

  // Tile geometry from the buffer: the name starts two cells after the tile's left
  // edge (▌ + space); the ★ trails the name (workspace-7t0a.2). The separator rule
  // is the card's LAST line — 5-row stride puts it at title+3 (workspace-stby), so
  // scan the next few rows for the first ─ run instead of hardcoding an offset.
  const nameCol = lines[r].indexOf('READING LIST')
  const c0 = Math.max(0, nameCol - 2)
  // Clamp the scan to the LIVE columns: after a narrowing resize the buffer rows
  // keep their old width, with stale pre-resize glyphs right of dims.cols.
  let ruleStart = -1; let ruleEnd = -1
  for (let dr = 1; dr <= 4 && ruleStart < 0; dr++) {
    const ruleLine = (lines[r + dr] || '').slice(0, dims.cols)
    for (let c = c0; c < ruleLine.length; c++) {
      if (ruleLine[c] === '─') { if (ruleStart < 0) ruleStart = c; ruleEnd = c } else if (ruleLine[c] === '┼') { if (ruleStart >= 0) break } else if (ruleStart >= 0 && c > ruleEnd + 1) break
    }
  }
  check(`${label}: tile rule row found`, ruleStart >= 0 && ruleEnd > ruleStart, `rule ${ruleStart}..${ruleEnd}`)
  if (ruleStart < 0) return
  const tileCells = ruleEnd - ruleStart + 1

  // CELL truth: bg runs on the title row read straight from the xterm buffer.
  const cellScan = await term.eval(`(() => {
    const line = term.buffer.active.getLine(${r})
    const probe = line.getCell(${c0 + 3})
    const mode = probe.getBgColorMode(), color = probe.getBgColor()
    let first = -1, last = -1
    for (let c = 0; c < term.cols; c++) {
      const cl = line.getCell(c)
      if (cl && cl.getBgColorMode() === mode && cl.getBgColor() === color) { if (first < 0) first = c; last = c }
    }
    return { mode, color, first, last }
  })()`)
  const barCells = cellScan.last - cellScan.first + 1
  check(`${label}: CELL truth — bar cells == tile rule cells`, Math.abs(barCells - tileCells) <= 1,
    `bar=${barCells} (${cellScan.first}..${cellScan.last}) rule=${tileCells} (${ruleStart}..${ruleEnd}) bgMode=${cellScan.mode}`)

  // PIXEL truth. Sample the selection bg from the PADDING row (pure fill — the title
  // row has glyph pixels: sampling there once grabbed the cyan ★ and measured cyan).
  const wid = env.activate()
  const stateW = (await term.eval('window.__wc.state()')).contentSize[0]
  const yTitle = PAD_T + (r + 0.5) * cell.h
  const yPad = PAD_T + (r - 0.5) * cell.h
  const rowP0 = pixelRow(env, wid, yPad, stateW, dsf, `${label}-pad`)
  const sample = rowP0[Math.round((PAD_L + (c0 + 4.5) * cell.w) * dsf)]
  check(`${label}: bg sample is a plausible fill color (greenish)`,
    sample && sample.g > 40 && sample.g >= sample.r && sample.g >= sample.b,
    JSON.stringify(sample))
  const rowT = pixelRow(env, wid, yTitle, stateW, dsf, `${label}-title`)
  const ruleRightPx = (PAD_L + (ruleEnd + 1) * cell.w) * dsf
  const ruleLeftPx = (PAD_L + ruleStart * cell.w) * dsf
  // Measure within the tile's own region — the NEIGHBOR tile's green text antialiasing
  // matches the fill color and would otherwise drag barMin outside the tile.
  const inTile = p => p.x >= ruleLeftPx - cell.w * dsf && p.x <= ruleRightPx + cell.w * dsf
  const barPx = rowT.filter(p => inTile(p) && near(p, sample, 12)).map(p => p.x)
  const barMin = Math.min(...barPx); const barMax = Math.max(...barPx)
  check(`${label}: PIXEL truth — bar reaches the tile's right edge`,
    barMax >= ruleRightPx - cell.w * dsf && barMax <= ruleRightPx + cell.w * dsf,
    `barMax=${barMax} ruleRight=${Math.round(ruleRightPx)} cellPx=${(cell.w * dsf).toFixed(1)}`)
  check(`${label}: PIXEL truth — bar starts at the tile's left edge`,
    barMin <= ruleLeftPx + 2 * cell.w * dsf, `barMin=${barMin} ruleLeft=${Math.round(ruleLeftPx)}`)

  // Seam check on the pure-fill padding row: every pixel inside the bar must be selBg.
  // Window starts past the ▌ accent-bar cell — its glyph edge antialiases into the fill
  // at devicePixelRatio 2 (one blend pixel, not a seam).
  // Scan the tile's solid interior only: exclude the ▌ left edge (blend pixel) AND the
  // right column divider — a NON-last tile now has a │ divider at its right edge whose
  // thin glyph reads as background (workspace-ehon: Reading List is the middle column at
  // 3 columns). A genuine retina stripe fills the whole interior, so this still catches it.
  const seamRight = Math.min(barMax - 2, ruleRightPx - cell.w * dsf)
  const inBar = rowP0.filter(p => p.x >= ruleLeftPx + 2 * cell.w * dsf && p.x <= seamRight)
  const seams = inBar.filter(p => !near(p, sample, 12))
  check(`${label}: SEAM-FREE solid fill on the padding row`, seams.length === 0,
    seams.length ? `first seam @x=${seams[0].x} (${seams[0].r},${seams[0].g},${seams[0].b}) of ${inBar.length}px` : `${inBar.length}px solid`)
  execFileSync('import', ['-display', env.display, '-window', wid, path.join(OUT, `d4-${label}.png`)])
  console.log(`      shot: gates/out/d4-${label}.png`)
}

for (const cfg of [
  { label: 'w1360-dsf1', w: 1360, h: 850, dsf: 1 },
  { label: 'w1100-dsf1', w: 1100, h: 700, dsf: 1 },
  { label: 'w900-dsf1', w: 900, h: 640, dsf: 1 },
  { label: 'w1360-dsf2', w: 1360, h: 850, dsf: 2 }
]) {
  const env = new Env({ display: ':93', cdpPort: 9259 })
  await env.up()
  env.launchShell({}, cfg.dsf === 2 ? ['--force-device-scale-factor=2'] : [])
  try {
    const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
    check(`${cfg.label}: TUI booted`, (await pollTermText(term, 'READING LIST', 30000)).ok)
    const wid = env.activate()
    env.x('windowsize', wid, String(cfg.w), String(cfg.h))
    await sleep(1500) // refit + TUI reflow
    // Select the Reading List tile (virtual index 1). Responsive columns (workspace-ehon):
    // Right crosses a column when the grid has ≥2 columns; a narrow window (e.g. the dsf2
    // point renders ~66 cols → 1 column) has no column to cross, so Down steps to the next
    // stacked tile instead.
    const cols0 = (await term.eval('window.__dims()')).cols
    env.key(cols0 >= 80 ? 'Right' : 'Down') // Daily Gazette [1] → Reading List [2]
    await sleep(1200)
    const sel = await pollTermText(term, 'READING LIST', 5000)
    check(`${cfg.label}: launcher present after resize`, sel.ok)
    await measurePoint(env, term, cfg.label, cfg.dsf)
    term.close()
  } catch (err) {
    console.error('GATE ERROR:', err)
    check(`${cfg.label}: completed without error`, false, String(err))
  } finally {
    env.down()
    await sleep(500)
  }
}
process.exit(summary())
