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
//   META   — fixture stories carry author/date; the SELECTED card's meta line renders
//            white-on-fill (selection fg) and the treatment MOVES with the selection
//            (workspace-v654).
//   DOCKED — with the live-page pane revealed (the "browser sidecar" that narrows the
//            reader), the story list renders "Link Tree.dc.html"'s ONE wide column
//            (workspace-h2caq — the roomier clamp(420px,40vw,700px) track), opens out
//            to 2 when the pane is hidden, long titles WRAP across lines, and
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
// Lines are CLAMPED to term.cols: after a dock narrows the pty, xterm keeps the
// old wider line cells in its buffer beyond the new width — invisible on the
// canvas but returned by an unclamped translateToString, which made the gate
// "see" a third column that no user can (workspace-vingi diagnosis).
async function viewport (term) {
  return term.eval(`(() => {
    const b = term.buffer.active
    const lines = []
    for (let i = 0; i < term.rows; i++) { const l = b.getLine(b.baseY + i); lines.push(l ? l.translateToString(true, 0, term.cols) : '') }
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

  // FILL: since workspace-pn5f the launcher is a CENTRED column capped at
  // LauncherRenderer.ContentColumnWidth (84, Launcher.dc.html's column): narrow
  // terminals fill edge-to-edge as before; wide ones centre the capped column
  // with symmetric gutters instead of fanning tiles across the whole width.
  const CONTENT_COL = 84
  const ruleLine = vp.lines[rules[0]]
  const ruleChars = [...ruleLine].map((ch, i) => ('─┼'.includes(ch) ? i : -1)).filter(i => i >= 0)
  const first = ruleChars[0]
  const last = ruleChars[ruleChars.length - 1]
  const ruleWidth = last - first + 1
  const expectedW = Math.max(1, Math.min(vp.cols - 2, CONTENT_COL))
  const gutterSkew = Math.abs(first - (vp.cols - 1 - last))
  check(`${label}: grid is the centred capped column (width ~${expectedW}, symmetric gutters)`,
    ruleWidth >= expectedW - 2 && ruleWidth <= expectedW && gutterSkew <= 2,
    `rule ${first}..${last} (w=${ruleWidth}) of ${vp.cols} cols, expected ~${expectedW}, gutter skew ${gutterSkew}`)

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
      // Since workspace-8d02r launcher ARROWS move in the HTML pane's responsive
      // geometry (3 columns at 1360px), while this hidden terminal grid keeps its
      // fixed 2 columns: the second Right advances to index 2, which the terminal
      // projects on the NEXT row — so the fill leaves the probe row. The
      // user-visible arrow behaviour is proven on the pane itself in
      // gate-launcher-html H4c/H4d.
      check('spotlight: Right steps column 0 → 1, then advances beyond the probe row (pane-geometry nav)',
        cols[0] === 0 && cols[1] === 1 && cols[2] === -1, `cols=[${cols.join(',')}]`)
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
// Each story carries author/date meta (workspace-v654): a .byline span + <time datetime>
// inside the <li> container is exactly what LinkExtractor.ExtractLinkMetadata picks up,
// so the rendered cards get an "Ada Lovelace · <date>" meta line under the title — the
// line whose white-on-fill selected treatment the checks below drive and observe.
const AUTHOR = 'Ada Lovelace'
const link = (href, text) =>
  `<li><a href="${href}">${text}</a> <span class="byline">${AUTHOR}</span> <time datetime="2026-07-19T08:00:00Z">Jul 19</time></li>`
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

// Foreground color key of one cell (mode:color), for the meta white-on-fill checks.
async function fgAt (term, baseY, vpRow, col) {
  return term.eval(`(() => {
    const line = term.buffer.active.getLine(${baseY} + ${vpRow})
    if (!line) return 'none'
    const c = line.getCell(${col})
    return c ? c.getFgColorMode() + ':' + c.getFgColor() : 'none'
  })()`)
}

// Every viewport position where the fixture byline renders, reading order. The
// story grid can be one column or several (workspace-h2caq), so the meta checks
// compare the SELECTED card's byline against the next card's wherever it sits.
function bylineHits (vp) {
  const hits = []
  for (let r = 0; r < vp.lines.length; r++) {
    const line = vp.lines[r] || ''
    for (let idx = line.indexOf(AUTHOR); idx >= 0; idx = line.indexOf(AUTHOR, idx + 1)) {
      hits.push({ row: r, col: idx })
    }
  }
  return hits
}

// Which story wears the selection right now, by the FOREGROUND colour of its
// headline (`selFg` = the selected card's title fg, sampled while the selection is
// known). Reading the background instead does not survive a single-column grid: the
// fill then covers most of the row, so it becomes the row's dominant background and
// selectedColumn's widest-non-bg run inverts onto the gaps.
async function selectedStory (term, vp, selFg) {
  for (const w of WORDS) {
    const r = vp.lines.findIndex(l => l.includes(`Dispatch ${w}`))
    if (r < 0) continue
    const col = vp.lines[r].indexOf(`Dispatch ${w}`)
    if ((await fgAt(term, vp.baseY, r, col)) === selFg) return w
  }
  return null
}

// Waits until the story grid has SETTLED at `expCols` columns — a dock/undock
// narrows or widens the pty and the re-render arrives asynchronously; until it
// does, the buffer still holds the previous width's render WRAPPED by the new
// terminal width (visible as mid-word fragments). Polling for the settled shape
// is the honest wait; the timeout failing means the resize re-render never came.
async function waitStoryCols (term, expCols, timeout = 12000) {
  const names = ['one', 'two', 'three', 'four', 'five']
  const t0 = Date.now()
  let vp = null
  while (Date.now() - t0 < timeout) {
    vp = await viewport(term)
    const rowOne = vp.lines.findIndex(l => l.includes('Dispatch one'))
    if (rowOne >= 0) {
      const line = vp.lines[rowOne]
      const onRow = names.slice(1, expCols).every(w => line.includes(`Dispatch ${w}`))
      const nextName = `Dispatch ${names[expCols]}`
      const rowNext = vp.lines.findIndex(l => l.includes(nextName))
      if (onRow && !line.includes(nextName) && rowNext > rowOne) return vp
    }
    await sleep(500)
  }
  return vp
}

// Asserts the story grid renders exactly `expCols` columns and titles wrap, in the
// CURRENT viewport. Since workspace-vingi the SHELL-mode story grid follows the HTML
// link-tree pane's responsive track count (LinkTreeRenderer.ShellPaneColumns); since
// workspace-h2caq that track is "Link Tree.dc.html"'s roomier clamp(420px,40vw,700px),
// so the docked ~58% split at 1360px is ONE wide column and the full 1360px window
// two. The fixed-2 contract (workspace-ehon) still governs plain-terminal mode only.
async function measureStoryGrid (term, label, expCols = 2) {
  const vp = await viewport(term)
  const rowOne = vp.lines.findIndex(l => l.includes('Dispatch one'))
  check(`${label}: story list on screen`, rowOne >= 0)
  if (rowOne < 0) return vp

  // Exactly expCols columns: Dispatches one..expCols share the first card row;
  // the next one does NOT.
  const line = vp.lines[rowOne]
  const names = ['one', 'two', 'three', 'four', 'five']
  const onRow = names.slice(1, expCols).every(w => line.includes(`Dispatch ${w}`))
  check(`${label}: ${expCols === 1 ? 'Dispatch one holds the card row alone' : `Dispatches 1..${expCols} share a row`} (${expCols} column${expCols === 1 ? '' : 's'})`,
    onRow, `line="${line.replace(/\s+/g, ' ').trim().slice(0, 110)}"`)
  const nextName = `Dispatch ${names[expCols]}`
  check(`${label}: ${nextName} is NOT on that row (not ${expCols + 1}+ columns)`,
    !line.includes(nextName), `line="${line.replace(/\s+/g, ' ').trim().slice(0, 110)}"`)
  const rowNext = vp.lines.findIndex(l => l.includes(nextName))
  check(`${label}: ${nextName} wrapped to the next tile row (grid did not collapse to 1 col)`,
    rowNext > rowOne, `rowOne=${rowOne} rowNext=${rowNext}`)

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
    await waitStoryCols(term, 1)

    const vpDocked = await measureStoryGrid(term, 'docked', 1)
    env.shot('columns-story-docked')

    // Move the selection to the NEXT story — the state CHANGE the doctrine demands —
    // then toggle the pane away and back and require the selection to hold.
    const rowOne = vpDocked.lines.findIndex(l => l.includes('Dispatch one'))
    const dividerCols = []
    for (let c = 0; c < (vpDocked.lines[rowOne] || '').length; c++) if (vpDocked.lines[rowOne][c] === '│') dividerCols.push(c)
    check('story: the docked split is a SINGLE wide column (no card divider)',
      dividerCols.length === 0, `dividers=${dividerCols.length}`)
    // The tree boots with Dispatch one selected — sample its title fg once and use it
    // as the "this card is selected" signature for the rest of the run.
    const selTitleFg = await fgAt(term, vpDocked.baseY, rowOne, vpDocked.lines[rowOne].indexOf('Dispatch one'))

    // Meta white-on-fill (workspace-v654): the byline renders under EVERY card, the
    // SELECTED card's meta wears the selection foreground (same fg as its selected
    // title — the design's white-on-fill), the others stay metadata green.
    const hits = bylineHits(vpDocked)
    check('story-meta: author/date meta renders under the docked cards', hits.length >= 2,
      `byline hits=${JSON.stringify(hits.slice(0, 3))}`)
    let selMetaFg = 'none', unselMetaFg = 'none'
    if (hits.length >= 2) {
      selMetaFg = await fgAt(term, vpDocked.baseY, hits[0].row, hits[0].col)
      unselMetaFg = await fgAt(term, vpDocked.baseY, hits[1].row, hits[1].col)
      check('story-meta: selected card meta is white-on-fill (fg == selected title fg)',
        selMetaFg !== 'none' && selMetaFg === selTitleFg, `metaFg=${selMetaFg} titleFg=${selTitleFg}`)
      check('story-meta: unselected card meta does NOT wear the selection fg',
        unselMetaFg !== selMetaFg, `unselMetaFg=${unselMetaFg} selMetaFg=${selMetaFg}`)
    }
    env.shot('columns-story-meta')

    env.key('Down'); await sleep(700)
    const vpMoved = await viewport(term)
    check('story: Down moves the selection to the next story while docked',
      (await selectedStory(term, vpMoved, selTitleFg)) === 'two', `selected=${await selectedStory(term, vpMoved, selTitleFg)}`)

    // The state CHANGE, not just the initial state: after Down the white-on-fill
    // must have MOVED — card 2's meta gains the selection fg, card 1's reverts.
    if (hits.length >= 2) {
      const hits2 = bylineHits(vpMoved)
      const fg0 = hits2[0] ? await fgAt(term, vpMoved.baseY, hits2[0].row, hits2[0].col) : 'none'
      const fg1 = hits2[1] ? await fgAt(term, vpMoved.baseY, hits2[1].row, hits2[1].col) : 'none'
      check('story-meta: white-on-fill moved with the selection (card 2 gained it, card 1 dropped it)',
        fg1 === selMetaFg && fg0 === unselMetaFg,
        `after Down: card1Fg=${fg0} card2Fg=${fg1} (selFg=${selMetaFg} normalFg=${unselMetaFg})`)
      env.shot('columns-story-meta-moved')
    }

    // Undock (| hides the pane → reader reflows to FULL width): the responsive grid
    // opens out to 2 columns at 1360px, selection still on Dispatch two.
    env.key('bar'); await sleep(500)
    let stHid = null
    for (let i = 0; i < 20; i++) { stHid = await term.eval('window.__wc.state()'); if (!stHid?.revealed) break; await sleep(400) }
    check('story: | undocks the pane', stHid?.revealed === false)
    await waitStoryCols(term, 2)
    const vpFull = await measureStoryGrid(term, 'undocked-full-width', 2)
    const rowOneF = vpFull.lines.findIndex(l => l.includes('Dispatch one'))
    const divF = []
    for (let c = 0; c < (vpFull.lines[rowOneF] || '').length; c++) if (vpFull.lines[rowOneF][c] === '│') divF.push(c)
    check('story: undock opened the grid out to 2 columns (1 divider on the card row)',
      divF.length === 1 && dividerCols.length === 0,
      `docked dividers=${dividerCols.length} undocked dividers=${divF.length}`)
    check('story: selection survived undock on the same story',
      (await selectedStory(term, vpFull, selTitleFg)) === 'two', `selected=${await selectedStory(term, vpFull, selTitleFg)}`)
    // Two columns is where the horizontal keys have something to do: Left steps back
    // to Dispatch one, Right returns — the fill MOVES with the key both ways.
    const cFull = (await selectedColumn(term, vpFull.baseY, rowOneF, divF)).col
    env.key('Left'); await sleep(700)
    const vpLeft = await viewport(term)
    const cLeft = (await selectedColumn(term, vpLeft.baseY, vpLeft.lines.findIndex(l => l.includes('Dispatch one')), divF)).col
    check('story: Left steps the selection to the adjacent card (column 1 → 0)',
      cFull === 1 && cLeft === 0 && (await selectedStory(term, vpLeft, selTitleFg)) === 'one',
      `cols=[${cFull},${cLeft}] selected=${await selectedStory(term, vpLeft, selTitleFg)}`)
    env.key('Right'); await sleep(700)
    const vpRight = await viewport(term)
    check('story: Right steps it back (column 0 → 1)',
      (await selectedStory(term, vpRight, selTitleFg)) === 'two', `selected=${await selectedStory(term, vpRight, selTitleFg)}`)
    env.shot('columns-story-undocked')

    // Re-dock: back to 1 column, selection still held.
    env.key('bar'); await sleep(500)
    let stBack = null
    for (let i = 0; i < 30; i++) { stBack = await term.eval('window.__wc.state()'); if (stBack?.revealed) break; await sleep(500) }
    check('story: | re-docks the pane', !!stBack?.revealed)
    await waitStoryCols(term, 1)
    const vpRe = await measureStoryGrid(term, 'redocked', 1)
    check('story: selection survived re-dock on the same story',
      (await selectedStory(term, vpRe, selTitleFg)) === 'two', `selected=${await selectedStory(term, vpRe, selTitleFg)}`)
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
