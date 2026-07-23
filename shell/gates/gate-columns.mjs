// Gate (workspace-21uy, supersedes the workspace-ehon responsive gate): every tile grid
// is ALWAYS exactly 2 columns wide, and the tiles are LARGE — they grow to fill the
// screen at ~4 rows (2×4 ≈ 8 tiles) with titles wrapping across multiple lines.
//
// Per the Verification Doctrine this asserts USER-VISIBLE OUTCOMES, not layout.Columns:
//   COUNT  — the RENDERED column count (┼ crosses on the tile rule row, +1) is 2 at
//            ~900 / ~1360 / ~2100 px windows — wide windows must NOT fan out to 3-5.
//   HEIGHT — the measured card stride (distance between consecutive rule rows) equals
//            max(5, available/4): tiles GROW with the window instead of stacking small.
//   FILL   — the tile rule spans essentially the whole width (no dead-space ribbon).
//   SPOTLIGHT — Right steps the highlighted tile column 0 → 1 and a further Right stays
//            at 1 (there is no column 2); the selection bg lands on the SELECTED column.
//   DOCKED — with the live-page pane revealed (the "browser sidecar" that narrows the
//            reader — the exact case that used to collapse the grid to 1 column), the
//            story list still renders 2 columns, long titles WRAP across lines, and
//            hiding/re-showing the pane (|) keeps the selection on the same story.
// Reads the VISIBLE viewport only (baseY-relative) — the full scrollback retains the wide
// pre-resize render and would be read as stale.
// Run from shell/: node gates/gate-columns.mjs
import fs from 'node:fs'
import path from 'node:path'
import http from 'node:http'
import { Env, attach, pollTermText, check, summary, buildTui, sleep } from './lib.mjs'

buildTui('Release')

// The current visible screen only (no scrollback), plus its base row and dims.
async function viewport (term) {
  return term.eval(`(() => {
    const b = term.buffer.active
    const lines = []
    for (let i = 0; i < term.rows; i++) { const l = b.getLine(b.baseY + i); lines.push(l ? l.translateToString(true) : '') }
    return { lines, baseY: b.baseY, cols: term.cols, rows: term.rows }
  })()`)
}

const crossPositions = ruleLine => {
  const xs = []
  for (let c = 0; c < ruleLine.length; c++) if (ruleLine[c] === '┼') xs.push(c)
  return xs
}

// All viewport rows that are tile separator rules (contain the ┼ column cross).
const ruleRows = vp => vp.lines.map((l, i) => (l.includes('┼') ? i : -1)).filter(i => i >= 0)

// Column of the selected tile on visible row `vpRow`: the longest run of cells whose bg
// differs from the page background (the selection fill), mapped through the ┼ dividers.
async function selectedColumn (term, baseY, vpRow, crosses) {
  const run = await term.eval(`(() => {
    const line = term.buffer.active.getLine(${baseY} + ${vpRow})
    if (!line) return { first: -1, last: -1 }
    const key = c => { const cl = line.getCell(c); return cl ? cl.getBgColorMode() + ':' + cl.getBgColor() : 'x' }
    const counts = {}
    for (let c = 0; c < term.cols; c++) { const k = key(c); counts[k] = (counts[k] || 0) + 1 }
    let bg = null, bgN = -1
    for (const k in counts) if (counts[k] > bgN) { bgN = counts[k]; bg = k }
    let bestF = -1, bestL = -1, f = -1
    for (let c = 0; c <= term.cols; c++) {
      const k = c < term.cols ? key(c) : bg
      if (k !== bg && f < 0) f = c
      else if (k === bg && f >= 0) { if ((c - 1 - f) > (bestL - bestF)) { bestF = f; bestL = c - 1 } f = -1 }
    }
    return { first: bestF, last: bestL }
  })()`)
  if (run.first < 0) return { col: -1, run }
  return { col: crosses.filter(x => x < run.first).length, run }
}

async function measureLauncher (env, term, label) {
  const vp = await viewport(term)
  const rules = ruleRows(vp)
  check(`${label}: grid rendered (at least one tile rule row)`, rules.length > 0, `rules=${rules.length}`)
  if (rules.length === 0) { env.shot(`columns-${label}`); return }

  // COUNT: exactly one ┼ per rule row ⟺ exactly 2 rendered columns, at EVERY width.
  const crossCounts = rules.map(r => crossPositions(vp.lines[r]).length)
  check(`${label}: rendered column count is 2 (1 ┼ per rule row) at ${vp.cols} cols`,
    crossCounts.every(n => n === 1), `crosses per rule row=[${crossCounts.join(',')}]`)

  // FILL: the rule spans the content width; the last column absorbs the remainder.
  const ruleLine = vp.lines[rules[0]]
  const ruleChars = [...ruleLine].map((ch, i) => ('─┼'.includes(ch) ? i : -1)).filter(i => i >= 0)
  check(`${label}: grid fills the width (no ribbon, last column flush)`,
    ruleChars[0] <= 2 && ruleChars[ruleChars.length - 1] >= vp.cols - 3,
    `rule ${ruleChars[0]}..${ruleChars[ruleChars.length - 1]} of ${vp.cols} cols`)

  // HEIGHT: the stride between consecutive rule rows is the card height. It must equal
  // max(5, floor(terminalRows/4)) — a tile is a quarter of the FULL screen tall
  // (workspace-1ogw: were the grid the only element, 2×4 = 8 tiles would fill it;
  // the header/URL bar reduce the visible count, never the tile size).
  if (rules.length >= 2) {
    const strides = rules.slice(1).map((r, i) => r - rules[i])
    const stride = strides[0]
    check(`${label}: uniform card stride`, strides.every(s => s === stride), `strides=[${strides.join(',')}]`)
    const expected = Math.max(5, Math.floor(vp.rows / 4))
    check(`${label}: tiles are a quarter of the screen — stride ${stride} == max(5, ${vp.rows}/4) = ${expected}`,
      stride === expected, `stride=${stride} expected=${expected} rows=${vp.rows}`)
    check(`${label}: no more than 4 tile rows on screen`, rules.length <= 4, `rows=${rules.length}`)
  } else {
    check(`${label}: expected at least 2 rule rows to measure the stride`, false, `rules=${rules.length}`)
  }

  env.shot(`columns-${label}`)
}

const SCREEN = '2600x1500x24'
for (const cfg of [
  { label: 'w900', w: 900, h: 760 },
  { label: 'w1360', w: 1360, h: 900, spotlight: true },
  { label: 'w2100', w: 2100, h: 1100 }
]) {
  const env = new Env({ display: ':95', cdpPort: 9271, screen: SCREEN })
  // Boot DIRECTLY at the target size by pre-seeding window-bounds — no resize, so no
  // pre-resize render lingers in scrollback to be misread. (main.js:59 maps userData
  // to WIRECOPY_SHELL_USERDATA; main.js:100 restores these bounds on launch.)
  fs.writeFileSync(path.join(env.extraEnv.WIRECOPY_SHELL_USERDATA, 'window-bounds.json'),
    JSON.stringify({ x: 0, y: 0, width: cfg.w, height: cfg.h }))
  await env.up()
  env.launchShell()
  try {
    const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
    // pollTermText is error-tolerant — it waits until the page's `term` + hooks exist.
    check(`${cfg.label}: TUI booted at ${cfg.w}px`, (await pollTermText(term, 'READING LIST', 30000)).ok)
    env.activate()
    await sleep(1200) // settle the first paint

    await measureLauncher(env, term, cfg.label)

    if (cfg.spotlight) {
      const vp = await viewport(term)
      const rules = ruleRows(vp)
      const crosses = crossPositions(vp.lines[rules[0]])
      const probeRow = rules[0] - 1 // any non-separator row of the first card carries the selection fill
      env.activate(); await sleep(200)
      const cols = []
      for (let k = 0; k <= 2; k++) {
        cols.push((await selectedColumn(term, vp.baseY, probeRow, crosses)).col)
        if (k < 2) { env.key('Right'); await sleep(700) }
      }
      check('spotlight: Right steps column 0 → 1, then stays at 1 (no third column)',
        cols[0] === 0 && cols[1] === 1 && cols[2] === 1, `cols=[${cols.join(',')}]`)
      env.shot('columns-spotlight')
    }
    term.close()
  } catch (err) {
    console.error('GATE ERROR:', err)
    check(`${cfg.label}: completed without error`, false, String(err))
    try { env.shot(`columns-${cfg.label}-error`) } catch {}
  } finally {
    env.down()
    await sleep(500)
  }
}

// ---- Story list (link tree): 2 columns docked AND undocked, wrapped titles, selection
// survives the pane toggle. The reader story list uses a DIFFERENT code path
// (LinkTreeGridMapper) than the launcher, and the auto-revealed live-page pane is the
// exact "browser sidecar narrows the terminal" case that used to collapse it to 1 column.
const FPORT = 8151
const link = (href, text) => `<li><a href="${href}">${text}</a></li>`
const WORDS = ['one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine']
const TAIL = 'carrying a long newsroom headline that must wrap across several tile lines instead of truncating away'
const fixture = http.createServer((req, res) => {
  res.setHeader('content-type', 'text/html; charset=utf-8')
  res.end(`<!doctype html><meta charset="utf-8"><title>Column Wire</title><body>
<h1>Column Wire</h1><section><h2>Dispatches</h2><ul>
${WORDS.map((w, i) => link(`/a${i + 1}.html`, `Dispatch ${w}: ${TAIL}`)).join('\n')}
</ul></section>`)
})
await new Promise(r => fixture.listen(FPORT, '127.0.0.1', r))
const listUrl = `http://127.0.0.1:${FPORT}/`

// Asserts the story grid is exactly 2 columns and titles wrap, in the CURRENT viewport.
async function measureStoryGrid (term, label) {
  const vp = await viewport(term)
  const rowOne = vp.lines.findIndex(l => l.includes('Dispatch one'))
  check(`${label}: story list on screen`, rowOne >= 0)
  if (rowOne < 0) return vp

  // Exactly 2 columns: Dispatch one + two share the first card row; three does NOT.
  const line = vp.lines[rowOne]
  check(`${label}: Dispatch one & two share a row (2 columns)`,
    line.includes('Dispatch two'), `line="${line.replace(/\s+/g, ' ').trim().slice(0, 90)}"`)
  check(`${label}: Dispatch three is NOT on that row (not 3+ columns)`,
    !line.includes('Dispatch three'), `line="${line.replace(/\s+/g, ' ').trim().slice(0, 90)}"`)
  const rowThree = vp.lines.findIndex(l => l.includes('Dispatch three'))
  check(`${label}: Dispatch three wrapped to the next tile row (grid did not collapse to 1 col)`,
    rowThree > rowOne, `rowOne=${rowOne} rowThree=${rowThree}`)

  // Wrapped title: the long headline continues on the line(s) below the title row.
  // Probe with a phrase from the MIDDLE of the tail ('across several') — it lands intact
  // on a continuation line at every tested width, while a phrase spanning a wrap point
  // would falsely fail.
  const continuation = vp.lines.slice(rowOne + 1, rowOne + 6).some(l => l.includes('across several'))
  check(`${label}: long titles wrap across multiple tile lines`, continuation,
    `following lines: "${vp.lines.slice(rowOne + 1, rowOne + 4).map(l => l.trim().slice(0, 40)).join(' | ')}"`)
  return vp
}

{
  const env = new Env({ display: ':95', cdpPort: 9271, screen: SCREEN })
  fs.writeFileSync(path.join(env.extraEnv.WIRECOPY_SHELL_USERDATA, 'window-bounds.json'),
    JSON.stringify({ x: 0, y: 0, width: 1360, height: 900 }))
  await env.up()
  env.launchShell()
  try {
    const term = await attach(env.cdpPort, 'term.html', 'terminal pane')
    check('story: TUI booted', (await pollTermText(term, 'READING LIST', 30000)).ok)
    env.activate(); await sleep(300)
    env.type('o'); await sleep(400); env.type(listUrl); await sleep(300); env.key('Return')
    check('story: link list rendered', (await pollTermText(term, 'Dispatch one', 40000)).ok)

    // D2 auto-reveals the pane on the first list — this IS the docked-sidecar state.
    // Wait for the split, then let the reveal animation + terminal reflow settle so the
    // reader is at its STABLE narrowed width before measuring.
    let st = null
    for (let i = 0; i < 40; i++) { st = await term.eval('window.__wc.state()'); if (st?.revealed) break; await sleep(500) }
    check('story: pane auto-revealed (reader narrowed by the docked sidecar)', !!st?.revealed)
    await sleep(2300)

    const vpDocked = await measureStoryGrid(term, 'docked')
    env.shot('columns-story-docked')

    // Move the selection to column 1 (Dispatch two) — the state CHANGE the doctrine
    // demands — then toggle the pane away and back and require the selection to hold.
    const rowOne = vpDocked.lines.findIndex(l => l.includes('Dispatch one'))
    const dividerCols = []
    for (let c = 0; c < (vpDocked.lines[rowOne] || '').length; c++) if (vpDocked.lines[rowOne][c] === '│') dividerCols.push(c)
    const c0 = (await selectedColumn(term, vpDocked.baseY, rowOne, dividerCols)).col
    env.key('Right'); await sleep(700)
    const c1 = (await selectedColumn(term, vpDocked.baseY, rowOne, dividerCols)).col
    check('story: Right moves selection to column 1 while docked', c0 === 0 && c1 === 1, `cols=[${c0},${c1}]`)

    // Undock (| hides the pane → reader reflows to FULL width): still 2 columns, wider
    // tiles, selection still on Dispatch two.
    env.key('bar'); await sleep(500)
    let stHid = null
    for (let i = 0; i < 20; i++) { stHid = await term.eval('window.__wc.state()'); if (!stHid?.revealed) break; await sleep(400) }
    check('story: | undocks the pane', stHid?.revealed === false)
    await sleep(2000)
    const vpFull = await measureStoryGrid(term, 'undocked-full-width')
    const rowOneF = vpFull.lines.findIndex(l => l.includes('Dispatch one'))
    const divF = []
    for (let c = 0; c < (vpFull.lines[rowOneF] || '').length; c++) if (vpFull.lines[rowOneF][c] === '│') divF.push(c)
    check('story: undock widened the tiles (divider moved right)',
      divF.length === 1 && dividerCols.length === 1 && divF[0] > dividerCols[0],
      `docked divider@${dividerCols[0]} undocked@${divF[0]}`)
    const cFull = (await selectedColumn(term, vpFull.baseY, rowOneF, divF)).col
    check('story: selection survived undock on the same story (column 1)', cFull === 1, `col=${cFull}`)
    env.shot('columns-story-undocked')

    // Re-dock: 2 columns again, selection still held.
    env.key('bar'); await sleep(500)
    let stBack = null
    for (let i = 0; i < 30; i++) { stBack = await term.eval('window.__wc.state()'); if (stBack?.revealed) break; await sleep(500) }
    check('story: | re-docks the pane', !!stBack?.revealed)
    await sleep(2000)
    const vpRe = await measureStoryGrid(term, 'redocked')
    const rowOneR = vpRe.lines.findIndex(l => l.includes('Dispatch one'))
    const divR = []
    for (let c = 0; c < (vpRe.lines[rowOneR] || '').length; c++) if (vpRe.lines[rowOneR][c] === '│') divR.push(c)
    const cRe = (await selectedColumn(term, vpRe.baseY, rowOneR, divR)).col
    check('story: selection survived re-dock on the same story (column 1)', cRe === 1, `col=${cRe}`)
    env.shot('columns-story-redocked')
    term.close()
  } catch (err) {
    console.error('GATE ERROR:', err)
    check('story: completed without error', false, String(err))
    try { env.shot('columns-story-error') } catch {}
  } finally {
    env.down()
    fixture.close()
    await sleep(500)
  }
}

process.exit(summary())
